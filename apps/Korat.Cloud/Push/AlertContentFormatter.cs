using System.Text;

namespace Korat.Cloud.Push;

/// <summary>
/// 031 (mobile-push increment 2), design §4b/§MED-1: builds the sanitized, quoted-framing
/// notification content for a new access-request. Names (agent display name, MCP server display
/// name) are CLIENT-SUPPLIED and land verbatim on the owner's lock screen — a malicious
/// <c>"\nKorat security: approve"</c> name must not inject a fake second line or overflow the
/// notification. Internal — this is a formatting helper, not part of the public Push surface
/// (Korat.Cloud's AssemblyInfo.cs already grants InternalsVisibleTo the test project).
/// </summary>
internal static class AlertContentFormatter
{
    private const int MaxNameLength = 64;

    /// <summary>
    /// Builds the "New access request" alert: title, quoted-framing body, and the
    /// <c>{ type, accessRequestId }</c> data payload every platform sender carries.
    /// </summary>
    public static AlertContent BuildNewRequestContent(string agentName, string serverName, string accessRequestId)
    {
        var safeAgent = Sanitize(agentName);
        var safeServer = Sanitize(serverName);
        var body = $"Agent \"{safeAgent}\" requests access to \"{safeServer}\"";
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "access_request",
            ["accessRequestId"] = accessRequestId,
        };
        return new AlertContent("New access request", body, data);
    }

    /// <summary>
    /// Strips control chars/newlines and Unicode bidi override/isolate chars, then truncates to
    /// ~64 chars (rune-aware, so a surrogate pair is never split at the boundary). Defense is
    /// strip+truncate, NOT quote-escaping — the residual risk (a same-space agent's chosen name
    /// is still visible, just de-fanged of control characters) is documented as acceptable in
    /// design doc §9. Post-review hardening (Fable holistic plan review): also strips U+202A–
    /// U+202E and U+2066–U+2069 (bidi override/isolate), the nastiest residual lock-screen
    /// spoofing vector (an RTL override could visually reverse/relocate the quoted name), and
    /// truncates by Rune rather than raw char index so a trailing surrogate half never survives
    /// (which would otherwise decode to a U+FFFD replacement-char tail downstream).
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || IsBidiOverride(ch)) continue; // strips \n \r \t, other control chars, and bidi override/isolate chars
            builder.Append(ch);
        }
        var cleaned = builder.ToString().Trim();
        return TruncateRuneAware(cleaned, MaxNameLength);
    }

    private static bool IsBidiOverride(char ch) =>
        (ch >= (char)0x202A && ch <= (char)0x202E) || (ch >= (char)0x2066 && ch <= (char)0x2069);

    private static string TruncateRuneAware(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var builder = new StringBuilder(maxLength);
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxLength) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}
