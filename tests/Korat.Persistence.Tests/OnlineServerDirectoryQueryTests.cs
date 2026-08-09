using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Entities;
using Korat.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Korat.Persistence.Tests;

/// <summary>
/// Unit tests for <see cref="EfMetadataRepository.ListUserIdsWithOnlineServerAsync"/> and
/// <see cref="EfMetadataRepository.HasOnlineServerAsync"/>. One [Fact] per case.
/// All tests share a single fixture but use distinct ids to avoid cross-test pollution.
/// </summary>
public sealed class OnlineServerDirectoryQueryTests
{
    private readonly PersistenceTestFixture _fixture = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Seeds a SpaceRecord with <paramref name="ownerId"/> as the owner.</summary>
    private async Task<string> SeedSpaceAsync(UserId ownerId)
    {
        await using var db = _fixture.CreateFactory().CreateDbContext();
        var spaceId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        db.Spaces.Add(new SpaceRecord
        {
            Id = spaceId,
            OwnerUserId = ownerId.Value.ToString("N"),
            DisplayName = "test-space",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return spaceId;
    }

    private static Node MakeOnlineNode(string spaceId, DateTimeOffset lastSeenAt) => new()
    {
        Id = NodeId.New(),
        SpaceId = new SpaceId(spaceId),
        DisplayName = "node",
        Status = NodeStatus.Online,
        LastSeenAt = lastSeenAt,
        CreatedAt = lastSeenAt,
        UpdatedAt = lastSeenAt,
    };

    private static Node MakeOfflineNode(string spaceId) => new()
    {
        Id = NodeId.New(),
        SpaceId = new SpaceId(spaceId),
        DisplayName = "offline-node",
        Status = NodeStatus.Offline,
        LastSeenAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static McpServer MakePublishedServer(string spaceId, NodeId publisherNodeId, bool isAsserted = true) => new()
    {
        Id = McpServerId.New(),
        SpaceId = new SpaceId(spaceId),
        PublisherNodeId = publisherNodeId,
        DisplayName = "test-mcp",
        Transport = "Stdio",
        LaunchCommand = "echo",
        LaunchArguments = "",
        Status = McpServerStatus.Published,
        IsAsserted = isAsserted,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Online_user_is_included_in_list_and_HasOnlineServer_is_true()
    {
        var repo = _fixture.CreateRepository();
        var u = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddSeconds(-90);

        var spaceId = await SeedSpaceAsync(u);
        var node = MakeOnlineNode(spaceId, now);
        await repo.UpsertNodeAsync(node);
        var server = MakePublishedServer(spaceId, node.Id);
        await repo.UpsertMcpServerAsync(server);

        var users = await repo.ListUserIdsWithOnlineServerAsync(staleCutoff);
        Assert.Contains(u, users);
        Assert.True(await repo.HasOnlineServerAsync(u, staleCutoff));
    }

    [Fact]
    public async Task Stale_node_is_excluded()
    {
        var repo = _fixture.CreateRepository();
        var u = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddSeconds(-90);
        var staleTime = now.AddSeconds(-120); // older than cutoff

        var spaceId = await SeedSpaceAsync(u);
        // Node is Online but LastSeenAt is before the cutoff.
        var node = MakeOnlineNode(spaceId, staleTime);
        await repo.UpsertNodeAsync(node);
        var server = MakePublishedServer(spaceId, node.Id);
        await repo.UpsertMcpServerAsync(server);

        var users = await repo.ListUserIdsWithOnlineServerAsync(staleCutoff);
        Assert.DoesNotContain(u, users);
        Assert.False(await repo.HasOnlineServerAsync(u, staleCutoff));
    }

    [Fact]
    public async Task Offline_node_is_excluded()
    {
        var repo = _fixture.CreateRepository();
        var u = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddSeconds(-90);

        var spaceId = await SeedSpaceAsync(u);
        var node = MakeOfflineNode(spaceId);
        await repo.UpsertNodeAsync(node);
        var server = MakePublishedServer(spaceId, node.Id);
        await repo.UpsertMcpServerAsync(server);

        var users = await repo.ListUserIdsWithOnlineServerAsync(staleCutoff);
        Assert.DoesNotContain(u, users);
        Assert.False(await repo.HasOnlineServerAsync(u, staleCutoff));
    }

    [Fact]
    public async Task Not_asserted_server_is_excluded()
    {
        var repo = _fixture.CreateRepository();
        var u = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddSeconds(-90);

        var spaceId = await SeedSpaceAsync(u);
        var node = MakeOnlineNode(spaceId, now);
        await repo.UpsertNodeAsync(node);
        // Server is not asserted (IsAsserted = false).
        var server = MakePublishedServer(spaceId, node.Id, isAsserted: false);
        await repo.UpsertMcpServerAsync(server);

        var users = await repo.ListUserIdsWithOnlineServerAsync(staleCutoff);
        Assert.DoesNotContain(u, users);
        Assert.False(await repo.HasOnlineServerAsync(u, staleCutoff));
    }

    [Fact]
    public async Task User_with_no_server_is_excluded()
    {
        var repo = _fixture.CreateRepository();
        var u = UserId.New();
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now.AddSeconds(-90);

        // Seed the space but no node or server.
        await SeedSpaceAsync(u);

        var users = await repo.ListUserIdsWithOnlineServerAsync(staleCutoff);
        Assert.DoesNotContain(u, users);
        Assert.False(await repo.HasOnlineServerAsync(u, staleCutoff));
    }
}
