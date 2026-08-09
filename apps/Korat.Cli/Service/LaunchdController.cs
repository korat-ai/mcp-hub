using Korat.Cli.Config;
using Korat.Cli.Util;

namespace Korat.Cli.Service;

/// <summary>
/// macOS launchd LaunchAgent controller.
///
/// Manages the plist at <c>~/Library/LaunchAgents/ai.korat.node.plist</c>.
/// The unit runs <c>korat service run</c> at login and restarts on crash.
/// </summary>
internal sealed class LaunchdController : IServiceController
{
    internal const string PlistLabel = "ai.korat.node";
    internal const string PlistFileName = "ai.korat.node.plist";

    internal static string PlistPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", PlistFileName);

    // ── Pure string generation (unit-testable, no side effects) ─────────────

    /// <summary>
    /// Generates the launchd plist XML for the Korat node service.
    /// </summary>
    /// <param name="exePath">Absolute path to the <c>korat</c> executable.</param>
    /// <param name="logsDir">Directory where stdout/stderr logs are written.</param>
    /// <param name="path">
    /// Value for the <c>PATH</c> environment variable baked into the plist.
    /// Captured at install time so MCP servers launched by the daemon can find
    /// <c>npx</c>, <c>uvx</c>, <c>node</c>, etc. even under launchd's restricted PATH.
    /// </param>
    /// <param name="home">User home directory baked into the plist as <c>HOME</c> and <c>WorkingDirectory</c>.</param>
    internal static string GeneratePlist(string exePath, string logsDir, string path, string home)
    {
        var outLog = Path.Combine(logsDir, "service.out.log");
        var errLog = Path.Combine(logsDir, "service.err.log");

        // XML-escape values: & → &amp;  < → &lt;  > → &gt;
        // Paths rarely contain these, but $HOME/.local/bin can appear inside PATH.
        static string Escape(string s) => s
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        var escapedExe  = Escape(exePath);
        var escapedPath = Escape(path);
        var escapedHome = Escape(home);
        var escapedOut  = Escape(outLog);
        var escapedErr  = Escape(errLog);

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
    "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{PlistLabel}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{escapedExe}</string>
        <string>service</string>
        <string>run</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{escapedOut}</string>
    <key>StandardErrorPath</key>
    <string>{escapedErr}</string>
    <key>WorkingDirectory</key>
    <string>{escapedHome}</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key>
        <string>{escapedPath}</string>
        <key>HOME</key>
        <string>{escapedHome}</string>
    </dict>
</dict>
</plist>
""";
    }

    // ── IServiceController ───────────────────────────────────────────────────

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        var logsDir = Path.Combine(KoratConfigPaths.BaseDir, "logs");
        Directory.CreateDirectory(logsDir);

        var plistPath = PlistPath;
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);

        // Capture the interactive-shell PATH at install time so the daemon can find
        // npx, uvx, node, python, etc. even under launchd's restricted PATH.
        const string FallbackPath = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            path = FallbackPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var plist = GeneratePlist(exePath, logsDir, path, home);
        await File.WriteAllTextAsync(plistPath, plist, ct);
        Console.WriteLine($"Wrote plist: {plistPath}");

        // Try modern bootstrap first; fall back to legacy load.
        var uid = await GetUidAsync(ct);
        if (uid is not null)
        {
            // FIX-4: quote plistPath so launchctl handles paths with spaces correctly.
            var (rc, _, err) = await ShellHelper.RunAsync(
                "launchctl", $"bootstrap gui/{uid} \"{plistPath}\"", ct);
            if (rc == 0)
            {
                Console.WriteLine("Service bootstrapped via launchctl bootstrap.");
                return;
            }

            // Already loaded (error 36 = service already registered) — bootout + retry.
            if (err.Contains("36", StringComparison.Ordinal) ||
                err.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                await ShellHelper.RunAsync("launchctl", $"bootout gui/{uid}/{PlistLabel}", ct);
                await ShellHelper.RunAsync("launchctl", $"bootstrap gui/{uid} \"{plistPath}\"", ct);
                Console.WriteLine("Service reinstalled via launchctl bootstrap.");
                return;
            }
        }

        // Legacy fallback.
        await ShellHelper.RunAsync("launchctl", $"load -w \"{plistPath}\"", ct);
        Console.WriteLine("Service loaded via launchctl load.");
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        var plistPath = PlistPath;
        var uid = await GetUidAsync(ct);

        if (uid is not null)
            await ShellHelper.RunAsync("launchctl", $"bootout gui/{uid}/{PlistLabel}", ct);
        else
            await ShellHelper.RunAsync("launchctl", $"unload -w {plistPath}", ct);

        if (File.Exists(plistPath))
        {
            File.Delete(plistPath);
            Console.WriteLine($"Removed plist: {plistPath}");
        }
        else
        {
            Console.WriteLine("Service was not installed (plist absent).");
        }
    }

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var installed = File.Exists(PlistPath);

        // launchctl list <label> exits 0 when the job is loaded.
        var (rc, stdout, _) = await ShellHelper.RunAsync(
            "launchctl", $"list {PlistLabel}", ct);
        var running = rc == 0 && !stdout.Contains("\"PID\" = missing", StringComparison.Ordinal);

        var detail = running
            ? "Job is loaded and running."
            : installed
                ? "Plist installed but job not currently running."
                : "Service not installed. Run `korat service install`.";

        return new ServiceStatus(installed, running, detail);
    }

    private static async Task<string?> GetUidAsync(CancellationToken ct)
    {
        var (rc, stdout, _) = await ShellHelper.RunAsync("id", "-u", ct);
        return rc == 0 ? stdout.Trim() : null;
    }
}
