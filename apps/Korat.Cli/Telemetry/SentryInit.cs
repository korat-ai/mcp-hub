using System.Reflection;
using System.Text.RegularExpressions;
using Sentry;

namespace Korat.Cli.Telemetry;

/// <summary>
/// Initialises the Sentry SDK for the Korat CLI.
///
/// <para>Rules from the repository privacy policy:</para>
/// <list type="bullet">
///   <item>Init ONLY when a DSN is available AND <c>KORAT_TELEMETRY=0</c> is NOT set.</item>
///   <item>DSN resolution order: <c>KORAT_SENTRY_DSN</c> env → build-baked <see cref="BakedDsn"/>.</item>
///   <item><c>SendDefaultPii = false</c> — no username/email attached.</item>
///   <item><c>TracesSampleRate = 0</c> — errors only, no perf tracing.</item>
///   <item><c>BeforeSend</c> scrubs: $HOME paths, tokens/DSNs, emails, server name.</item>
///   <item>Opt-out: <c>KORAT_TELEMETRY=0</c> env → SDK never initialised.</item>
/// </list>
///
/// <para>
/// <c>BakedDsn</c> is declared in the MSBuild-generated partial class
/// <c>obj/SentryBakedDsn.g.cs</c> (target <c>GenerateSentryDsnConstant</c>).
/// When the <c>KoratSentryDsn</c> MSBuild property is absent, <c>BakedDsn</c> is
/// an empty string → SDK is a no-op.
/// </para>
/// </summary>
internal static partial class SentryInit
{
    // BakedDsn is in the generated partial: obj/SentryBakedDsn.g.cs
    // internal const string BakedDsn = "<value baked at publish time, or empty>";

