using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Confirmed API (Microsoft Learn, Microsoft.Orleans.TestingHost v10.0.0):
/// <c>TestCluster.GetSiloServiceProvider(SiloAddress silo = default) : IServiceProvider</c> — with
/// the default parameter, "one of the existing silos will be picked randomly" per its own doc
/// comment. That's fine here: SiloConfigurator.Configure's registrations (including the bridged
/// SessionRoutingTable/IOutboundHttpClientFactory) apply to EVERY silo in the cluster identically,
/// so whichever silo answers resolves through the SAME static bridge fields either way.
/// </summary>
public sealed class HttpMcpProxyGrainTestBridgeTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public void SiloContainer_ResolvesTheSameSessionRoutingTableInstance_AsTheWebHost()
    {
        var fromWebHost = fixture.Services.GetRequiredService<Korat.Cloud.Gateways.SessionRoutingTable>();
        var fromSilo = fixture.Cluster.GetSiloServiceProvider()
            .GetRequiredService<Korat.Cloud.Gateways.SessionRoutingTable>();

        Assert.Same(fromWebHost, fromSilo);
    }

    [Fact]
    public void SiloContainer_ResolvesAWorkingOutboundHttpClientFactory()
    {
        var factory = fixture.Cluster.GetSiloServiceProvider()
            .GetRequiredService<Korat.Domain.IOutboundHttpClientFactory>();
        using var client = factory.CreateClient("bridge-smoke-test");

        Assert.NotNull(client);
    }
}
