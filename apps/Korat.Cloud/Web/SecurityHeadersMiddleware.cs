namespace Korat.Cloud.Web;

/// <summary>
/// SEC-MED-1: emit baseline security response headers on every response.
///
/// The CSP keeps <c>'unsafe-inline'</c> for <c>script-src</c> and <c>style-src</c> because the
/// approve/index HTML pages currently include inline &lt;script&gt; and &lt;style&gt; blocks. A follow-on
/// extracts those to external .js / .css files and switches to nonce-based CSP, which removes
/// the only remaining XSS-mitigation gap on the static UI.
/// </summary>
internal static class SecurityHeadersMiddleware
{
    /// <summary>
    /// The OAuth consent page's form (GET/POST /connect/authorize) is 302-redirected by OpenIddict
    /// to the CLIENT's registered callback — a loopback URL (native clients: Claude Code, Cursor,
    /// Codex bind http://127.0.0.1|localhost|[::1]:&lt;ephemeral-port&gt;) or an exact-match https URL
    /// (hosted clients). Chrome enforces <c>form-action</c> against the REDIRECT target of a form
    /// submission, so the baseline <c>form-action 'self'</c> silently BLOCKS the callback ("Refused
    /// to load http://localhost:&lt;port&gt;/callback … does not appear in the form-action directive")
    /// — the auth code never reaches the client's loopback listener, which manifests as the
    /// "click Allow several times" bug. Widen form-action for THIS PATH ONLY to the callback
    /// schemes. This relaxes only the redundant browser layer: the real redirect target is still
    /// constrained server-side by DcrRedirectUriPolicy (registration) + OpenIddict exact-match
    /// (authorize), so a rogue https/loopback origin still cannot receive a code.
    ///
    /// NOTE on <c>http://[::1]:*</c>: the CSP3 host-source grammar (host-char = ALPHA / DIGIT / "-")
    /// cannot express an IPv6 literal — brackets/colons are unrepresentable — so Chrome parses this
    /// token as invalid and silently drops it (fail-closed: the rest of the directive still applies).
    /// It is therefore INERT in browsers today and kept only as forward-compat if the spec ever gains
    /// IPv6 host-sources. Real native clients bind <c>localhost</c>/<c>127.0.0.1</c> (both covered),
    /// so an [::1] callback would still hit the Chrome block; no clean CSP fix exists for that case.
    /// </summary>
    private const string OAuthFormAction =
        "form-action 'self' http://127.0.0.1:* http://localhost:* http://[::1]:* https:";

    public static IApplicationBuilder UseKoratSecurityHeaders(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var telemetryOrigin = ResolveTelemetryOrigin(
            configuration["Korat:Web:TelemetryOrigin"],
            configuration["SENTRY_DSN"]);
        var cspBase = BuildCspBase(telemetryOrigin);
        var cspPolicy = cspBase + "form-action 'self'";
        var cspPolicyOAuthAuthorize = cspBase + OAuthFormAction;

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            // Use indexer so we never end up with duplicate values across middleware passes.
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
            headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), microphone=(), payment=()";
            headers["Content-Security-Policy"] =
                context.Request.Path.StartsWithSegments("/connect/authorize", StringComparison.OrdinalIgnoreCase)
                    ? cspPolicyOAuthAuthorize
                    : cspPolicy;
            await next();
        });
    }

    internal static string? ResolveTelemetryOrigin(string? explicitOrigin, string? sentryDsn)
    {
        foreach (var candidate in new[] { explicitOrigin, sentryDsn })
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                continue;
            }

            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return $"{Uri.UriSchemeHttps}://{authority}";
        }

        return null;
    }

    private static string BuildCspBase(string? telemetryOrigin)
    {
        var connectSources = telemetryOrigin is null
            ? "'self'"
            : $"'self' {telemetryOrigin}";

        return
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; " +
            $"connect-src {connectSources}; " +
            "object-src 'none'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; ";
    }
}