    /// <summary>
    /// Call once at startup, before command dispatch.
    /// Returns an <see cref="IDisposable"/> that cleans up the SDK; always also call
    /// <see cref="SentrySdk.FlushAsync"/> before process exit to avoid losing events.
    /// Returns <see langword="null"/> when the SDK is not initialised (no-op path).
    /// </summary>
    public static IDisposable? TryInit(string? nodeId = null)
    {
        // Opt-out: KORAT_TELEMETRY=0 → skip init entirely.
        if (Environment.GetEnvironmentVariable("KORAT_TELEMETRY") == "0")
            return null;

        // DSN resolution: env first, then build-baked constant.
        var dsn = Environment.GetEnvironmentVariable("KORAT_SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(dsn))
            dsn = BakedDsn;
        if (string.IsNullOrWhiteSpace(dsn))
            return null; // No DSN available — SDK stays a no-op.

        // Release: "korat@<InformationalVersion>" — same string as `korat version`.
        var infoVersion = typeof(SentryInit).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev+unknown";
        var release = "korat@" + infoVersion;

        var environment = Environment.GetEnvironmentVariable("KORAT_SENTRY_ENVIRONMENT")
                          ?? "production";

        return SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Release = release;
            o.Environment = environment;
            o.TracesSampleRate = 0;
            o.SendDefaultPii = false;
            o.IsGlobalModeEnabled = true;

            // Attach anonymous node ID (non-PII) when available.
            if (!string.IsNullOrWhiteSpace(nodeId))
                o.DefaultTags["node_id"] = nodeId;

            o.SetBeforeSend((ev, _) => ScrubEvent(ev));
            o.SetBeforeBreadcrumb(ScrubBreadcrumb);
        });
    }

    // ---- Scrubber -------------------------------------------------------

    private static readonly Lazy<string> _homeDir = new(() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // Redacts bearer/API token patterns: "Bearer <tok>", "token=<tok>",
    // env-var assignments (KORAT_SENTRY_DSN=..., cli_token=...), and
    // MCP launch args that might carry secrets.
    private static readonly Regex _tokenRe = new(
        @"(Bearer\s+|token[=:]\s*|KORAT_SENTRY_DSN[=:]\s*|cli_token[=:]\s*)[^\s""'&;,]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _emailRe = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    // A bare DSN (scheme://<publicKey>[:secret]@host/<projectId>). The token regex
    // only catches a DSN prefixed by KORAT_SENTRY_DSN=; this catches one that
    // surfaces on its own — e.g. an SDK init error echoing the configured DSN.
    private static readonly Regex _dsnRe = new(
        @"https?://[^@\s/]+@[^\s/]+/\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---- Transient-transport noise filter --------------------------------

    // Leaf exception types that, inside an UnobservedTaskException, mean "a
    // long-lived connection dropped" rather than a real bug.
    private static readonly HashSet<string> _benignLeafTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Sockets.SocketException",
        "System.IO.IOException",
        "System.OperationCanceledException",
        "System.Threading.Tasks.TaskCanceledException",
        "Grpc.Core.RpcException",
    };

    // gRPC statuses that mean the stream/connection went away (expected on a node
    // relay teardown / network blip). A real RpcException (PermissionDenied,
    // Unauthenticated, …) carries a different status and is kept.
    private static readonly string[] _transientRpcStatuses =
        { "Unavailable", "Cancelled", "DeadlineExceeded" };

    /// <summary>
    /// True when <paramref name="ev"/> is an <c>UnobservedTaskException</c> whose
    /// leaves are ALL benign connection-teardown faults: gRPC Unavailable/Cancelled,
    /// socket timeout, transport IO abort, or cancellation. These fire when a node's
    /// long-lived relay stream drops and the in-flight HTTP/2 read on the disposed
    /// gRPC call faults on a task nobody awaited (Grpc.Net.Client teardown). The CLI
    /// reconnects fine — capturing them only floods error tracking.
    /// Returns <see langword="false"/> (keep) for any non-transient leaf or a
    /// non-transient RpcException status, so real bugs and real gRPC errors surface.
    /// </summary>
    internal static bool IsTransientTransportNoise(SentryEvent ev)
    {
        var exceptions = ev.SentryExceptions?.ToList();
        if (exceptions is null || exceptions.Count == 0)
            return false;

        // Unobserved-task exceptions are always wrapped by TaskScheduler in an
        // AggregateException; require it so we only ever drop that specific shape.
        if (!exceptions.Any(e => e.Type == "System.AggregateException"))
            return false;

        var leaves = exceptions.Where(e => e.Type != "System.AggregateException").ToList();
        if (leaves.Count == 0)
            return false;

        var sawTransport = false;
        foreach (var e in leaves)
        {
            if (e.Type is null || !_benignLeafTypes.Contains(e.Type))
                return false; // a non-transient leaf → real signal, keep the event

            if (e.Type == "Grpc.Core.RpcException")
            {
                // Anchor on the STATUS field, not a bare substring of the whole value:
                // RpcException.Value is `Status(StatusCode="X", Detail="…")`, and the
                // server-supplied Detail could itself contain a transient word (e.g. a
                // real PermissionDenied with Detail="…Cancelled by policy"). Matching
                // `StatusCode="X"` keeps such real errors instead of silently dropping them.
                var value = e.Value ?? string.Empty;
                if (!_transientRpcStatuses.Any(s => value.Contains($"StatusCode=\"{s}\"", StringComparison.Ordinal)))
                    return false; // RpcException with a non-transient status → keep
                sawTransport = true;
            }
            else if (e.Type is "System.Net.Sockets.SocketException" or "System.IO.IOException")
            {
                sawTransport = true;
            }
        }

        return sawTransport;
    }

    /// <summary>
    /// The <see cref="SentrySdk.Init"/> <c>BeforeSend</c> callback. Scrubs every
    /// text surface of the event in place and returns it. Internal so a test can
    /// feed it a synthesized event and assert the payload comes out redacted —
    /// this is the regression guard for "BeforeSend wired but never scrubs".
    /// </summary>
    internal static SentryEvent? ScrubEvent(SentryEvent ev)
    {
        // Drop benign connection-teardown noise (a node's relay stream dropping and
        // surfacing as an unobserved-task exception) before doing any work.
        if (IsTransientTransportNoise(ev))
            return null;

        // Hostname may identify the user's machine.
        ev.ServerName = null;

        // Run ScrubString over EVERY text surface the event actually ships —
        // these are the fields that can carry $HOME paths, tokens, DSNs, or
        // emails. Without this BeforeSend would be a no-op and the CLI would
        // leak. All of these properties are writable (verified against the SDK).
        if (ev.Message is { } msg)
        {
            if (msg.Message is { } m) msg.Message = ScrubString(m);
            if (msg.Formatted is { } f) msg.Formatted = ScrubString(f);
        }

        if (ev.SentryExceptions is { } exceptions)
        {
            foreach (var ex in exceptions)
            {
                if (ex.Value is { } v) ex.Value = ScrubString(v);
                // Stack frame paths are usually null in a trimmed/no-PDB publish,
                // but scrub them defensively when present (real $HOME leak vector).
                if (ex.Stacktrace?.Frames is { } frames)
                {
                    foreach (var fr in frames)
                    {
                        if (fr.FileName is { } fn) fr.FileName = ScrubString(fn);
                        if (fr.AbsolutePath is { } ap) fr.AbsolutePath = ScrubString(ap);
                    }
                }
            }
        }

        // Breadcrumbs are immutable (init-only message/data), so they CANNOT be
        // scrubbed here — they are scrubbed at capture time via SetBeforeBreadcrumb
        // (see TryInit / ScrubBreadcrumb), which rebuilds each one redacted.

        return ev;
    }

    /// <summary>
    /// The <c>BeforeBreadcrumb</c> callback. <see cref="Breadcrumb"/> is immutable,
    /// so this rebuilds a new breadcrumb with its message + string data scrubbed.
    /// Returns <see langword="null"/> to drop the breadcrumb entirely. Internal for
    /// unit testing.
    /// </summary>
    internal static Breadcrumb? ScrubBreadcrumb(Breadcrumb crumb)
    {
        var message = crumb.Message is { } m ? ScrubString(m) : string.Empty;
        var data = crumb.Data is { Count: > 0 } d
            ? d.ToDictionary(kv => kv.Key, kv => ScrubString(kv.Value))
            : crumb.Data;
        return new Breadcrumb(message, crumb.Type ?? "default", data, crumb.Category, crumb.Level);
    }

    /// <summary>
    /// Visible internally so tests can verify the scrub logic without
    /// needing a real Sentry connection.
    /// </summary>
    internal static string ScrubString(string value, string? home = null)
    {
        if (string.IsNullOrEmpty(value)) return value;
        home ??= _homeDir.Value;

        // Replace $HOME paths with ~.
        if (!string.IsNullOrEmpty(home))
            value = value.Replace(home, "~", StringComparison.Ordinal);

        // Redact credentials file content marker.
        value = value.Replace("~/.korat/credentials", "<credentials-redacted>",
            StringComparison.Ordinal);

        // Redact tokens / DSN values.
        value = _tokenRe.Replace(value, m => m.Groups[1].Value + "<redacted>");

        // Redact bare DSNs (publicKey@host/projectId).
        value = _dsnRe.Replace(value, "<dsn-redacted>");

        // Redact email addresses.
        value = _emailRe.Replace(value, "<email-redacted>");

        return value;
    }
}
