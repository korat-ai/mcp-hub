namespace Korat.Cli.Util;

/// <summary>Minimal SemVer comparison for upgrade decisions. Compares MAJOR.MINOR.PATCH
/// numerically; a pre-release (has '-') sorts BEFORE its release. Build metadata ('+...')
/// and a leading 'v' are ignored. Unparseable inputs compare as equal (no nudge).</summary>
public static class SemVer
{
    /// <summary>True if <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
        => Compare(candidate, current) > 0;

    public static int Compare(string a, string b)
    {
        if (!TryParse(a, out var va, out var preA) || !TryParse(b, out var vb, out var preB))
            return 0;
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        // equal core: release (no pre) > pre-release; both-pre or both-release => equal enough
        if (preA == preB) return 0;
        return preA ? -1 : 1;
    }

    private static bool TryParse(string s, out Version core, out bool isPre)
    {
        core = new Version(0, 0, 0); isPre = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = s.IndexOf('-');
        if (dash >= 0) { isPre = true; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min) || !int.TryParse(parts[2], out var pat))
            return false;
        core = new Version(maj, min, pat);
        return true;
    }
}
