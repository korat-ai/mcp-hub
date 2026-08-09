using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// #165 (`korat nodes prune`): contract tests for <c>POST /api/nodes/prune</c> — owner-scoped
/// bulk GC of stale agent-kind nodes. Mirrors NodeNoteEndpointTests' fixture/auth idioms.
/// </summary>
public sealed class NodesPruneEndpointTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static async Task<NodeId> RegisterNodeAsync(
        KoratIntegrationFixture fixture, string spaceId, string displayName, NodeKind kind,
        DateTimeOffset? lastSeenAt, DateTimeOffset createdAt)
    {
        var nodeId = NodeId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId);
        await grain.RegisterNodeAsync(new Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(spaceId),
            DisplayName = displayName,
            Status = NodeStatus.Offline,
            Kind = kind,
            LastSeenAt = lastSeenAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        return nodeId;
    }

    private static StringContent PruneBody(string kind, int? olderThanDays = null) =>
        new(JsonSerializer.Serialize(new { kind, olderThanDays }), Encoding.UTF8, "application/json");

    private static StringContent RawBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Prune_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient()
            .PostAsync("/api/nodes/prune", PruneBody("agent"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Prune_KindPublisher_Returns400_AndDeletesNothing()
    {
        var owner = await fixture.SeedUserAsync($"prune-kind-pub-{Guid.NewGuid():N}@x.io", "Prune KindPublisher");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(owner.SpaceId);
        var now = DateTimeOffset.UtcNow;
        var publisherId = await RegisterNodeAsync(fixture, owner.SpaceId, "old-publisher", NodeKind.Publisher,
            lastSeenAt: now.AddDays(-365), createdAt: now.AddDays(-400));

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("publisher"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var nodes = await space.ListNodesAsync();
        Assert.Contains(nodes, n => n.Id == publisherId);
    }

    [Fact]
    public async Task Prune_UnknownKind_Returns400()
    {
        var owner = await fixture.SeedUserAsync($"prune-kind-unknown-{Guid.NewGuid():N}@x.io", "Prune KindUnknown");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("bogus"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Prune_OlderThanDaysZero_Returns400()
    {
        var owner = await fixture.SeedUserAsync($"prune-older0-{Guid.NewGuid():N}@x.io", "Prune Older0");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("agent", 0));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Prune_OlderThanDaysNegative_Returns400()
    {
        var owner = await fixture.SeedUserAsync($"prune-oldernegative-{Guid.NewGuid():N}@x.io", "Prune OlderNegative");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("agent", -5));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Prune_OwnerOk_PrunesStaleAgent_KeepsFreshAgentAndPublisher()
    {
        var owner = await fixture.SeedUserAsync($"prune-owner-ok-{Guid.NewGuid():N}@x.io", "Prune OwnerOk");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(owner.SpaceId);
        var now = DateTimeOffset.UtcNow;

        var staleAgentId = await RegisterNodeAsync(fixture, owner.SpaceId, "stale-agent", NodeKind.Agent,
            lastSeenAt: now.AddDays(-45), createdAt: now.AddDays(-90));
        var freshAgentId = await RegisterNodeAsync(fixture, owner.SpaceId, "fresh-agent", NodeKind.Agent,
            lastSeenAt: now.AddMinutes(-5), createdAt: now.AddDays(-60));
        var publisherId = await RegisterNodeAsync(fixture, owner.SpaceId, "old-publisher", NodeKind.Publisher,
            lastSeenAt: now.AddDays(-365), createdAt: now.AddDays(-400));

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("agent", 30));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("prunedCount").GetInt32());
        var prunedNames = body.GetProperty("prunedNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("stale-agent", prunedNames);

        var nodes = await space.ListNodesAsync();
        Assert.DoesNotContain(nodes, n => n.Id == staleAgentId);
        Assert.Contains(nodes, n => n.Id == freshAgentId);
        Assert.Contains(nodes, n => n.Id == publisherId);
    }

    [Fact]
    public async Task Prune_OlderThanDaysOmitted_DefaultsTo30()
    {
        var owner = await fixture.SeedUserAsync($"prune-default30-{Guid.NewGuid():N}@x.io", "Prune Default30");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(owner.SpaceId);
        var now = DateTimeOffset.UtcNow;

        var beyond30Id = await RegisterNodeAsync(fixture, owner.SpaceId, "agent-31d", NodeKind.Agent,
            lastSeenAt: now.AddDays(-31), createdAt: now.AddDays(-60));
        var within30Id = await RegisterNodeAsync(fixture, owner.SpaceId, "agent-10d", NodeKind.Agent,
            lastSeenAt: now.AddDays(-10), createdAt: now.AddDays(-60));

        // No "olderThanDays" property at all — exercises the server-side default (30), distinct
        // from an explicit value.
        var resp = await client.PostAsync("/api/nodes/prune", RawBody("""{"kind":"agent"}"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var nodes = await space.ListNodesAsync();
        Assert.DoesNotContain(nodes, n => n.Id == beyond30Id);
        Assert.Contains(nodes, n => n.Id == within30Id);
    }

    [Fact]
    public async Task Prune_NoMatches_Returns200_WithEmptyResult()
    {
        var owner = await fixture.SeedUserAsync($"prune-nomatches-{Guid.NewGuid():N}@x.io", "Prune NoMatches");
        using var client = await fixture.CreateAuthenticatedClientAsync(owner.UserId);
        var now = DateTimeOffset.UtcNow;
        await RegisterNodeAsync(fixture, owner.SpaceId, "fresh-agent", NodeKind.Agent,
            lastSeenAt: now, createdAt: now.AddDays(-1));

        var resp = await client.PostAsync("/api/nodes/prune", PruneBody("agent", 30));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("prunedCount").GetInt32());
        Assert.Empty(body.GetProperty("prunedNames").EnumerateArray());
    }

    [Fact]
    public async Task Prune_IsSpaceIsolated_OwnerBPruneDoesNotTouchOwnerASpace()
    {
        var a = await fixture.SeedUserAsync($"prune-isolation-a-{Guid.NewGuid():N}@x.io", "Prune Isolation A");
        var b = await fixture.SeedUserAsync($"prune-isolation-b-{Guid.NewGuid():N}@x.io", "Prune Isolation B");
        var spaceA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var now = DateTimeOffset.UtcNow;

        var staleInA = await RegisterNodeAsync(fixture, a.SpaceId, "stale-agent-in-a", NodeKind.Agent,
            lastSeenAt: now.AddDays(-45), createdAt: now.AddDays(-90));

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);
        var resp = await clientB.PostAsync("/api/nodes/prune", PruneBody("agent", 30));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("prunedCount").GetInt32());

        // Space A's stale node was never touched by B's prune — it only ever acts on the
        // caller's OWN resolved default space (no id parameter for a caller to redirect).
        var nodesA = await spaceA.ListNodesAsync();
        Assert.Contains(nodesA, n => n.Id == staleInA);
    }
}
