using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Task-5 grain tests: SpaceGrain must hold nodes in real in-memory state keyed by SpaceId.
/// SC-6: a grain keyed SpaceId_A never returns SpaceId_B rows.
/// F2: ListNodesAsync and ListMcpServersAsync must serve from in-memory state (not forwarding).
/// </summary>
public sealed class SpaceGrainStateTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private Node MakeNode(SpaceId spaceId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = name,
            Status = NodeStatus.Offline,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task SeedNodeInRepoAsync(Node node)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await repo.UpsertNodeAsync(node);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNodesAsync_ReturnsOnlyThisSpacesNodes()
    {
        // Arrange: two distinct Spaces, each with one node seeded directly in the repository.
        var spaceA = SpaceId.New();
        var spaceB = SpaceId.New();
        var nodeA = MakeNode(spaceA, "node-a");
        var nodeB = MakeNode(spaceB, "node-b");
        await SeedNodeInRepoAsync(nodeA);
        await SeedNodeInRepoAsync(nodeB);

        // Act: activate a SpaceGrain for each Space and list nodes.
        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceA.Value);
        var grainB = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceB.Value);
        var nodesA = await grainA.ListNodesAsync();
        var nodesB = await grainB.ListNodesAsync();

        // Assert: isolation — A never returns B's nodes and vice-versa (SC-6).
        Assert.Contains(nodesA, n => n.Id == nodeA.Id);
        Assert.DoesNotContain(nodesA, n => n.Id == nodeB.Id);
        Assert.Contains(nodesB, n => n.Id == nodeB.Id);
        Assert.DoesNotContain(nodesB, n => n.Id == nodeA.Id);
    }

    [Fact]
    public async Task RegisterNodeAsync_ThenListNodesAsync_ReturnsRegisteredNode()
    {
        // Arrange: empty space, register a node via the grain write path.
        var spaceId = SpaceId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var node = MakeNode(spaceId, "write-through-node");

        // Act: write via grain, then read back.
        await grain.RegisterNodeAsync(node);
        var listed = await grain.ListNodesAsync();

        // Assert: the written node appears in the list.
        Assert.Contains(listed, n => n.Id == node.Id);
    }

    [Fact]
    public async Task RegisterThenList_ServesFromMemory_AfterFirstLoad()
    {
        // Arrange: seed a node in the repository, then activate the grain (hydrate).
        var spaceId = SpaceId.New();
        var seeded = MakeNode(spaceId, "hydrated-node");
        await SeedNodeInRepoAsync(seeded);

        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        // First call hydrates from the repository.
        var firstList = await grain.ListNodesAsync();
        Assert.Contains(firstList, n => n.Id == seeded.Id);

        // Now add another node directly to the repository (bypassing the grain).
        // If the grain re-queries the repository on the second call, it would see both nodes.
        // If it serves from in-memory state (no re-query), it will only see the seeded node.
        var stale = MakeNode(spaceId, "stale-node-repo-only");
        await SeedNodeInRepoAsync(stale);

        // Second call: grain must serve from in-memory state — should NOT see the stale node.
        var secondList = await grain.ListNodesAsync();
        Assert.Contains(secondList, n => n.Id == seeded.Id);
        Assert.DoesNotContain(secondList, n => n.Id == stale.Id);
    }

    // ── 021: declarative SyncMcpServers reconcile ────────────────────────────────

    [Fact]
    public async Task SyncMcpServersAsync_UpsertsSetAndSoftRetiresAbsent()
    {
        // Arrange: one publisher node with two published servers (A, B), both asserted.
        var spaceId = SpaceId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var publisher = NodeId.New();
        var nameA = $"sync-a-{Guid.NewGuid():N}";
        var nameB = $"sync-b-{Guid.NewGuid():N}";
        var nameC = $"sync-c-{Guid.NewGuid():N}";
        var a = (await grain.PublishMcpServerAsync(publisher, nameA, "echo", "a"))!;
        var b = (await grain.PublishMcpServerAsync(publisher, nameB, "echo", "b"))!;
        Assert.True(a.IsAsserted);
        Assert.True(b.IsAsserted);

        // Act: declarative sync with {A (updated), C (new)} — B is ABSENT.
        var synced = await grain.SyncMcpServersAsync(publisher, new[]
        {
            new McpServerSpec(nameA, "echo", "a2"),
            new McpServerSpec(nameC, "echo", "c"),
        });

        // Assert: A keeps its stable id (idempotent upsert), C is created, both asserted.
        Assert.Contains(synced, s => s.Id == a.Id && s.IsAsserted);
        Assert.Contains(synced, s => s.DisplayName == nameC && s.IsAsserted);

        // B was omitted → soft-retired (IsAsserted=false), but NOT hard-deleted (row still present).
        var all = await grain.ListMcpServersAsync();
        var bAfter = Assert.Single(all, s => s.Id == b.Id);
        Assert.False(bAfter.IsAsserted);
        // A is still asserted after the sync.
        Assert.True(Assert.Single(all, s => s.Id == a.Id).IsAsserted);
    }

    [Fact]
    public async Task SyncMcpServersAsync_IsIdempotent()
    {
        // Arrange + Act: sync the same single-server set twice.
        var spaceId = SpaceId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var publisher = NodeId.New();
        var name = $"sync-idem-{Guid.NewGuid():N}";

        var first = await grain.SyncMcpServersAsync(publisher, new[] { new McpServerSpec(name, "echo", "x") });
        var second = await grain.SyncMcpServersAsync(publisher, new[] { new McpServerSpec(name, "echo", "x") });

        // Assert: same stable id, still asserted, no duplicate rows.
        var firstId = Assert.Single(first).Id;
        Assert.Equal(firstId, Assert.Single(second).Id);
        var all = await grain.ListMcpServersAsync();
        Assert.Single(all, s => s.Id == firstId && s.IsAsserted);
    }
}
