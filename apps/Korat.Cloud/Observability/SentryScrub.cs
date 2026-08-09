using System.Text.RegularExpressions;
using Orleans.Runtime;
using Sentry;

namespace Korat.Cloud.Observability;

/// <summary>
/// Scrubs secrets/PII from Sentry events before they leave the cloud process
/// Wired as the <c>BeforeSend</c> / <c>BeforeBreadcrumb</c> callbacks in
/// Program.cs. Internal + statically testable so the redaction can be verified
/// without a live Sentry connection (the CLI shipped a "wired but never scrubs"
/// regression once — this surface has a unit test to prevent the same).
///
/// <para>Cloud is multi-tenant: an unhandled exception's <i>message</i> can carry
/// another user's email or a token, which request-header scrubbing never touched.
/// So we scrub the message/exception/breadcrumb text too, not just request data.</para>
/// </summary>
internal static partial class SentryScrub
{
    private static readonly string[] SecretHeaders =
        ["Authorization", "X-Korat-Owner-Token", "Cookie", "Set-Cookie"];

    // token=/code=/invite=/cli_token=/Bearer <x> — keep the label, drop the value.
    [GeneratedRegex(@"(Bearer\s+|(?:cli_)?token[=:]\s*|code[=:]\s*|invite[=:]\s*)[^\s""'&;,]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    // Fable review follow-up (#185 MEDIUM-2): bare OpenAI (sk-…) / Anthropic (sk-ant-…) shaped
    // API keys — TokenRegex above only matches a Bearer/token=/code=/invite= PREFIX. An upstream
    // inference-point error body can echo a raw key with no such prefix (e.g. "invalid key
    // sk-ant-...") and ride a log line (Warning breadcrumb or Error event) straight through to
    // GlitchTip. Mirrors AgentRuntime.ApiKeyRegex, kept here too so ANY text surface reaching
    // Sentry gets this net, not just AgentRuntime's own LastTurnError.
    [GeneratedRegex(@"\bsk-(?:ant-)?[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    // Telegram Bot API token embedded in the URL PATH (api.telegram.org/bot{id}:{secret}/method)
    // — PR-2 Task 9 review fix (defence-in-depth): the prefix-based TokenRegex above and .NET's
    // query-string-only redaction never match a path-embedded token. Primary control is
    // RemoveAllLoggers() on the "telegram-bot" HttpClient (Program.cs); this catches any other
    // surface (exception messages, breadcrumbs from custom log lines).
    [GeneratedRegex(@"bot\d+:[\w\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BotTokenRegex();

    // Bare DSN: scheme://publicKey[:secret]@host/projectId.
    [GeneratedRegex(@"https?://[^@\s/]+@[^\s/]+/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex DsnRegex();

    // ADO.NET-style connection-string secrets (Password=, Pwd=).
    [GeneratedRegex(@"(Password|Pwd)\s*=\s*[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnStringSecretRegex();

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    /// <summary>Redacts secrets/PII from free text (message / exception / breadcrumb).</summary>
    internal static string ScrubText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = TokenRegex().Replace(value, m => m.Groups[1].Value + "<redacted>");
        value = ApiKeyRegex().Replace(value, "sk-<redacted>");
        value = BotTokenRegex().Replace(value, "bot<redacted>");
        value = DsnRegex().Replace(value, "<dsn-redacted>");
        value = ConnStringSecretRegex().Replace(value, m => m.Groups[1].Value + "=<redacted>");
        value = EmailRegex().Replace(value, "<email-redacted>");
        return value;
    }

    // Connectivity-check exception is Orleans.Runtime-internal; match by full name to avoid
    // taking a hard reference to an internal type that may change access modifier between minor
    // Orleans releases.
    private const string ConnectivityCheckFullName =
        "Orleans.Runtime.OrleansClusterConnectivityCheckFailedException";

    // Orleans MembershipService's join-retry progress log — see IsOrleansJoinRetryNoise below.
    [GeneratedRegex(@"Attempt #(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex OrleansJoinAttemptRegex();

    /// <summary>
    /// Walks the CLR exception chain (including <see cref="AggregateException.InnerExceptions"/>)
    /// and returns true if any node is a known transient cluster-membership exception produced
    /// during Orleans rolling deploys/restarts.  These exceptions are expected, self-healing, and
    /// not actionable — dropping them keeps GlitchTip clear of deploy-time noise.
    /// </summary>
    internal static bool IsTransientClusterNoise(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is SiloUnavailableException
            || ex is OrleansMessageRejectionException
            || ex.GetType().FullName == ConnectivityCheckFullName)
        {
            return true;
        }
        // AggregateException wraps multiple concurrent failures — check all inner exceptions.
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                if (IsTransientClusterNoise(inner)) return true;
        }
        // Walk the standard InnerException chain (handles wrapped task exceptions etc.).
        return IsTransientClusterNoise(ex.InnerException);
    }

    /// <summary>
    /// Walks the CLR exception chain and returns true if any node is an
    /// <see cref="OperationCanceledException"/> (which covers <see cref="TaskCanceledException"/>).
    /// On the server these almost always mean the request was aborted (client disconnected
    /// mid-request) or the host is shutting down — ASP.NET itself treats them as non-errors.
    /// Dropping them keeps GlitchTip clear of request-cancellation noise, e.g. an aborted
    /// GET /api/space cancelling an in-flight Npgsql connection open (korat-cloud #335/#210).
    /// </summary>
    internal static bool IsCancellationNoise(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is OperationCanceledException) return true;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                if (IsCancellationNoise(inner)) return true;
        }
        return IsCancellationNoise(ex.InnerException);
    }

    /// <summary>
    /// Returns true iff <paramref name="message"/> is Orleans MembershipService's join-retry
    /// progress log ("Failed to get ping responses from {FailedCount} of {ActiveCount} active
    /// silos. Newly joining silos validate connectivity ... Will continue attempting to
    /// validate connectivity until {Timeout}. Attempt #{Attempt}.") on an early attempt
    /// (1-5). Every rolling deploy logs this a handful of times while the joining silo warms
    /// up — expected, self-healing noise, not actionable.
    ///
    /// Deliberately narrow: requires BOTH the exact leading phrase and the "Will continue
    /// attempting" marker, and only matches when an "Attempt #&lt;N&gt;" with N ≤ 5 can be
    /// parsed. A genuine failure surfaces later as a different terminal message (e.g. the
    /// connectivity-check-failed exception handled elsewhere in this file) or as this same
    /// log line past attempt 5 — both are kept so a real broken silo still alerts
    /// generated by repeated cluster-join attempts.
    /// </summary>
    internal static bool IsOrleansJoinRetryNoise(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (!message.StartsWith("Failed to get ping responses from", StringComparison.Ordinal)) return false;
        if (!message.Contains("Will continue attempting", StringComparison.Ordinal)) return false;

        var match = OrleansJoinAttemptRegex().Match(message);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var attempt)) return false;

        return attempt <= 5;
    }

    /// <summary>The <c>BeforeSend</c> callback: scrub request data + all text surfaces.</summary>
    internal static SentryEvent? ScrubEvent(SentryEvent ev)
    {
        // Drop expected rolling-deploy cluster-membership churn via the live CLR exception (when
        // Sentry captures it as an unhandled exception the CLR object is available here).
        if (IsTransientClusterNoise(ev.Exception)) return null;

        // Drop request-abort / shutdown cancellations (client disconnected mid-request, host
        // stopping, …) — ASP.NET treats these as non-errors and they are not actionable.
        if (IsCancellationNoise(ev.Exception)) return null;

        // Drop Orleans MembershipService's join-retry progress noise — logged
        // via ILogger (no exception object), so check both the rendered text and the raw
        // logentry template (either can carry the leading phrase).
        if (ev.Message is { } joinRetryMsg &&
            (IsOrleansJoinRetryNoise(joinRetryMsg.Formatted) || IsOrleansJoinRetryNoise(joinRetryMsg.Message)))
        {
            return null;
        }

        // Instance hostname identifies the silo — drop it.
        ev.ServerName = null;

        if (ev.Request is { } req)
        {
            foreach (var h in SecretHeaders)
                req.Headers?.Remove(h);

            if (req.QueryString is { } qs
                && (qs.Contains("token=", StringComparison.OrdinalIgnoreCase)
                    || qs.Contains("code=", StringComparison.OrdinalIgnoreCase)
                    || qs.Contains("invite=", StringComparison.OrdinalIgnoreCase)))
            {
                req.QueryString = "[redacted]";
            }
        }

        if (ev.Message is { } msg)
        {
            if (msg.Message is { } m) msg.Message = ScrubText(m);
            if (msg.Formatted is { } f) msg.Formatted = ScrubText(f);
        }

        if (ev.SentryExceptions is { } exceptions)
        {
            foreach (var ex in exceptions)
            {
                if (ex.Value is { } v) ex.Value = ScrubText(v);
            }
        }

        // Transient cluster-membership churn during rolling deploys/restarts (a silo briefly
        // leaving the cluster) surfaces as OrleansMessageRejectionException / SiloUnavailableException
        // / OrleansClusterConnectivityCheckFailedException — expected, self-healing (grain ref stays
        // valid; Orleans reactivates; caller retries) and not actionable per-event. Drop so deploy-time
        // spikes don't create issues. Matches both the Sentry protocol exception list AND the relay's
        // "errorType=..." log message (which carries no exception object when logged via structured
        // logging). The CLR-level chain is already checked above via IsTransientClusterNoise(ev.Exception)
        // for unhandled-exception captures; this catches the same types when they reach Sentry via
        // manual capture or the relay log path. See .github/workflows/DEPLOY.md "Orleans cluster update".
        static bool IsTransientClusterChurn(SentryEvent e)
        {
            const string m1 = "OrleansMessageRejectionException";
            const string m2 = "SiloUnavailableException";
            const string m3 = "OrleansClusterConnectivityCheckFailedException";
            if (e.SentryExceptions is { } exs)
                foreach (var ex in exs)
                    if (ex.Type is { } t &&
                        (t.Contains(m1, StringComparison.Ordinal)
                         || t.Contains(m2, StringComparison.Ordinal)
                         || t.Contains(m3, StringComparison.Ordinal)))
                        return true;
            var text = e.Message?.Formatted ?? e.Message?.Message;
            return text is { } s &&
                (s.Contains(m1, StringComparison.Ordinal)
                 || s.Contains(m2, StringComparison.Ordinal)
                 || s.Contains(m3, StringComparison.Ordinal));
        }
        if (IsTransientClusterChurn(ev)) return null;

        // Breadcrumbs are immutable here too — scrubbed at capture via ScrubBreadcrumb.
        return ev;
    }

    /// <summary>The <c>BeforeBreadcrumb</c> callback: rebuild the (immutable) breadcrumb scrubbed.</summary>
    internal static Breadcrumb? ScrubBreadcrumb(Breadcrumb crumb)
    {
        var message = crumb.Message is { } m ? ScrubText(m) : string.Empty;
        var data = crumb.Data is { Count: > 0 } d
            ? d.ToDictionary(kv => kv.Key, kv => ScrubText(kv.Value))
            : crumb.Data;
        return new Breadcrumb(message, crumb.Type ?? "default", data, crumb.Category, crumb.Level);
    }
}
