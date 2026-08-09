namespace Korat.Cloud.Web.Auth.Security;

public static class IsSafeReturnUrl
{
    // web-M4 minor: tighten the allowed surface — remove /api/ and /signin/ prefixes.
    // /api/* is never a valid post-auth redirect destination (returns JSON, not a page).
    // /signin/* is the sign-in flow itself; a returnUrl pointing there creates a redirect loop
    // and opens a vector for phishing via crafted /signin/... sub-paths.
    // Legitimate SPA post-auth destinations all live under /app/.
    // This is still NOT an open-redirect (all URLs are already validated as relative, no //, etc.).
    //
    // MF-3 (Space-MCP inc-2a plan-review correction): /connect/authorize is ALSO a legitimate
    // post-auth destination — KoratAuthorizeEndpoints.RedirectToSignin (Task 3) sends a
    // not-yet-logged-in owner to "/app/signin?returnUrl=/connect/authorize?...". Without this
    // prefix, CanonicalSigninHandler.CompleteAsync (:136) rejects that returnUrl and falls back
    // to "/app/", bouncing the owner to the dashboard instead of back to the consent page they
    // started at — the OAuth flow could never complete for a not-yet-logged-in owner. Still
    // same-origin/relative-only (no open-redirect risk): every candidate still runs through
    // Validate() below (banned chars, no "//", no "..", relative-only) — this only widens WHICH
    // relative path is accepted, not the relative/same-origin constraint itself.
    private static readonly string[] AllowedPrefixes = { "/app/", "/connect/authorize" };
    private static readonly char[] BannedChars =
    {
        '\t', '\r', '\n', '\0',
        '\u0085', // NEL (next line)
        '\u2028', // line separator
        '\u2029', // paragraph separator
    };

    public static bool Check(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 2048) return false;
        if (!Validate(returnUrl)) return false;

        // Percent-decode and re-validate (catches /%2f%2fevil.com => //evil.com).
        // UnescapeDataString returns the same instance when no escapes are present,
        // so ReferenceEquals is a real fast path for the common no-encoding case.
        string decoded;
        try { decoded = Uri.UnescapeDataString(returnUrl); }
        catch (UriFormatException) { return false; }
        if (!ReferenceEquals(returnUrl, decoded) && decoded != returnUrl)
        {
            if (!Validate(decoded)) return false;
        }
        return true;
    }

    private static bool Validate(string url)
    {
        if (url.IndexOfAny(BannedChars) >= 0) return false;
        if (!url.StartsWith('/')) return false;
        if (url.StartsWith("//", StringComparison.Ordinal)) return false;
        if (url.StartsWith("/\\", StringComparison.Ordinal)) return false;
        // Note: "\\evil.com" already fails the StartsWith('/') guard above —
        // no separate backslash-only check needed.
        if (url.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var prefix in AllowedPrefixes)
        {
            if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;
            // Prefixes ending in '/' (e.g. "/app/") already have a path-boundary built in.
            // A bare-route prefix like "/connect/authorize" (no trailing '/' — the route has
            // no sub-paths) must ALSO be followed by a boundary ('?', '#', or end-of-string),
            // not by further path/host characters — otherwise a hypothetical
            // "/connect/authorizeEVIL" would wrongly match on StartsWith alone.
            if (prefix[^1] == '/') return true;
            if (url.Length == prefix.Length) return true;
            var boundary = url[prefix.Length];
            if (boundary is '?' or '#') return true;
        }
        return false;
    }
}
