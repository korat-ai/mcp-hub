using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Persistence.Tests.Infrastructure;
using Xunit;

namespace Korat.Persistence.Tests;

public sealed class SessionReaperRepositoryTests
{
    private readonly PersistenceTestFixture _fixture = new();

    [Fact]
    public async Task ListReapable_returns_only_long_stale_active_or_opening_sessions()
    {
        var repo = _fixture.CreateRepository();
        var space = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - SessionReaperRules.ReapGrace;
        var stale = now - SessionReaperRules.ReapGrace - TimeSpan.FromMinutes(5);
        var fresh = now;

        var freshNode = MakeNode(space, fresh);
        var staleNode = MakeNode(space, stale);
        await repo.UpsertNodeAsync(freshNode);
        await repo.UpsertNodeAsync(staleNode);

        // Reapable: Active, publisher stale.
        var reapPub = MakeSession(space, freshNode.Id, staleNode.Id, SessionStatus.Active);
        await repo.UpsertSessionAsync(reapPub);
        // Reapable: Opening, client stale.
        var reapCli = MakeSession(space, staleNode.Id, freshNode.Id, SessionStatus.Opening);
        await repo.UpsertSessionAsync(reapCli);
        // Reapable: Active, client node row missing (never persisted).
        var reapMissing = MakeSession(space, NodeId.New(), freshNode.Id, SessionStatus.Active);
        await repo.UpsertSessionAsync(reapMissing);

        // NOT reapable: Active, both nodes fresh.
        var live = MakeSession(space, freshNode.Id, freshNode.Id, SessionStatus.Active);
        await repo.UpsertSessionAsync(live);
        // NOT reapable: already Closed (even with a stale node).
        var closed = MakeSession(space, staleNode.Id, staleNode.Id, SessionStatus.Closed);
        await repo.UpsertSessionAsync(closed);

        // No sentinel-cliented session here — the sentinel age cutoff is irrelevant to this
        // test's outcome, mirror the production default.
        var sentinelSessionAgeCutoff = now - SessionReaperRules.DefaultSpaceMcpSessionMaxAge;
        var reapable = (await repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff)).Select(r => r.Id).ToHashSet();

        Assert.Contains(reapPub.Id, reapable);
        Assert.Contains(reapCli.Id, reapable);
        Assert.Contains(reapMissing.Id, reapable);
        Assert.DoesNotContain(live.Id, reapable);
        Assert.DoesNotContain(closed.Id, reapable);
    }

    private static Node MakeNode(SpaceId space, DateTimeOffset lastSeen) => new()
    {
        Id = NodeId.New(),
        SpaceId = space,
        DisplayName = "n",
        Status = NodeStatus.Online,
        LastSeenAt = lastSeen,
        CreatedAt = lastSeen,
        UpdatedAt = lastSeen
    };

    private static RelaySession MakeSession(SpaceId space, NodeId client, NodeId publisher, SessionStatus status) => new()
    {
        Id = SessionId.New(),
        SpaceId = space,
        GrantId = GrantId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = McpServerId.New(),
        ClientNodeId = client,
        PublisherNodeId = publisher,
        HomeGatewayId = GatewayId.New(),
        Status = status,
        StartedAt = DateTimeOffset.UtcNow
    };
}
