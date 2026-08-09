using System.Net;
using Korat.Cloud.IntegrationTests;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.ContractTests;

public sealed class ApprovePageContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task AccessRequestDetail_HasNoMcpPayloadFields()
    {
        var nodeId = NodeId.New();
        var server = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "approve-contract", "echo", "x");
        var agentId = ConsumerId.New();
        var request = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .CreateAccessRequestAsync(agentId, server.Id, nodeId);

        // Endpoint requires session auth.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.GetAsync($"/api/access-requests/{request.Id.Value}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcpServerName", json);
        Assert.Contains("agentNodeName", json);
    }

    [SkippableFact]
    public async Task ApprovePageStaticFile_IsServed()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/space/approve.html");
        // The built SPA is not present in wwwroot during a plain `dotnet test` run; it is
        // produced by the Docker build and served in CI/prod. Skip (don't fail) when absent,
        // mirroring the Testcontainers skip pattern, so this contract still gates where the
        // SPA exists. Build apps/Korat.App into Korat.Cloud/wwwroot to run it locally.
        Skip.If(response.StatusCode == HttpStatusCode.NotFound,
            "Built SPA not present in wwwroot (run a Docker/SPA build to exercise this contract).");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Allow this device", html);
    }
}
