using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

public sealed class AccessRequestApprovalTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ApprovePendingRequest_CreatesActiveGrant()
    {
        var (requestId, seededAgentClientId, seededMcpServerId) = await SeedPendingRequestAsync();

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/access-requests/{requestId}/approve", null);
        response.EnsureSuccessStatusCode();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        // Scope the assertion to the seeded (agent, server) pair to avoid false positives from
        // active grants created by other tests sharing the KoratIntegrationFixture.
        Assert.Contains(grants, g =>
            g.Status == GrantStatus.Active
            && g.ConsumerId.Value == seededAgentClientId
            && g.McpServerId.Value == seededMcpServerId);
    }

    [Fact]
    public async Task DenyPendingRequest_LeavesNoActiveGrant()
    {
        var (requestId, seededAgentClientId, seededMcpServerId) = await SeedPendingRequestAsync();

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/access-requests/{requestId}/deny", null);
        response.EnsureSuccessStatusCode();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        // Scope the assertion to the seeded (agent, server) pair — other tests sharing the
        // KoratIntegrationFixture may have left active grants for different pairs in the space.
        Assert.DoesNotContain(grants, g =>
            g.Status == GrantStatus.Active
            && g.ConsumerId.Value == seededAgentClientId
            && g.McpServerId.Value == seededMcpServerId);

        var requests = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListAccessRequestsAsync();
        var denied = requests.Single(r => r.Id.Value == requestId);
        Assert.Equal(AccessRequestStatus.Denied, denied.Status);
    }

    [Fact]
    public async Task RevokeActiveGrant_MarksGrantRevoked()
    {
        var (_, _, _, grantId) = await SeedApprovedGrantAsync();

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/grants/{grantId}/revoke", null);
        response.EnsureSuccessStatusCode();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        var grant = grants.Single(g => g.Id.Value == grantId);
        Assert.Equal(GrantStatus.Revoked, grant.Status);
    }

    [Fact]
    public async Task DuplicatePendingRequest_ReturnsSameAccessRequestId()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, "dup-pending", "echo", "one"))!;
        var agentId = ConsumerId.New();

        var first = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);
        var second = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ReApproveApprovedRequest_ReturnsExistingGrant_Idempotent()
    {
        // Seed an already-approved request and capture the first grant.
        var (requestId, _, _, firstGrantId) = await SeedApprovedGrantAsync();

        // Call approve a second time via HTTP.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/access-requests/{requestId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        var returnedGrantId = body!.RootElement.GetProperty("id").GetString();
        // The idempotent re-approve must return the same grant that was created on the first approval.
        Assert.Equal(firstGrantId, returnedGrantId);

        // Confirm no duplicate grant rows were created — only the original grant exists.
        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        var grantsForRequest = grants.Where(g => g.Id.Value == firstGrantId).ToList();
        Assert.Single(grantsForRequest);
    }

    [Fact]
    public async Task ReApproveDeniedRequest_Returns409_InvalidStateTransition()
    {
        // Seed a denied request.
        var (requestId, _, _) = await SeedPendingRequestAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var deny = await client.PostAsync($"/api/access-requests/{requestId}/deny", null);
        deny.EnsureSuccessStatusCode();

        // Attempt to approve a denied request — must be rejected.
        var response = await client.PostAsync($"/api/access-requests/{requestId}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(body);
        // GrainExceptionExtensions maps InvalidStateTransition to 409; the detail field carries the message.
        var detail = body!.RootElement.GetProperty("detail").GetString();
        Assert.Contains("not in a state", detail, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string RequestId, string ConsumerId, string ServerId)> SeedPendingRequestAsync()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentId = ConsumerId.New();
        var request = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);
        return (request.Id.Value, agentId.Value, server.Id.Value);
    }

    private async Task<(string RequestId, string ConsumerId, string ServerId, string GrantId)> SeedApprovedGrantAsync()
    {
        var (requestId, agentClientId, serverId) = await SeedPendingRequestAsync();
        var grant = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .ApproveAccessRequestAsync(new AccessRequestId(requestId), KoratIntegrationFixture.DevSpaceOwnerUserId);
        return (requestId, agentClientId, serverId, grant.Id.Value);
    }
}
