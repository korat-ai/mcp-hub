using Korat.Cloud.Maintenance;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

public sealed class SessionReaperServiceTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private IMetadataRepository Repo() =>
        fixture.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IMetadataRepository>();

    private SessionReaperService NewReaper(IMetadataRepository repo) =>
        new(repo, fixture.ClusterClient, new ConfigurationBuilder().Build(),
            NullLogger<SessionReaperService>.Instance);

    [Fact]
    public async Task Sweep_closes_stale_session_and_leaves_live_session()
    {
        var repo = Repo();
        var space = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var stale = now - SessionReaperRules.ReapGrace - TimeSpan.FromMinutes(5);

        var freshNode = MakeNode(space, now);
        var staleNode = MakeNode(space, stale);
        await repo.UpsertNodeAsync(freshNode);
        await repo.UpsertNodeAsync(staleNode);

        var ghost = MakeSession(space, freshNode.Id, staleNode.Id, SessionStatus.Active); // publisher stale
        var live = MakeSession(space, freshNode.Id, freshNode.Id, SessionStatus.Active);
        await repo.UpsertSessionAsync(ghost);
        await repo.UpsertSessionAsync(live);

        await NewReaper(repo).SweepAsync(CancellationToken.None);

        var ghostAfter = await repo.GetSessionAsync(ghost.Id);
        var liveAfter = await repo.GetSessionAsync(live.Id);
        Assert.Equal(SessionStatus.Closed, ghostAfter!.Status);
        Assert.Equal(SessionCloseReason.Abandoned, ghostAfter.CloseReason);
        Assert.NotNull(ghostAfter.EndedAt);
        Assert.Equal(SessionStatus.Active, liveAfter!.Status);
    }

    [Fact]
    public async Task Sweep_is_idempotent_second_run_no_throw_still_closed()
    {
        var repo = Repo();
        var space = SpaceId.New();
        var stale = DateTimeOffset.UtcNow - SessionReaperRules.ReapGrace - TimeSpan.FromMinutes(5);
        var staleNode = MakeNode(space, stale);
        await repo.UpsertNodeAsync(staleNode);
        var ghost = MakeSession(space, staleNode.Id, staleNode.Id, SessionStatus.Active);
        await repo.UpsertSessionAsync(ghost);

        var reaper = NewReaper(repo);
        await reaper.SweepAsync(CancellationToken.None);
        await reaper.SweepAsync(CancellationToken.None); // no throw, no double-effect

        var after = await repo.GetSessionAsync(ghost.Id);
        Assert.Equal(SessionStatus.Closed, after!.Status);
    }

    private static Node MakeNode(SpaceId space, DateTimeOffset lastSeen) => new()
    {
        Id = NodeId.New(), SpaceId = space, DisplayName = "n",
        Status = NodeStatus.Online, LastSeenAt = lastSeen,
        CreatedAt = lastSeen, UpdatedAt = lastSeen
    };

    private static RelaySession MakeSession(SpaceId space, NodeId client, NodeId publisher, SessionStatus status) => new()
    {
        Id = SessionId.New(), SpaceId = space, GrantId = GrantId.New(),
        ConsumerId = ConsumerId.New(), McpServerId = McpServerId.New(),
        ClientNodeId = client, PublisherNodeId = publisher,
        HomeGatewayId = GatewayId.New(), Status = status, StartedAt = DateTimeOffset.UtcNow
    };
}
