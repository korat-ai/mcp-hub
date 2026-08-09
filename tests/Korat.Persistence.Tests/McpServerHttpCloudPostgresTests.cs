using Korat.Domain;
using Korat.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Korat.Persistence.Tests;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space), Task 1: Postgres-backed proof that (a) the new
/// EncryptedSecret column survives an unrelated UpsertMcpServerAsync call (the IsModified=false
/// guard, mirroring InferencePointRecord's identical hazard), (b) McpServerReaperService's
/// query no longer treats an http_cloud server (PublisherNodeId always empty by design) as an
/// orphan — see plan Crux Finding 7 / EfMetadataRepository.ListPurgeableMcpServersAsync — and
/// (c) SessionReaperService's query (ListReapableSessionsAsync) has the IDENTICAL bug for a LIVE
/// http_cloud SESSION and is fixed the same way (Finding 16, B1): the client-node OR-clause is
/// transport-agnostic and stays; the publisher-node OR-clause must not fire for an http_cloud
/// session's always-empty PublisherNodeId.
/// InMemory does not exercise the real LEFT JOIN SQL translation — this must run on Postgres.
/// (`tests/Korat.Persistence.Tests/SessionReaperRepositoryTests.cs` already covers
/// ListReapableSessionsAsync's non-http_cloud behavior, but on the InMemory provider via
/// PersistenceTestFixture — the exact InMemory-green/Postgres-broken gap this class exists to
/// close; that test's own fixtures are untouched by this change.)
/// </summary>
public sealed class McpServerHttpCloudPostgresTests : IAsyncLifetime
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

    [SkippableFact]
    public async Task SetMcpServerSecret_SurvivesUnrelatedUpsert()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var server = new McpServer
        {
            Id = McpServerId.New(),
            SpaceId = spaceId,
            PublisherNodeId = new NodeId(string.Empty),
            DisplayName = "http-srv-1",
            Transport = McpServerTransports.HttpCloud,
            RemoteUrl = "https://example.test/mcp",
            AuthMode = McpServerAuthModes.Bearer,
            Status = McpServerStatus.Published,
            IsAsserted = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertMcpServerAsync(server);
        await _repo.SetMcpServerSecretAsync(server.Id, "kenv1.fake.ciphertext", "…ab12");

        // Unrelated update through the normal domain-entity path (e.g. a PATCH that only
        // touches RemoteUrl) — must NOT null out EncryptedSecret.
        server.RemoteUrl = "https://example.test/mcp/v2";
        await _repo.UpsertMcpServerAsync(server);

        var ciphertext = await _repo.GetMcpServerSecretCiphertextAsync(server.Id);
        Assert.Equal("kenv1.fake.ciphertext", ciphertext);
    }

    [SkippableFact]
    public async Task ListPurgeableMcpServers_ExcludesHttpCloud()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        // An http_cloud server: no owner node by design (PublisherNodeId == "").
        var httpCloudServer = new McpServer
        {
            Id = McpServerId.New(),
            SpaceId = spaceId,
            PublisherNodeId = new NodeId(string.Empty),
            DisplayName = "http-srv-2",
            Transport = McpServerTransports.HttpCloud,
            Status = McpServerStatus.Published,
            IsAsserted = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertMcpServerAsync(httpCloudServer);

        // Cutoff far in the future — would catch ANY row with a missing/stale owner node,
        // proving the exclusion is by Transport, not by staleness window.
        var farFutureCutoff = DateTimeOffset.UtcNow.AddYears(10);
        var purgeable = await _repo.ListPurgeableMcpServersAsync(farFutureCutoff);

        Assert.DoesNotContain(purgeable, p => p.Id == httpCloudServer.Id);
    }

    /// <summary>
    /// Finding 16, B1: the session reaper has the SAME bug as the server reaper, independently —
    /// ListReapableSessionsAsync LEFT JOINs Sessions→Nodes on BOTH ClientNodeId and
    /// PublisherNodeId and reaps when EITHER is missing/stale. An http_cloud session's
    /// PublisherNodeId is "" by design → the publisher-side OR-clause fires unconditionally →
    /// every live http_cloud session would be force-closed within SessionReaperService's 1-hour
    /// sweep, even with its client node perfectly healthy. Far-future cutoff proves the exclusion
    /// is by Transport, not by staleness window (mirrors ListPurgeableMcpServers_ExcludesHttpCloud
    /// above).
    /// </summary>
    [SkippableFact]
    public async Task ListReapableSessions_HttpCloudSession_SurvivesWithHealthyClientNode()
    {
        Skip.If(_dockerUnavailableReason is not null, _dockerUnavailableReason);
        var spaceId = SpaceId.New();
        await SeedSpaceAsync(spaceId);

        var httpCloudServer = new McpServer
        {
            Id = McpServerId.New(),
            SpaceId = spaceId,
            PublisherNodeId = new NodeId(string.Empty),
            DisplayName = "http-srv-3",
            Transport = McpServerTransports.HttpCloud,
            RemoteUrl = "https://example.test/mcp",
            Status = McpServerStatus.Published,
            IsAsserted = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo!.UpsertMcpServerAsync(httpCloudServer);

        // A healthy, freshly-seen client node — the session must survive on the strength of
        // this alone, since http_cloud has no publisher node to be stale/missing.
        var clientNode = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "healthy-client",
            Status = NodeStatus.Online,
            LastSeenAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo.UpsertNodeAsync(clientNode);

        var session = new RelaySession
        {
            Id = SessionId.New(),
            SpaceId = spaceId,
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = httpCloudServer.Id,
            ClientNodeId = clientNode.Id,
            PublisherNodeId = new NodeId(string.Empty),
            HomeGatewayId = GatewayId.New(),
            Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow
        };
        await _repo.UpsertSessionAsync(session);

        // NOTE (deviation from the plan's literal snippet): the plan used a "far future" cutoff
        // here (mirroring ListPurgeableMcpServers_ExcludesHttpCloud above), but that test has no
        // client-node concept to break — this one does. Production always calls this method with
        // cutoff = DateTimeOffset.UtcNow - ReapGrace (see SessionReaperService.cs:59), i.e. cutoff
        // is always in the PAST. A "far future" cutoff makes `cn.LastSeenAt < cutoff` trivially
        // true for ANY real timestamp (since nothing can be later than 10 years from now),
        // tripping the fully transport-agnostic client-node OR-clause regardless of the fix under
        // test and failing this assertion for a reason unrelated to http_cloud exclusion. A
        // recent-past cutoff (mirrors SessionReaperRepositoryTests.cs's own convention) correctly
        // exercises the intended proof: PublisherNodeId == "" means `pn == null` unconditionally
        // (a genuinely missing row, not a stale timestamp) for this session, so the OLD query
        // would reap it regardless of cutoff sign — while the client node stays fresh (not < a
        // recent-past cutoff), isolating the assertion to the publisher-side/Transport fix.
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        // No sentinel-cliented session here — the sentinel age cutoff is irrelevant to this
        // test's outcome, mirror the production default.
        var sentinelSessionAgeCutoff = DateTimeOffset.UtcNow - SessionReaperRules.DefaultSpaceMcpSessionMaxAge;
        var reapable = await _repo.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff);

        Assert.DoesNotContain(reapable, r => r.Id == session.Id);
    }

    private async Task SeedSpaceAsync(SpaceId spaceId)
    {
        await using var ctx = new KoratDbContext(new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(_pg!.GetConnectionString()).Options);
        ctx.Spaces.Add(new SpaceRecord
        {
            Id = spaceId.Value,
            // NOTE (deviation from the plan's literal snippet): SpaceRecord.OwnerUserId is a
            // string ("N"-format Guid), not a System.Guid — confirmed against every other
            // Postgres test in this project (e.g. InferencePointRepositoryTests.cs) that seeds a
            // SpaceRecord the same way.
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
