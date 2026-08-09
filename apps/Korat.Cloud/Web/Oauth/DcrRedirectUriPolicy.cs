namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2b, Task 3: the redirect-URI allow policy for open DCR — the load-bearing
/// anti-open-redirect gate. Applied at registration time so a rejected URI is NEVER persisted;
/// OpenIddict then enforces exact-match against the persisted set at authorize time, so the two
/// layers compose (registration decides WHICH strings are registrable; OpenIddict decides that
/// the authorize request's redirect_uri exactly equals one of them).
///
/// Allowed:
///   • RFC 8252 loopback: http://127.0.0.1[:port][/path], http://[::1][:port][/path], and
///     http://localhost[:port][/path] — any port (native clients bind an ephemeral loopback
///     port). The real MCP OAuth clients (Claude Code, Cursor — both on the MCP TypeScript SDK)
///     register http://localhost:&lt;port&gt;, so it MUST be accepted; see the http branch below
///     for why it is safe (RFC 6761 special-use name, exact host match).
///   • Exact-match https://… (any host) — Claude and other hosted callbacks.
/// Rejected (everything else): http non-loopback (0.0.0.0, LAN IPs, "localhost.evil.com", …),
///   any non-http(s) scheme, wildcards, fragments, userinfo, relative/unparseable.
/// </summary>
public static class DcrRedirectUriPolicy
{
    /// <summary>Returns <c>null</c> when <paramref name="value"/> is an allowed redirect URI,
    /// otherwise a human-readable rejection reason (surfaced as invalid_redirect_uri).</summary>
    public static string? Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "redirect_uri must not be empty.";
        // A wildcard is never a legitimate redirect target; reject before parsing (some wildcard
        // shapes parse into an unexpected host/path).
        if (value.Contains('*', StringComparison.Ordinal))
            return "redirect_uri must not contain wildcards.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return "redirect_uri must be an absolute URI.";
        if (!string.IsNullOrEmpty(uri.Fragment))
            return "redirect_uri must not contain a fragment.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "redirect_uri must not contain userinfo.";

        // Exact-match https (the whole string is registered; OpenIddict enforces exact equality
        // at authorize time). Any https host is acceptable — a rogue https origin still cannot
        // receive a code without also passing consent (owner-owns-Space) and PKCE.
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return null;

        // RFC 8252 loopback: http ONLY to a loopback host, any port/path. .NET's Uri.Host yields
        // "127.0.0.1", "[::1]" (brackets retained), and a lowercased "localhost" for these
        // (verified). "localhost" is accepted because the real-world MCP OAuth clients (Claude
        // Code, Cursor — both on the MCP TypeScript SDK) bind their loopback callback as
        // http://localhost:<port>, NOT the IP literal; rejecting it broke the actual clients even
        // though the IP forms passed every unit test. It is safe: RFC 6761 §6.3 makes "localhost"
        // a special-use name that ALWAYS resolves to the loopback interface and is never sent to
        // DNS, so the DNS-rebinding concern RFC 8252 §8.3 raises does not apply on a conformant
        // host; and the match is EXACT on the lowercased Uri.Host. "localhost.evil.com",
        // "notlocalhost", and Unicode-homoglyph forms (e.g. Cyrillic-о "localhоst") have a
        // different Host and stay rejected; "localhost@evil.com" has Host "evil.com" AND is caught
        // by the userinfo guard above (Host is a component separate from userinfo — the guard, not
        // the host string, is what rejects the user@host bait). This still rejects http://0.0.0.0,
        // http://127.0.0.1.evil.com, LAN IPs, and every other non-loopback http host by construction.
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            if (uri.Host is "127.0.0.1" or "[::1]" or "localhost")
                return null;
            return "http redirect URIs are allowed only for RFC 8252 loopback (http://127.0.0.1, http://[::1], or http://localhost).";
        }

        return "redirect_uri scheme must be https or http loopback.";
    }
}
