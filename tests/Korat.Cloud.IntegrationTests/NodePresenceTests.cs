using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

public sealed class NodePresenceTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task RegisteredNode_AppearsInSpaceOverview()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        var nodeId = NodeId.New();
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(new Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(fixture.LegacyOwnerSpaceId),
            DisplayName = "test-node",
            Status = NodeStatus.Online,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        // W3: /api/space now requires owner auth.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var json = await client.GetStringAsync("/api/space");
        Assert.Contains("test-node", json);
        Assert.Contains(nodeId.Value, json);
    }
}

public sealed class McpServerPublicationGrainTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task PublishMcpServer_PersistsMetadata()
    {
        var nodeId = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "filesystem", "npx", "-y server"))!;

        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await repository.UpsertMcpServerAsync(server);
        var persisted = await repository.GetMcpServerAsync(server.Id);
        Assert.NotNull(persisted);
        Assert.Equal("filesystem", persisted.DisplayName);
    }

    [Fact]
    public async Task PublishMcpServer_DuplicateName_IsRejected()
    {
        var nodeId = NodeId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        await space.PublishMcpServerAsync(nodeId, "dup-server", "echo", "one");
        await Record.ExceptionAsync(() => space.PublishMcpServerAsync(nodeId, "dup-server", "echo", "two"));

        var servers = await space.ListMcpServersAsync();
        Assert.Equal(1, servers.Count(s => s.DisplayName == "dup-server"));
    }
}

public sealed class McpServerPublicationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task PublishMcpServer_AppearsInSpaceOverview()
    {
        var nodeId = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "filesystem", "npx", "-y server"))!;

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            await repository.UpsertMcpServerAsync(server);
        }

        // W3: /api/space now requires owner auth.
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var json = await client.GetStringAsync("/api/space");
        Assert.Contains("filesystem", json);
    }
}

public sealed class McpServerDisableTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task DisableMcpServer_MarksServerUnavailableForNewAccess()
    {
        var nodeId = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "to-disable", "echo", "hello"))!;

        using var ownerClient = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        Assert.True((await ownerClient.PostAsync($"/api/mcp-servers/{server.Id.Value}/disable", null)).IsSuccessStatusCode);

        var disabled = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        Assert.Equal(McpServerStatus.Disabled, disabled.Status);
    }
}

public sealed class McpServerDisableMetadataTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task DisableMcpServer_PersistsDisabledStatusInRepository()
    {
        var nodeId = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId)
            .PublishMcpServerAsync(nodeId, "persist-disable", "echo", "hello"))!;

        using var ownerClient = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        await ownerClient.PostAsync($"/api/mcp-servers/{server.Id.Value}/disable", null);

        var servers = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListMcpServersAsync();
        var persisted = servers.Single(s => s.Id == server.Id);
        Assert.Equal(McpServerStatus.Disabled, persisted.Status);
    }

    [Fact]
    public async Task UnpublishMcpServer_HardDeletesRow_NotJustDisabled()
    {
        var nodeId = NodeId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var server = (await space.PublishMcpServerAsync(nodeId, "persist-remove", "echo", "hello"))!;

        await space.UnpublishMcpServerAsync(nodeId, server.Id);

        // `mcp remove` is a HARD DELETE: the repository row is gone entirely — NOT left behind
        // as Disabled (contrast with DisableMcpServer_PersistsDisabledStatusInRepository above).
        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        Assert.Null(await repository.GetMcpServerAsync(server.Id));

        // And it no longer appears in the Space catalog.
        var servers = await space.ListMcpServersAsync();
        Assert.DoesNotContain(servers, s => s.Id == server.Id);
    }

    [Fact]
    public async Task UnpublishMcpServer_ByDifferentNode_IsNoOp()
    {
        var ownerNode = NodeId.New();
        var otherNode = NodeId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var server = (await space.PublishMcpServerAsync(ownerNode, "owned-by-a", "echo", "hello"))!;

        // A different node must not be able to remove a server it didn't publish.
        await space.UnpublishMcpServerAsync(otherNode, server.Id);

        using var scope = fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        Assert.NotNull(await repository.GetMcpServerAsync(server.Id));
    }
}

/// <summary>
/// 029: node-initiated inference-point retire (`korat agent remove` → daemon reconcile →
/// UnpublishInferencePoint). Mirrors the MCP UnpublishMcpServer ownership/hard-delete tests
/// above so a removed agent leaves NO orphaned cloud endpoint.
/// </summary>
public sealed class InferencePointUnpublishGrainTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // NOTE: a List<string> (not a collection-expression literal) — Orleans has no deep-copier
    // for the compiler-generated <>z__ReadOnlyArray that `[...]` targets on IReadOnlyList, and
    // the grain proxy deep-copies args. Production passes publish.Models.ToList() (a List).
    private static readonly IReadOnlyList<string> Models =
        new List<string> { "claude-opus-4-8", "claude-sonnet-4-6" };

}

/// <summary>
/// 079: declarative inference-point sync on connect — closes the offline-remove orphan edge.
/// Mirrors <see cref="InferencePointUnpublishGrainTests"/> but for the bulk SyncInferencePointsAsync path.
/// </summary>
public sealed class InferencePointSyncGrainTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // NOTE: a List<string> (not a collection-expression literal) — Orleans has no deep-copier
    // for the compiler-generated <>z__ReadOnlyArray that `[...]` targets on IReadOnlyList, and
    // the grain proxy deep-copies args. Production passes publish.Models.ToList() (a List).
    private static readonly IReadOnlyList<string> Models =
        new List<string> { "claude-opus-4-8", "claude-sonnet-4-6" };

}
