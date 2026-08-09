using System.Net;
using Korat.Cloud.IntegrationTests;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.ContractTests;

public sealed class WebAuthContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData("/api/access-requests/req-1/approve")]
    [InlineData("/api/access-requests/req-1/deny")]
    [InlineData("/api/mcp-servers/srv-1/disable")]
    [InlineData("/api/grants/grant-1/revoke")]
    public async Task MutationEndpoints_WithoutAuth_ReturnUnauthorized(string path)
    {
        var response = await fixture.Factory.CreateClient().PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DisableEndpoint_WithSessionAuth_AcceptsAuthorizedRequest()
    {
        var nodeId = NodeId.New();
        var server = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "auth-test", "echo", "x");

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/mcp-servers/{server.Id.Value}/disable", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
