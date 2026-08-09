using Korat.Domain;
using Korat.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Korat.Persistence.Tests;

/// <summary>
/// MUST-FIX 2 (adversarial review, Space-MCP increment 1 Tasks 4-6, BLOCKER): SessionAdmission
/// persists every Space-MCP aggregator-opened backend session's ClientNodeId as the synthetic
/// sentinel <see cref="WellKnownNodeIds.AggregatorSentinelNodeId"/> ("cagg-sentinel") — which
/// never has a Nodes row (there is no real bridge/publisher behind it, only the aggregator's
/// in-process delivery leg). Before the fix, ListReapableSessionsAsync's client-node OR-clause
/// treated that missing row exactly like an abandoned client, so SessionReaperService's hourly
/// sweep force-closed EVERY live Space-MCP session within ~75 minutes regardless of health. A
/// sentinel-client session's liveness must instead be judged by its backend PUBLISHER's Nodes
/// row (mirrors <see cref="McpServerHttpCloudPostgresTests"/>'s http_cloud publisher-gate proof,
/// just gating the CLIENT clause this time instead of the publisher clause).
///
/// MUST-FIX F2 (adversarial review, second pass, should-fix): the publisher-gate above still
/// leaves a zero-reap-clause HOLE for (a) a sentinel session whose publisher is healthy but whose
/// OWN aggregator-side teardown (MUST-FIX 1) failed/crashed/never ran, and (b) ANY sentinel ×
/// http_cloud session — discovery doesn't filter by transport, and admission supports http_cloud
/// under ConsumerBindPolicy.ServerMinted with PublisherNodeId="", so BOTH the client clause (gated
/// out for the sentinel id) and the publisher clause (gated out for http_cloud) are inapplicable
/// at once. <see cref="ListReapableSessions_SentinelClientSession_HealthyPublisher_OlderThanAgeCutoff_ReapedByBackstop"/>
/// and <see cref="ListReapableSessions_SentinelHttpCloudSession_RecentNotReaped_OldReapedByBackstop"/>
/// prove the new third (absolute-age) clause closes both gaps.
///
/// Postgres-backed (not InMemory) for the same reason as that class: InMemory does not exercise
/// the real LEFT JOIN SQL translation.
/// </summary>
public sealed class SpaceMcpSentinelReaperPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private IDbContextFactory<KoratDbContext>? _factory;
    private EfMetadataRepository? _repo;
    private string? _dockerUnavailableReason;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _pg.StartAsync();
        }
        catch (Exception ex)
        {
            _dockerUnavailableReason = $"Docker/Postgres container unavailable: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        var options = new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        await using (var ctx = new KoratDbContext(options))
            await ctx.Database.MigrateAsync();

        _factory = new StaticDbContextFactory(options);
        _repo = new EfMetadataRepository(_factory);
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    /// <summary>
    /// A session bound to the sentinel client (no Nodes row for it, ever) must survive purely on
    /// the strength of a healthy backend publisher, then become reap-eligible once that
    /// publisher goes stale — proving the gate is keyed on liveness of the PUBLISHER, not on the
    /// sentinel's own (permanently absent) client row. sentinelSessionAgeCutoff is set recent
    /// enough (24h, mirrors the production default) that the NEW F2 backstop clause never fires
    /// in this test — the session's StartedAt is "now", not older than the cutoff — isolating the
    /// assertions to the pre-existing publisher-liveness gate.
    /// </summary>
    [SkippableFact]
    public async Task ListReapableSessions_SentinelClientSession_GovernedByPublisherLiveness()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var publisherNode = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "sentinel-test-publisher",
            Status = NodeStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertNodeAsync(publisherNode);

        var session = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            PublisherNodeId = publisherNode.Id,
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow
        };
        await _repo.UpsertSessionAsync(session);

        var cutoff = DateTimeOffset.UtcNow - SessionReaperRules.ReapGrace;
        var sentinelSessionAgeCutoff = DateTimeOffset.UtcNow - SessionReaperRules.DefaultSpaceMcpSessionMaxAge;

        // (1) Publisher fresh — must NOT be reap-eligible purely because the sentinel client has
        // no Nodes row.
        var reapableWhilePublisherFresh = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);
        Assert.DoesNotContain(reapableWhilePublisherFresh, r => r.Id == session.Id);

        // (2) Publisher goes stale — NOW it must be reap-eligible (governed by the publisher).
        publisherNode.LastSeenAt = DateTimeOffset.UtcNow - SessionReaperRules.ReapGrace - TimeSpan.FromMinutes(5);
        await _repo.UpsertNodeAsync(publisherNode);
        var reapableAfterPublisherStale = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);
        Assert.Contains(reapableAfterPublisherStale, r => r.Id == session.Id);
    }

    /// <summary>
    /// MUST-FIX F2 (i)/(ii): a sentinel session with a PERFECTLY HEALTHY publisher (fresh
    /// LastSeenAt, never stale) must still become reap-eligible once its StartedAt crosses the
    /// absolute-age backstop — proving the new third clause is an INDEPENDENT net, not merely a
    /// restatement of the publisher-liveness gate. This is the gap MUST-FIX 1's own failure modes
    /// (crash mid-teardown, a best-effort TerminateSessionAsync that itself failed, a
    /// shutdown-deadline-canceled terminate) fall into: the aggregator's own lifecycle close never
    /// ran, and the publisher never learned to go stale either (nothing told it to).
    /// </summary>
    [SkippableFact]
    public async Task ListReapableSessions_SentinelClientSession_HealthyPublisher_OlderThanAgeCutoff_ReapedByBackstop()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var publisherNode = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "sentinel-backstop-publisher",
            Status = NodeStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow, // stays healthy for the WHOLE test — never goes stale.
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertNodeAsync(publisherNode);

        var now = DateTimeOffset.UtcNow;
        var recentSession = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            PublisherNodeId = publisherNode.Id,
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = now
        };
        await _repo.UpsertSessionAsync(recentSession);

        var oldSession = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            PublisherNodeId = publisherNode.Id,
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = now - TimeSpan.FromHours(30) // older than the 24h cutoff below.
        };
        await _repo.UpsertSessionAsync(oldSession);

        var cutoff = now - SessionReaperRules.ReapGrace;
        var sentinelSessionAgeCutoff = now - TimeSpan.FromHours(24);

        var reapable = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);

        Assert.DoesNotContain(reapable, r => r.Id == recentSession.Id);
        Assert.Contains(reapable, r => r.Id == oldSession.Id);
    }

    /// <summary>
    /// MUST-FIX F2 (iii): a sentinel × http_cloud session is gated OUT of BOTH pre-existing
    /// clauses at once — the client clause is gated out because the client IS the sentinel; the
    /// publisher clause is gated out because the server's Transport is http_cloud (no relay node
    /// to be stale/missing by design). Without the new third clause this combination is NEVER
    /// reap-eligible, no matter how old. Proves both halves: recent → survives, old → reaped.
    /// </summary>
    [SkippableFact]
    public async Task ListReapableSessions_SentinelHttpCloudSession_RecentNotReaped_OldReapedByBackstop()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var httpCloudServer = new McpServer
        {
            Id = McpServerId.New(),
            SpaceId = spaceId,
            PublisherNodeId = new NodeId(string.Empty),
            DisplayName = "sentinel-http-cloud-srv",
            Transport = McpServerTransports.HttpCloud,
            RemoteUrl = "https://example.test/mcp",
            Status = McpServerStatus.Published,
            IsAsserted = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertMcpServerAsync(httpCloudServer);

        var now = DateTimeOffset.UtcNow;
        var recentSession = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = httpCloudServer.Id,
            ClientNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            PublisherNodeId = new NodeId(string.Empty), // http_cloud: no relay node, by design.
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = now
        };
        await _repo.UpsertSessionAsync(recentSession);

        var oldSession = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = httpCloudServer.Id,
            ClientNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            PublisherNodeId = new NodeId(string.Empty),
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = now - TimeSpan.FromHours(30) // older than the 24h cutoff below.
        };
        await _repo.UpsertSessionAsync(oldSession);

        var cutoff = now - SessionReaperRules.ReapGrace;
        var sentinelSessionAgeCutoff = now - TimeSpan.FromHours(24);

        var reapable = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);

        Assert.DoesNotContain(reapable, r => r.Id == recentSession.Id);
        Assert.Contains(reapable, r => r.Id == oldSession.Id);
    }

    /// <summary>
    /// Regression guard: a NORMAL (non-sentinel) session whose ClientNodeId has no Nodes row at
    /// all (e.g. a client that never re-registered) must still be reap-eligible even though its
    /// publisher is healthy — proving the new gate is scoped to the sentinel id specifically, not
    /// a general "missing client row never reaps" bypass. Uses a recent sentinelSessionAgeCutoff
    /// (mirrors the production default) to prove this session's reap-eligibility comes from the
    /// UNCHANGED client-node clause, not incidentally from the new backstop clause (which does not
    /// apply to it at all — its ClientNodeId is not the sentinel id).
    /// </summary>
    [SkippableFact]
    public async Task ListReapableSessions_NonSentinelSession_MissingClientNode_StillReaped()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var publisherNode = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "regression-publisher",
            Status = NodeStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertNodeAsync(publisherNode);

        var session = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = NodeId.New(), // never persisted as a Node row — NOT the sentinel id
            PublisherNodeId = publisherNode.Id,
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow
        };
        await _repo.UpsertSessionAsync(session);

        var cutoff = DateTimeOffset.UtcNow - SessionReaperRules.ReapGrace;
        var sentinelSessionAgeCutoff = DateTimeOffset.UtcNow - SessionReaperRules.DefaultSpaceMcpSessionMaxAge;
        var reapable = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);

        Assert.Contains(reapable, r => r.Id == session.Id);
    }

    private async Task SeedSpaceAsync(SpaceId spaceId)
    {
        await using var ctx = new KoratDbContext(new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(_pg!.GetConnectionString()).Options);
        ctx.Spaces.Add(new SpaceRecord
        {
            Id = spaceId.Value,
            OwnerUserId = Guid.NewGuid().ToString("N"),
            DisplayName = "test-space",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class StaticDbContextFactory(DbContextOptions<KoratDbContext> options) : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() => new(options);
    }
}
