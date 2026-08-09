namespace Korat.Cli.Util;

public static class UpgradeNotice
{
    private static bool _warned;

    /// <summary>Message if an upgrade is available, else null. Pure — for testing.</summary>
    public static string? Format(string current, string running)
        => !string.IsNullOrEmpty(current) && SemVer.IsNewer(current, running)
            ? $"A newer korat is available: {current} (you have {running}). Run 'korat upgrade'."
            : null;

    /// <summary>Write the notice to stderr at most once per process. Non-fatal.</summary>
    public static void MaybeWarn(string current, string? running = null, TextWriter? err = null)
    {
        if (_warned) return;
        var msg = Format(current, running ?? CliVersion.Bare());
        if (msg is null) return;
        _warned = true;
        (err ?? Console.Error).WriteLine(msg);
    }

    internal static void ResetForTests() => _warned = false;
}
