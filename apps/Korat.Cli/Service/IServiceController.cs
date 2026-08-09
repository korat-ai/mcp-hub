namespace Korat.Cli.Service;

/// <summary>
/// Abstracts OS-specific service management (launchd on macOS, systemd --user on Linux).
/// </summary>
internal interface IServiceController
{
    /// <summary>Installs (or reinstalls) the service unit and starts it. Idempotent.</summary>
    Task InstallAsync(CancellationToken ct = default);

    /// <summary>Stops and removes the service unit. No-op if not installed.</summary>
    Task UninstallAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current service status.
    /// </summary>
    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);
}

internal sealed record ServiceStatus(bool IsInstalled, bool IsRunning, string? Detail);
