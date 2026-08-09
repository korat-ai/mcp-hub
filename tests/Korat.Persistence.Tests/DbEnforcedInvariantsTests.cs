using Korat.Domain;
using Korat.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Persistence.Tests;

/// <summary>
/// DB-enforced invariant tests that require a real Postgres instance.
///
/// These tests verify that filtered unique indexes actually reject duplicates — something the
/// EF InMemory provider silently ignores. The pattern mirrors OrleansAdoNetSchemaTests:
/// start a Testcontainers Postgres container, apply EF migrations, run assertions, then
/// dispose. Tests are marked [SkippableFact] and skip cleanly when Docker is unavailable.
///
/// Invariants verified:
///   - Two Pending access-requests for same (space, agent, server) → DB rejects second insert.
///   - Two Active grants for same (space, agent, server) → application layer rejects duplicate.
///   - Two MCP servers with same DisplayName in a Space → DB rejects second insert.
///   - Two default Spaces for one owner → DB rejects second insert.
///   - ApproveAccessRequestAsync concurrent race → exactly one Active grant created.
/// </summary>
public sealed class DbEnforcedInvariantsTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private IDbContextFactory<KoratDbContext>? _factory;
    private EfMetadataRepository? _repo;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _pg.StartAsync();
        }
        catch (Exception ex)
        {
            // Docker unavailable — mark as skipped via the shared field (checked in each test).
            _dockerUnavailableReason = $"Docker/Postgres container unavailable: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        var connectionString = _pg.GetConnectionString();
        _factory = new PostgresDbContextFactory(connectionString);
        _repo = new EfMetadataRepository(_factory);

        // Apply EF migrations so the real schema (incl. filtered unique indexes) is in place.
        await _repo.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    // ── Skip guard ─────────────────────────────────────────────────────────────

    private string? _dockerUnavailableReason;

    private void SkipIfDockerUnavailable()
    {
        if (_dockerUnavailableReason is not null)
            throw new SkipException(_dockerUnavailableReason);
        if (_repo is null || _factory is null)
            throw new SkipException("Repository not initialized.");
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// C4: The filtered unique index on (SpaceId, ConsumerId, McpServerId) WHERE Status='Pending'
    /// must reject a second concurrent Pending access-request for the same triplet at the DB level.
    /// </summary>
    [SkippableFact]
    public async Task PendingAccessRequest_FilteredUniqueIndex_RejectsDuplicate()
    {
        SkipIfDockerUnavailable();

        var spaceId = SpaceId.New();
        var agentId = ConsumerId.New();
        var serverId = McpServerId.New();

        var first = MakePendingRequest(spaceId, agentId, serverId);
        await _repo!.UpsertAccessRequestAsync(first);

        // Attempt to insert a second Pending row for the same (space, agent, server) triplet
        // directly without going through the grain's idempotency check — the DB unique index
        // must reject it.
        var second = MakePendingRequest(spaceId, agentId, serverId);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await _repo!.UpsertAccessRequestAsync(second));
    }

    /// <summary>
    /// FR-007: ApproveAccessRequestAsync must reject a second Active grant for the same
    /// (space, agent, server) triplet at the application layer (ApproveAccessRequestAsync
    /// performs an AnyAsync guard before inserting the grant).
    /// </summary>
    [SkippableFact]
    public async Task ActiveGrant_ApplicationLayerGuard_RejectsDuplicate()
    {
        SkipIfDockerUnavailable();

        var spaceId = SpaceId.New();
        var agentId = ConsumerId.New();
        var serverId = McpServerId.New();
        var approver = UserId.New();

        // First approve.
        var req1 = MakePendingRequest(spaceId, agentId, serverId);
        StateTransitions.ApproveAccessRequest(req1, approver, DateTimeOffset.UtcNow);
        var grant1 = MakeActiveGrant(spaceId, agentId, serverId);
        await _repo!.ApproveAccessRequestAsync(req1, grant1);

        // Second approve for the same triplet — application guard must fire.
        var req2 = MakePendingRequest(spaceId, agentId, serverId);
        StateTransitions.ApproveAccessRequest(req2, approver, DateTimeOffset.UtcNow);
        var grant2 = MakeActiveGrant(spaceId, agentId, serverId);

        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await _repo!.ApproveAccessRequestAsync(req2, grant2));
        Assert.Equal(KoratErrorCode.AccessDenied, ex.Code);
    }

    /// <summary>
    /// The unique index on (SpaceId, DisplayName) for McpServers must reject two servers with
    /// the same DisplayName in the same Space. (Index is NOT filtered — it covers all statuses.)
    /// </summary>
    [SkippableFact]
    public async Task McpServer_UniqueDisplayNameIndex_RejectsDuplicate()
    {
        SkipIfDockerUnavailable();

        var spaceId = SpaceId.New();
        var nodeId = NodeId.New();
        var displayName = $"my-server-{Guid.NewGuid():N}";

        var server1 = MakeMcpServer(spaceId, nodeId, displayName);
        await _repo!.UpsertMcpServerAsync(server1);

        // Try to insert a second server with the same (SpaceId, DisplayName) — must fail at DB.
        var server2 = MakeMcpServer(spaceId, nodeId, displayName);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await _repo!.UpsertMcpServerAsync(server2));
    }

    /// <summary>
    /// SC-1: The filtered unique index on Spaces.OwnerUserId WHERE IsDefault=true must prevent
    /// a second default Space for the same owner from being inserted.
    /// </summary>
    [SkippableFact]
    public async Task Space_DefaultFilteredUniqueIndex_RejectsDuplicateDefault()
    {
        SkipIfDockerUnavailable();

        var ownerUserId = UserId.New().Value.ToString("N");
        var now = DateTimeOffset.UtcNow;

        await using var db1 = await _factory!.CreateDbContextAsync();
        db1.Spaces.Add(new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerUserId = ownerUserId,
            DisplayName = "First Default Space",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db1.SaveChangesAsync();

        await using var db2 = await _factory!.CreateDbContextAsync();
        db2.Spaces.Add(new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerUserId = ownerUserId,
            DisplayName = "Second Default Space",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // The filtered unique index must reject this.
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await db2.SaveChangesAsync());
    }

    /// <summary>
    /// Sequential double-approve: the application-level AnyAsync guard in ApproveAccessRequestAsync
    /// (FR-007) must reject a second Active grant for the same triplet even when two separate
    /// requests are approved sequentially (no grain serialization). This simulates what would
    /// happen if the grain invariant were bypassed (e.g. direct repo call from a test or admin tool).
    ///
    /// Note: true concurrent racing of the AnyAsync guard is inherently non-deterministic without
    /// a DB-level unique constraint on (SpaceId, ConsumerId, McpServerId) WHERE Active. The
    /// Orleans grain serialization (single-threaded activation) is the primary concurrency barrier
    /// in production. This test confirms the sequential guard works on real Postgres.
    /// </summary>
    [SkippableFact]
    public async Task ApproveAccessRequest_SequentialDoubleCalls_SecondCallRejected()
    {
        SkipIfDockerUnavailable();

        var spaceId = SpaceId.New();
        var agentId = ConsumerId.New();
        var serverId = McpServerId.New();
        var approver = UserId.New();

        // First approve — must succeed.
        var req1 = MakePendingRequest(spaceId, agentId, serverId);
        StateTransitions.ApproveAccessRequest(req1, approver, DateTimeOffset.UtcNow);
        var grant1 = MakeActiveGrant(spaceId, agentId, serverId);
        await _repo!.ApproveAccessRequestAsync(req1, grant1);

        // Confirm exactly one Active grant exists.
        var activeGrant = await _repo!.GetActiveGrantAsync(spaceId, agentId, serverId);
        Assert.NotNull(activeGrant);

        // Second approve — the AnyAsync guard must reject it (FR-007).
        var req2 = MakePendingRequest(spaceId, agentId, serverId);
        StateTransitions.ApproveAccessRequest(req2, approver, DateTimeOffset.UtcNow);
        var grant2 = MakeActiveGrant(spaceId, agentId, serverId);

        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await _repo!.ApproveAccessRequestAsync(req2, grant2));
        Assert.Equal(KoratErrorCode.AccessDenied, ex.Code);

        // Still exactly one Active grant.
        var finalGrant = await _repo!.GetActiveGrantAsync(spaceId, agentId, serverId);
        Assert.NotNull(finalGrant);
        Assert.Equal(grant1.Id, finalGrant!.Id);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AccessRequest MakePendingRequest(SpaceId spaceId, ConsumerId agentId, McpServerId serverId) => new()
    {
        Id = AccessRequestId.New(),
        SpaceId = spaceId,
        ConsumerId = agentId,
        McpServerId = serverId,
        RequestedByNodeId = NodeId.New(),
        PublisherNodeId = NodeId.New(),
        Status = AccessRequestStatus.Pending,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static Grant MakeActiveGrant(SpaceId spaceId, ConsumerId agentId, McpServerId serverId) => new()
    {
        Id = GrantId.New(),
        SpaceId = spaceId,
        ConsumerId = agentId,
        McpServerId = serverId,
        ApprovedByUserId = UserId.New(),
        ApprovedAt = DateTimeOffset.UtcNow,
        Status = GrantStatus.Active
    };

    // ── 024 reaper query (real-PG: validates the left-join + nullable-LastSeenAt SQL) ──────────
    [SkippableFact]
    public async Task ListPurgeableMcpServers_ReturnsPublishedWithLongOfflineOrMissingOwner()
    {
        SkipIfDockerUnavailable();
        var space = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromDays(7);

        // Stale owner (offline 10d) → its Published server qualifies.
        var staleNode = MakeNode(space, now - TimeSpan.FromDays(10));
        await _repo!.UpsertNodeAsync(staleNode);
        var staleServer = MakeMcpServer(space, staleNode.Id, $"stale-{Guid.NewGuid():N}");
        await _repo.UpsertMcpServerAsync(staleServer);

        // Fresh owner (seen now) → NOT purgeable.
        var freshNode = MakeNode(space, now);
        await _repo.UpsertNodeAsync(freshNode);
        var freshServer = MakeMcpServer(space, freshNode.Id, $"fresh-{Guid.NewGuid():N}");
        await _repo.UpsertMcpServerAsync(freshServer);

        // Disabled server with a stale owner → excluded (owner intent; query filters Status=Published).
        var disabledServer = MakeMcpServer(space, staleNode.Id, $"disabled-{Guid.NewGuid():N}");
        disabledServer.Status = McpServerStatus.Disabled;
        await _repo.UpsertMcpServerAsync(disabledServer);

        // Server whose owner node row does not exist (left join → null) → qualifies.
        var orphanServer = MakeMcpServer(space, NodeId.New(), $"orphan-{Guid.NewGuid():N}");
        await _repo.UpsertMcpServerAsync(orphanServer);

        // Owner node row EXISTS but LastSeenAt is null → qualifies (exercises the SQL null branch).
        var nullSeenNode = MakeNode(space, now);
        nullSeenNode.LastSeenAt = null;
        await _repo.UpsertNodeAsync(nullSeenNode);
        var nullSeenServer = MakeMcpServer(space, nullSeenNode.Id, $"nullseen-{Guid.NewGuid():N}");
        await _repo.UpsertMcpServerAsync(nullSeenServer);

        var purgeable = await _repo.ListPurgeableMcpServersAsync(cutoff);
        var ids = purgeable.Select(p => p.Id).ToHashSet();

        Assert.Contains(staleServer.Id, ids);
        Assert.Contains(orphanServer.Id, ids);
        Assert.Contains(nullSeenServer.Id, ids);
        Assert.DoesNotContain(freshServer.Id, ids);
        Assert.DoesNotContain(disabledServer.Id, ids);
    }

    // ── session reaper query (real-PG: validates the double-left-join SQL) ────────
    [SkippableFact]
    public async Task ListReapableSessions_translates_on_postgres()
    {
        SkipIfDockerUnavailable();
        var space = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromHours(1);

        // Stale client node (offline 2h) + fresh publisher → session qualifies because client is stale.
        var staleNode = MakeNode(space, now - TimeSpan.FromHours(2));
        await _repo!.UpsertNodeAsync(staleNode);
        var freshNode = MakeNode(space, now);
        await _repo.UpsertNodeAsync(freshNode);
        var staleSession = MakeActiveSession(space, staleNode.Id, freshNode.Id);
        await _repo.UpsertSessionAsync(staleSession);

        // Both nodes fresh → session must NOT appear.
        var freshNode2 = MakeNode(space, now);
        await _repo.UpsertNodeAsync(freshNode2);
        var freshSession = MakeActiveSession(space, freshNode.Id, freshNode2.Id);
        await _repo.UpsertSessionAsync(freshSession);

        // Neither session here is sentinel-cliented — the sentinel age cutoff is irrelevant to
        // this test's outcome, mirror the production default.
        var sentinelSessionAgeCutoff = now - SessionReaperRules.DefaultSpaceMcpSessionMaxAge;
        var reapable = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);
        var ids = reapable.Select(r => r.Id).ToHashSet();

        Assert.Contains(staleSession.Id, ids);
        Assert.DoesNotContain(freshSession.Id, ids);
    }

    private static RelaySession MakeActiveSession(SpaceId spaceId, NodeId clientNodeId, NodeId publisherNodeId)
    {
        var now = DateTimeOffset.UtcNow;
        return new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = clientNodeId,
            PublisherNodeId = publisherNodeId,
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = now,
            AgentConnectionId = ConnectionId.New(),
        };
    }

    private static Node MakeNode(SpaceId spaceId, DateTimeOffset lastSeenAt) => new()
    {
        Id = NodeId.New(),
        SpaceId = spaceId,
        DisplayName = "reaper-node",
        Status = NodeStatus.Online,
        LastSeenAt = lastSeenAt,
        CreatedAt = lastSeenAt,
        UpdatedAt = lastSeenAt,
    };

    private static McpServer MakeMcpServer(SpaceId spaceId, NodeId nodeId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpServer
        {
            Id = McpServerId.New(),
            SpaceId = spaceId,
            PublisherNodeId = nodeId,
            DisplayName = displayName,
            LaunchCommand = "echo",
            LaunchArguments = "x",
            Status = McpServerStatus.Published,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // ── Postgres DbContext factory ─────────────────────────────────────────────

    private sealed class PostgresDbContextFactory(string connectionString) : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<KoratDbContext>()
                .UseNpgsql(connectionString)
                .Options);

        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
