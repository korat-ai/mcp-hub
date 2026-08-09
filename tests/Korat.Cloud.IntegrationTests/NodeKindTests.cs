using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 017: NodeKind persistence and API exposure tests.
/// Verifies that Agent-kind nodes round-trip through the grain/repository and appear
/// correctly in the /api/space overview.
/// </summary>
public sealed class NodeKindTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Node> SeedNodeAsync(SpaceId spaceId, string name, NodeKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        var node = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = name,
            Status = NodeStatus.Offline,
            Kind = kind,
            CreatedAt = now,
            UpdatedAt = now,
        };

        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await repo.UpsertNodeAsync(node);
        return node;
    }

    // ── Grain-level tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterNodeAsync_AgentKind_PersistedAndReturnedByListNodesAsync()
    {
        // Arrange: seed an Agent node directly via the repository.
        var spaceId = SpaceId.New();
        var agentNode = await SeedNodeAsync(spaceId, "agent-node-persist", NodeKind.Agent);

        // Act: activate the SpaceGrain and list nodes (hydrates from DB).
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var listed = await grain.ListNodesAsync();

        // Assert: the Agent node is present with Kind=Agent.
        var match = listed.SingleOrDefault(n => n.Id == agentNode.Id);
        Assert.NotNull(match);
        Assert.Equal(NodeKind.Agent, match.Kind);
    }

    [Fact]
    public async Task RegisterNodeAsync_DefaultKind_IsPublisher()
    {
        // Arrange: seed a node without explicitly setting Kind (should default to Publisher).
        var spaceId = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var node = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "default-kind-node",
            Status = NodeStatus.Offline,
            // Kind not set — relies on the domain default (Publisher).
            CreatedAt = now,
            UpdatedAt = now,
        };
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            await repo.UpsertNodeAsync(node);
        }

        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var listed = await grain.ListNodesAsync();

        var match = listed.SingleOrDefault(n => n.Id == node.Id);
        Assert.NotNull(match);
        Assert.Equal(NodeKind.Publisher, match.Kind);
    }

    [Fact]
    public async Task RegisterNodeAsync_ViaGrainWritePath_AgentKindRoundTrips()
    {
        // Arrange: write an Agent node via RegisterNodeAsync (the grain write path,
        // which NodeGatewayService calls after ConnectAsync).
        var spaceId = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        var node = new Node
        {
            Id = NodeId.New(),
            SpaceId = spaceId,
            DisplayName = "agent-write-path",
            Status = NodeStatus.Online,
            Kind = NodeKind.Agent,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        await grain.RegisterNodeAsync(node);
        var listed = await grain.ListNodesAsync();

        var match = listed.SingleOrDefault(n => n.Id == node.Id);
        Assert.NotNull(match);
        Assert.Equal(NodeKind.Agent, match.Kind);
    }

    [Fact]
    public async Task SpaceOverviewEndpoint_ExposesKindAsLowercaseString()
    {
        // Arrange: seed an Agent node + a Publisher node bound to the dev fixture Space.
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var agentNode = await SeedNodeAsync(spaceId, $"agent-api-{Guid.NewGuid():N}", NodeKind.Agent);
        var pubNode   = await SeedNodeAsync(spaceId, $"pub-api-{Guid.NewGuid():N}",   NodeKind.Publisher);

        // Act: call /api/space as the authenticated owner and parse the JSON.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        // Invalidate the SpaceGrain cache so the newly-seeded nodes appear in the response
        // (the grain hydrated before we seeded; calling ListNodesAsync via InvalidateCacheAsync
        // resets the flag, forcing re-hydration on the next read).
        await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .InvalidateCacheAsync();

        var json = await client.GetStringAsync("/api/space");

        // Assert: kind field appears in the JSON response (case-insensitive search).
        Assert.Contains("\"kind\"", json, StringComparison.OrdinalIgnoreCase);
        // The specific nodes we seeded should show the correct kind strings.
        // We check the raw JSON; the SPA relies on lowercase "agent" / "publisher".
        Assert.Contains(agentNode.Id.Value, json);
        Assert.Contains("agent", json);
        Assert.Contains(pubNode.Id.Value, json);
        Assert.Contains("publisher", json);
    }
}
