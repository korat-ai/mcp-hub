using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

[Trait("Category", "PersistenceRestart")]
public sealed class PersistenceRestartTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task PendingRequestSurvivesRestart()
    {
        var (requestId, _) = await SeedPendingRequestAsync();
        await fixture.RecycleSilosAsync();

        var requests = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListAccessRequestsAsync();
        var pending = Assert.Single(requests.Where(r => r.Id.Value == requestId));
        Assert.Equal(AccessRequestStatus.Pending, pending.Status);

        // /api/access-requests requires session auth.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var json = await client.GetStringAsync("/api/access-requests");
        Assert.Contains(requestId, json);
    }

    [Fact]
    public async Task ApproveAfterRestartSucceeds()
    {
        var (requestId, _) = await SeedPendingRequestAsync();
        await fixture.RecycleSilosAsync();

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/access-requests/{requestId}/approve", null);
        response.EnsureSuccessStatusCode();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        Assert.Contains(grants, g => g.Status == GrantStatus.Active);
    }

    [Fact]
    public async Task DenyAfterRestartSucceeds()
    {
        var (requestId, _) = await SeedPendingRequestAsync();
        await fixture.RecycleSilosAsync();

        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.PostAsync($"/api/access-requests/{requestId}/deny", null);
        response.EnsureSuccessStatusCode();

        var requests = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListAccessRequestsAsync();
        var denied = requests.Single(r => r.Id.Value == requestId);
        Assert.Equal(AccessRequestStatus.Denied, denied.Status);
    }

    [Fact]
    public async Task IdempotentPendingRequestReturnsSameId()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, "idem-server", "echo", "one"))!;
        var agentId = ConsumerId.New();

        var first = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);
        var second = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task ApprovedRequestStatusSurvivesRestart()
    {
        var (requestId, _) = await SeedPendingRequestAsync();
        await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .ApproveAccessRequestAsync(new AccessRequestId(requestId), KoratIntegrationFixture.DevSpaceOwnerUserId);

        await fixture.RecycleSilosAsync();

        var requests = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListAccessRequestsAsync();
        var approved = requests.Single(r => r.Id.Value == requestId);
        Assert.Equal(AccessRequestStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task ActiveGrantSurvivesRestart()
    {
        var (_, grantId) = await SeedApprovedGrantAsync();
        await fixture.RecycleSilosAsync();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        var grant = grants.Single(g => g.Id.Value == grantId);
        Assert.Equal(GrantStatus.Active, grant.Status);
    }

    [Fact]
    public async Task RevokedGrantSurvivesRestart()
    {
        var (_, grantId) = await SeedApprovedGrantAsync();
        await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .RevokeGrantAsync(new GrantId(grantId), KoratIntegrationFixture.DevSpaceOwnerUserId);

        await fixture.RecycleSilosAsync();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            var persisted = await repository.GetGrantAsync(new GrantId(grantId));
            Assert.NotNull(persisted);
            Assert.Equal(GrantStatus.Revoked, persisted.Status);
        }

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        Assert.Contains(grants, g => g.Id.Value == grantId && g.Status == GrantStatus.Revoked);
    }

    [Fact]
    public async Task ApproveRejectedWhenServerDisabled()
    {
        var (requestId, serverId) = await SeedPendingRequestAsync();
        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(serverId).DisableAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var disabled = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(serverId).GetAsync();
        Assert.Equal(McpServerStatus.Disabled, disabled.Status);

        var exception = await Record.ExceptionAsync(() =>
            fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
                .ApproveAccessRequestAsync(new AccessRequestId(requestId), KoratIntegrationFixture.DevSpaceOwnerUserId));

        var domain = FindDomainException(exception);
        Assert.NotNull(exception);
        Assert.True(
            domain?.Code == KoratErrorCode.ServerDisabled ||
            exception.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase),
            $"Expected server disabled rejection, got: {exception}");
    }

    private static KoratDomainException? FindDomainException(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is KoratDomainException domain)
                return domain;
        }

        return null;
    }

    [Fact]
    public async Task ClosedSessionMetadataSurvivesRestart()
    {
        var sessionId = await SeedClosedSessionAsync(bytesClientToServer: 100, bytesServerToClient: 200);
        await fixture.RecycleSilosAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        var session = await repository.GetSessionAsync(new SessionId(sessionId));

        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(100, session.BytesClientToServer);
        Assert.Equal(200, session.BytesServerToClient);
    }

    [Fact]
    public async Task ActiveSessionBecomesTerminalAfterRestart()
    {
        var sessionId = SessionId.New().Value;
        var grantId = GrantId.New();
        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId);
        await sessionGrain.OpenAsync(
            grantId,
            ConsumerId.New(),
            McpServerId.New(),
            NodeId.New(),
            NodeId.New(),
            GatewayId.New(),
            SpaceId.New());

        await fixture.RecycleSilosAsync();

        var session = await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId).GetAsync();
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(SessionCloseReason.ServiceRestart, session.CloseReason);

        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        var persisted = await repository.GetSessionAsync(new SessionId(sessionId));
        Assert.NotNull(persisted);
        Assert.Equal(SessionStatus.Closed, persisted.Status);
        Assert.Equal(SessionCloseReason.ServiceRestart, persisted.CloseReason);
    }

    [Fact]
    public async Task PersistedSessionRows_ContainNoPayloadFields()
    {
        var sessionId = await SeedClosedSessionAsync(10, 20);

        // W3: /api/sessions now requires owner auth.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var json = await client.GetStringAsync("/api/sessions");

        Assert.Contains(sessionId, json);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string RequestId, string ServerId)> SeedPendingRequestAsync()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, $"server-{Guid.NewGuid():N}", "echo", "test"))!;
        var request = await space.CreateAccessRequestAsync(ConsumerId.New(), server.Id, nodeId);
        return (request.Id.Value, server.Id.Value);
    }

    private async Task<(string RequestId, string GrantId)> SeedApprovedGrantAsync()
    {
        var (requestId, _) = await SeedPendingRequestAsync();
        var grant = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .ApproveAccessRequestAsync(new AccessRequestId(requestId), KoratIntegrationFixture.DevSpaceOwnerUserId);
        return (requestId, grant.Id.Value);
    }

    private async Task<string> SeedClosedSessionAsync(long bytesClientToServer, long bytesServerToClient)
    {
        var sessionId = SessionId.New().Value;
        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId);
        // Use the legacy owner's default Space so that the session is visible to the
        // CreateAuthenticatedClientAsync(LegacyOwnerUserId) client. The SpaceId is derived
        // from the fixture's bridge property rather than a literal "default" string so that
        // SP4 can update the coupling in one place.
        await sessionGrain.OpenAsync(
            GrantId.New(),
            ConsumerId.New(),
            McpServerId.New(),
            NodeId.New(),
            NodeId.New(),
            GatewayId.New(),
            new SpaceId(fixture.LegacyOwnerSpaceId));
        await sessionGrain.RecordBytesAsync(bytesClientToServer, bytesServerToClient);
        await sessionGrain.CloseAsync(SessionCloseReason.Completed);
        return sessionId;
    }
}
