using Korat.Cli.Config;
using Korat.Cli.Util;

namespace Korat.Cli.Service;

/// <summary>
/// Linux systemd --user unit controller.
///
/// Manages the unit at <c>~/.config/systemd/user/korat-node.service</c>.
/// The unit runs <c>korat service run</c> at login and restarts on failure.
/// </summary>
internal sealed class SystemdController : IServiceController
{
    internal const string UnitName = "korat-node.service";

    internal static string UnitPath
    {
        get
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configBase = string.IsNullOrWhiteSpace(xdgConfigHome)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config")
                : xdgConfigHome;
            return Path.Combine(configBase, "systemd", "user", UnitName);
        }
    }

    // ── Pure string generation (unit-testable, no side effects) ─────────────

    /// <summary>
    /// Generates the systemd --user unit file content for the Korat node service.
    /// </summary>
    /// <param name="exePath">Absolute path to the <c>korat</c> executable.</param>
    /// <param name="path">
    /// Value for the <c>PATH</c> environment variable baked into the unit.
    /// Captured at install time so MCP servers launched by the daemon can find
    /// <c>npx</c>, <c>uvx</c>, <c>node</c>, etc. even under systemd --user's minimal PATH.
    /// </param>
    /// <param name="home">User home directory baked in as <c>HOME</c> and <c>WorkingDirectory</c>.</param>
    internal static string GenerateUnit(string exePath, string path, string home) => $"""
[Unit]
Description=Korat publisher runtime — publishes local MCP servers to Korat
After=network.target

[Service]
ExecStart="{exePath}" service run
Restart=on-failure
RestartSec=5
WorkingDirectory={home}
Environment=PATH={path}
Environment=HOME={home}

[Install]
WantedBy=default.target
""";

    // ── IServiceController ───────────────────────────────────────────────────

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        var unitPath = UnitPath;
        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);

        // Capture the interactive-shell PATH at install time so the daemon can find
        // npx, uvx, node, python, etc. even under systemd --user's minimal PATH.
        const string FallbackPath = "/usr/local/bin:/usr/bin:/bin";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            path = FallbackPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var unit = GenerateUnit(exePath, path, home);
        await File.WriteAllTextAsync(unitPath, unit, ct);
        Console.WriteLine($"Wrote unit: {unitPath}");

        var (rc1, _, err1) = await ShellHelper.RunAsync(
            "systemctl", "--user daemon-reload", ct);
        if (rc1 != 0)
            Console.Error.WriteLine($"daemon-reload warning: {err1}");

        var (rc2, _, err2) = await ShellHelper.RunAsync(
            "systemctl", "--user enable --now korat-node.service", ct);
        if (rc2 != 0)
            Console.Error.WriteLine($"enable --now failed: {err2}");
        else
            Console.WriteLine("Service enabled and started.");
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        var unitPath = UnitPath;

        // Best-effort stop + disable before removing the file.
        await ShellHelper.RunAsync("systemctl", "--user disable --now korat-node.service", ct);

        if (File.Exists(unitPath))
        {
            File.Delete(unitPath);
            Console.WriteLine($"Removed unit: {unitPath}");
        }
        else
        {
            Console.WriteLine("Service was not installed (unit file absent).");
        }

        await ShellHelper.RunAsync("systemctl", "--user daemon-reload", ct);
    }

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var installed = File.Exists(UnitPath);

        var (rc, stdout, _) = await ShellHelper.RunAsync(
            "systemctl", "--user is-active korat-node.service", ct);
        var running = rc == 0 && stdout.Trim() == "active";

        var detail = running
            ? "Service is active (running)."
            : installed
                ? $"Unit installed but not active (state: {stdout.Trim()})."
                : "Service not installed. Run `korat service install`.";

        return new ServiceStatus(installed, running, detail);
    }
}
