using System.Net;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Task-6 endpoint isolation tests: /api/space must serve only the caller's own
/// Space data. Two distinct authenticated users must not see each other's nodes.
///
/// Uses the Task-1 SeedUserAsync / CreateAuthenticatedClientAsync helpers so
/// each test user goes through the real production provisioning seam.
/// </summary>
public sealed class NodeSpaceIsolationTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private Node MakeNode(string spaceId, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Node
        {
            Id = NodeId.New(),
            SpaceId = new SpaceId(spaceId),
            DisplayName = name,
            Status = NodeStatus.Offline,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SpaceOverview_DoesNotLeakAnotherUsersNodes()
    {
        // Arrange: seed two users, each with their own default Space.
        var a = await fixture.SeedUserAsync("a6-node@x.io", "A6-Node");
        var b = await fixture.SeedUserAsync("b6-node@x.io", "B6-Node");

        // Seed a node directly into user A's SpaceGrain (grain-keyed by A's SpaceId).
        var nodeA = MakeNode(a.SpaceId, "node-isolation-a6");
        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        await grainA.RegisterNodeAsync(nodeA);

        // Create authenticated clients for each user via the session-cookie path.
        using var clientA = await fixture.CreateAuthenticatedClientAsync(a.UserId);
        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: each user fetches their space overview.
        var respA = await clientA.GetAsync("/api/space");
        var respB = await clientB.GetAsync("/api/space");

        // Assert: user A sees their own node.
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var bodyA = await respA.Content.ReadAsStringAsync();
        Assert.Contains(nodeA.Id.Value, bodyA);

        // Assert: user B does not see user A's node (cross-Space isolation — SC-8).
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var bodyB = await respB.Content.ReadAsStringAsync();
        Assert.DoesNotContain(nodeA.Id.Value, bodyB);
    }

    [Fact]
    public async Task SpaceOverview_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient().GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
