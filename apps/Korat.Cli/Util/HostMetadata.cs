using System.Runtime.InteropServices;

namespace Korat.Cli.Util;

/// <summary>
/// Node host metadata (additive, node-visibility-doctor design 2026-07-02): the facts about
/// this machine sent in <c>NodeHello.hostname/os/arch</c> so the cloud/console can answer
/// "где кто запущен на каком хосте". Pure/static — no I/O beyond <see cref="Environment.MachineName"/> —
/// so it's directly unit-testable without a live gRPC connection.
/// </summary>
public static class HostMetadata
{
    /// <summary>The machine's hostname, as reported by the OS.</summary>
    public static string Hostname => Environment.MachineName;

    /// <summary>"macos" | "linux" | "windows"; "" if none of the known platforms match.</summary>
    public static string Os =>
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsWindows() ? "windows" :
        "";

    /// <summary>Lowercase OS architecture, e.g. "arm64", "x64".</summary>
    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
