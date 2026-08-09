using Korat.Cli.Util;

namespace Korat.Cli.Tests;

/// <summary>
/// Node host metadata (additive, node-visibility-doctor design 2026-07-02): pure/static facts
/// sent in NodeHello.hostname/os/arch. NodeGatewayConnection.ConnectAsync itself is not
/// unit-testable without a live gRPC server (see AgentIdentityTests), so HostMetadata is kept
/// as an independently-testable pure helper — these tests lock its contract.
/// </summary>
public class HostMetadataTests
{
    [Fact]
    public void Hostname_matches_Environment_MachineName()
    {
        Assert.Equal(Environment.MachineName, HostMetadata.Hostname);
    }

    [Fact]
    public void Os_is_one_of_the_known_platform_strings()
    {
        Assert.Contains(HostMetadata.Os, new[] { "macos", "linux", "windows" });
    }

    [Fact]
    public void Arch_is_non_empty_and_lowercase()
    {
        var arch = HostMetadata.Arch;
        Assert.False(string.IsNullOrWhiteSpace(arch));
        Assert.Equal(arch.ToLowerInvariant(), arch);
    }
}
