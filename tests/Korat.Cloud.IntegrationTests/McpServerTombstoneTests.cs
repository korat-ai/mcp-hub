using Grpc.Core;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Korat.Relay.V1;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

public sealed class McpServerTombstoneTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private IMetadataRepository Repo()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
    }

    [Fact]
    public async Task Tombstone_add_exists_remove_list_round_trip()
    {
        var repo = Repo();
        var space = SpaceId.New();
        var node = NodeId.New();
        var owner = KoratIntegrationFixture.DevSpaceOwnerUserId;

        Assert.False(await repo.TombstoneExistsAsync(space, node, "foo"));

        await repo.AddTombstoneAsync(space, node, "foo", owner);
        await repo.AddTombstoneAsync(space, node, "foo", owner); // idempotent upsert — no throw

        Assert.True(await repo.TombstoneExistsAsync(space, node, "foo"));
        // Node-scoped: a different node's "foo" is NOT tombstoned.
        Assert.False(await repo.TombstoneExistsAsync(space, NodeId.New(), "foo"));

        var list = await repo.ListTombstonesForNodeAsync(space, node);
        Assert.Contains(list, t => t.DisplayName == "foo");

        await repo.RemoveTombstoneAsync(space, node, "foo");
        Assert.False(await repo.TombstoneExistsAsync(space, node, "foo"));
        await repo.RemoveTombstoneAsync(space, node, "foo"); // idempotent — no throw on missing
    }

    [Fact]
    public async Task Tombstoned_node_name_is_not_recreated_on_publish()
    {
        var repo = Repo();
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"tomb-{Guid.NewGuid():N}";

        // Pre-tombstone the (node, name) directly via the repo, then attempt to publish it.
        await repo.AddTombstoneAsync(spaceId, node, name, KoratIntegrationFixture.DevSpaceOwnerUserId);

        var result = await space.PublishMcpServerAsync(node, name, "echo", "x");

        Assert.Null(result); // refused — tombstone blocks brand-new creation
        var servers = await space.ListMcpServersAsync();
        Assert.DoesNotContain(servers, s => s.DisplayName == name);
    }

    // ── Task 3: owner delete writes tombstone; reaper path does NOT ──────────────────────────────

    [Fact]
    public async Task Owner_delete_writes_tombstone()
    {
        var repo = Repo();
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"del-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(server);

        var result = await space.DeleteMcpServerAsync(server!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        Assert.True(result.Deleted);
        Assert.True(await repo.TombstoneExistsAsync(spaceId, node, name));
    }

    [Fact]
    public async Task Reaper_path_delete_does_NOT_write_tombstone()
    {
        var repo = Repo();
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"reap-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(server);

        // Reaper passes writeTombstone:false — a returning node may re-publish.
        var result = await space.DeleteMcpServerAsync(server!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId, writeTombstone: false);

        Assert.True(result.Deleted);
        Assert.False(await repo.TombstoneExistsAsync(spaceId, node, name));

        // And a re-publish of the same (node, name) succeeds (not blocked).
        var republished = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(republished);
    }

    // ── Task 4: SyncMcpServersAsync skip-null Pass 1 + CLEAR pass ─────────────────────────────────

    [Fact]
    public async Task Sync_redeclaring_deleted_name_does_not_resurrect()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"res-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(server);
        await space.DeleteMcpServerAsync(server!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Node reconnects and re-declares the deleted name (the bug scenario).
        var synced = await space.SyncMcpServersAsync(node, new[] { new McpServerSpec(name, "echo", "x") });

        Assert.DoesNotContain(synced, s => s.DisplayName == name); // null skipped in Pass 1
        var servers = await space.ListMcpServersAsync();
        Assert.DoesNotContain(servers, s => s.DisplayName == name);
    }

    [Fact]
    public async Task Sync_is_node_scoped_other_node_same_name_is_created()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var nodeA = NodeId.New();
        var nodeB = NodeId.New();
        var name = $"scope-{Guid.NewGuid():N}";

        var a = await space.PublishMcpServerAsync(nodeA, name, "echo", "x");
        await space.DeleteMcpServerAsync(a!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Node B declaring its OWN "name" is created — tombstone is keyed by node.
        var synced = await space.SyncMcpServersAsync(nodeB, new[] { new McpServerSpec(name, "echo", "y") });
        Assert.Contains(synced, s => s.DisplayName == name);
    }

    [Fact]
    public async Task Sync_clears_tombstone_when_name_dropped_then_readd_works()
    {
        var repo = Repo();
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"clr-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        await space.DeleteMcpServerAsync(server!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);
        Assert.True(await repo.TombstoneExistsAsync(spaceId, node, name));

        // Node drops the name from its config → sync WITHOUT it → CLEAR pass removes the tombstone.
        await space.SyncMcpServersAsync(node, Array.Empty<McpServerSpec>());
        Assert.False(await repo.TombstoneExistsAsync(spaceId, node, name));

        // Re-add (korat mcp add) → next sync declares it → no tombstone → created.
        var synced = await space.SyncMcpServersAsync(node, new[] { new McpServerSpec(name, "echo", "x") });
        Assert.Contains(synced, s => s.DisplayName == name);
    }

    [Fact]
    public async Task Sync_omitting_nondeleted_server_soft_retires_not_tombstones()
    {
        // 021 regression guard: a normal reconnect that omits a (never-deleted) server soft-retires it
        // (IsAsserted=false, row stays), and does NOT create a tombstone — re-declaring re-asserts it.
        var repo = Repo();
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"retire-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(server);

        // Sync omitting the name (transient partial config) — soft-retire, NOT delete.
        await space.SyncMcpServersAsync(node, Array.Empty<McpServerSpec>());
        Assert.False(await repo.TombstoneExistsAsync(spaceId, node, name)); // no tombstone
        var afterRetire = await space.ListMcpServersAsync();
        Assert.Contains(afterRetire, s => s.DisplayName == name && !s.IsAsserted); // row stays, retired

        // Re-declaring re-asserts it.
        var synced = await space.SyncMcpServersAsync(node, new[] { new McpServerSpec(name, "echo", "x") });
        Assert.Contains(synced, s => s.DisplayName == name && s.IsAsserted);
    }

    // ── Task 6: Durability — tombstone survives SpaceGrain rehydrate ─────────────────────────────

    /// <summary>
    /// Prove that the delete-tombstone survives a SpaceGrain deactivation/rehydrate.
    /// The tombstone is a repository (DB) row, not grain in-memory state — so a fresh grain
    /// activation still reads it and refuses re-publication. This is a regression guard: if
    /// the consult were wrongly scoped to grain state, it would silently pass after rehydrate.
    ///
    /// Mechanism: call <see cref="ISpaceGrain.InvalidateCacheAsync"/> which drops the in-memory
    /// cache; the next grain call re-hydrates from the DB. The tombstone row is unaffected.
    /// </summary>
    [Fact]
    public async Task Tombstone_survives_grain_rehydrate_and_still_refuses()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = NodeId.New();
        var name = $"dur-{Guid.NewGuid():N}";

        var server = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.NotNull(server);
        await space.DeleteMcpServerAsync(server!.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Force a re-hydrate (drops the in-memory cache; tombstone lives in the repo, not grain state).
        await space.InvalidateCacheAsync();

        var result = await space.PublishMcpServerAsync(node, name, "echo", "x");
        Assert.Null(result); // still refused after rehydrate — durable
    }

    // ── Task 5: gRPC PublishMcpServer handler + dev endpoint handle null result ─────────────────

    /// <summary>
    /// Sends a PublishMcpServer message over the live gRPC stream for a pre-tombstoned (node, name)
    /// and asserts the gateway replies with AccessDenied (not PublishMcpServerAck) — the null result
    /// from PublishMcpServerAsync is handled gracefully without an NRE or crash.
    ///
    /// The error reason is KoratErrorCode.NotFound (reused from the existing duplicate-name path;
    /// no more-specific enum member exists — see KoratErrorCode in src/Korat.Domain/States.cs).
    /// Covered-by-construction comment: the guard at NodeGatewayService.cs:582-595 is the code under
    /// test; the grain-level refusal is locked by Tombstoned_node_name_is_not_recreated_on_publish.
    /// </summary>
    [Fact]
    public async Task GrpcPublish_tombstoned_name_returns_AccessDenied()
    {
        // Seed a user + space with a real CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"t5-grpc-{Guid.NewGuid():N}@example.com", "T5 gRPC Tombstone Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        // Pick a node id and pre-tombstone the name directly in the repo.
        var nodeId = NodeId.New();
        var spaceId = new SpaceId(seeded.SpaceId);
        var name = $"t5-{Guid.NewGuid():N}";

        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await repo.AddTombstoneAsync(spaceId, nodeId, name, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Connect as a publisher node (Bearer auth — Hello without NodeAuthToken is the Bearer path).
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        using var call = grpcClient.Connect(GrpcTestClient.BearerCallOptions(cliToken));
        var requestId = Guid.NewGuid().ToString("N");

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId.Value,
                DisplayName = "t5-publisher"
            }
        });

        // Consume GatewayHello.
        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await call.ResponseStream.MoveNext(helloCts.Token));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        // Send PublishMcpServer for the tombstoned name.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            PublishMcpServer = new PublishMcpServer
            {
                RequestId = requestId,
                NodeId = nodeId.Value,
                DisplayName = name,
                Command = "echo",
                Args = { "x" }
            }
        });

        // Expect AccessDenied (not PublishMcpServerAck) — the null-guard fires.
        using var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await call.ResponseStream.MoveNext(ackCts.Token));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal(requestId, response.AccessDenied.RequestId);
        // Reason must be non-empty (KoratErrorCode.NotFound message).
        Assert.False(string.IsNullOrEmpty(response.AccessDenied.Reason));

        await call.RequestStream.CompleteAsync();
    }
}
