using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// P2 defense-in-depth: SpaceGrain must refuse to mutate entities that belong to a DIFFERENT
/// space, even when called directly (bypassing the REST endpoint pre-checks).
///
/// Two gaps closed:
///   RevokeGrantAsync  — was: GetGrantAsync(grantId) PK-only; now: checks grant.SpaceId.
///   FindAccessRequestAsync — was: GetAccessRequestAsync(id) PK-only; now: checks request.SpaceId.
///
/// Strategy: seed a grant + an access-request in Space A via A's grain.  Then call the same
/// operations on Space B's grain using A's entity ids.  Both must return NotFound (same behavior
/// as a missing entity — no existence oracle).  After each call A's entity must be unchanged.
/// </summary>
public sealed class SpaceGrainCrossSpaceGuardTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── setup helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a minimal Space row in the DB (required so FK constraints on nodes/servers don't
    /// fail with the InMemory provider).  Returns a seeded (UserId, spaceId) pair.
    /// </summary>
    private async Task<(UserId OwnerUserId, SpaceId SpaceId)> SeedSpaceAsync(string tag)
    {
        var userId = UserId.New();
        var spaceId = SpaceId.New();
        var now = DateTimeOffset.UtcNow;

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        db.Spaces.Add(new SpaceRecord
        {
            Id = spaceId.Value,
            OwnerUserId = userId.Value.ToString("N"),
            DisplayName = $"P2-Guard-Test-{tag}",
            IsDefault = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (userId, spaceId);
    }

    private async Task SeedNodeInRepoAsync(NodeId nodeId, SpaceId spaceId)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        await repo.UpsertNodeAsync(new Node
        {
            Id = nodeId,
            SpaceId = spaceId,
            DisplayName = "guard-test-node",
            Status = NodeStatus.Offline,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // ── RevokeGrantAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// SpaceGrain(B).RevokeGrantAsync(grantFromA) must throw NotFound.
    /// A's grant must remain Active afterward.
    /// </summary>
    [Fact]
    public async Task RevokeGrantAsync_CrossSpace_ThrowsNotFound_GrantUnchanged()
    {
        // Arrange: Space A — seed a full grant.
        var (userIdA, spaceIdA) = await SeedSpaceAsync("revoke-a");
        var nodeIdA = NodeId.New();
        await SeedNodeInRepoAsync(nodeIdA, spaceIdA);

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdA.Value);
        var serverA = (await grainA.PublishMcpServerAsync(nodeIdA, $"guard-srv-revoke-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeIdA);
        var grantA = await grainA.ApproveAccessRequestAsync(requestA.Id, userIdA);
        Assert.Equal(GrantStatus.Active, grantA.Status);

        // Arrange: Space B — unrelated space, owns nothing.
        var (userIdB, spaceIdB) = await SeedSpaceAsync("revoke-b");
        var grainB = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdB.Value);

        // Act: Space B's grain is asked to revoke A's grant by id.
        // The in-memory list of grainB has no grants; the repository.GetGrantAsync fallback
        // resolves A's grant by PK.  The P2 guard must reject it (grant.SpaceId == A != B).
        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await grainB.RevokeGrantAsync(grantA.Id, userIdB));

        Assert.Equal(KoratErrorCode.NotFound, ex.Code);

        // Assert: A's grant is still Active — the guard prevented mutation.
        var grantsA = await grainA.ListGrantsAsync();
        Assert.Equal(GrantStatus.Active, grantsA.Single(g => g.Id == grantA.Id).Status);
    }

    // ── FindAccessRequestAsync via ApproveAccessRequestAsync ─────────────────

    /// <summary>
    /// SpaceGrain(B).ApproveAccessRequestAsync(requestFromA) must throw NotFound.
    /// A's request must remain Pending afterward.
    /// </summary>
    [Fact]
    public async Task ApproveAccessRequestAsync_CrossSpace_ThrowsNotFound_RequestUnchanged()
    {
        // Arrange: Space A — seed a pending access request.
        var (userIdA, spaceIdA) = await SeedSpaceAsync("approve-a");
        var nodeIdA = NodeId.New();
        await SeedNodeInRepoAsync(nodeIdA, spaceIdA);

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdA.Value);
        var serverA = (await grainA.PublishMcpServerAsync(nodeIdA, $"guard-srv-approve-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeIdA);
        Assert.Equal(AccessRequestStatus.Pending, requestA.Status);

        // Arrange: Space B.
        var (userIdB, spaceIdB) = await SeedSpaceAsync("approve-b");
        var grainB = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdB.Value);

        // Act: Space B's grain tries to approve A's request.
        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await grainB.ApproveAccessRequestAsync(requestA.Id, userIdB));

        Assert.Equal(KoratErrorCode.NotFound, ex.Code);

        // Assert: A's request is still Pending, no grant was created.
        var requestsA = await grainA.ListAccessRequestsAsync();
        Assert.Equal(AccessRequestStatus.Pending, requestsA.Single(r => r.Id == requestA.Id).Status);

        var grantsA = await grainA.ListGrantsAsync();
        Assert.Empty(grantsA);
    }

    // ── FindAccessRequestAsync via DenyAccessRequestAsync ────────────────────

    /// <summary>
    /// SpaceGrain(B).DenyAccessRequestAsync(requestFromA) must throw NotFound.
    /// A's request must remain Pending afterward.
    /// </summary>
    [Fact]
    public async Task DenyAccessRequestAsync_CrossSpace_ThrowsNotFound_RequestUnchanged()
    {
        // Arrange: Space A — seed a pending access request.
        var (userIdA, spaceIdA) = await SeedSpaceAsync("deny-a");
        var nodeIdA = NodeId.New();
        await SeedNodeInRepoAsync(nodeIdA, spaceIdA);

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdA.Value);
        var serverA = (await grainA.PublishMcpServerAsync(nodeIdA, $"guard-srv-deny-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeIdA);
        Assert.Equal(AccessRequestStatus.Pending, requestA.Status);

        // Arrange: Space B.
        var (userIdB, spaceIdB) = await SeedSpaceAsync("deny-b");
        var grainB = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceIdB.Value);

        // Act: Space B's grain tries to deny A's request.
        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await grainB.DenyAccessRequestAsync(requestA.Id, userIdB));

        Assert.Equal(KoratErrorCode.NotFound, ex.Code);

        // Assert: A's request is still Pending.
        var requestsA = await grainA.ListAccessRequestsAsync();
        Assert.Equal(AccessRequestStatus.Pending, requestsA.Single(r => r.Id == requestA.Id).Status);
    }

    // ── same-space operations still work ─────────────────────────────────────

    /// <summary>
    /// Regression guard: the P2 SpaceId check must not break legit same-space revoke.
    /// </summary>
    [Fact]
    public async Task RevokeGrantAsync_SameSpace_Succeeds()
    {
        var (userId, spaceId) = await SeedSpaceAsync("revoke-same");
        var nodeId = NodeId.New();
        await SeedNodeInRepoAsync(nodeId, spaceId);

        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var server = (await grain.PublishMcpServerAsync(nodeId, $"guard-srv-same-revoke-{Guid.NewGuid():N}", "echo", "x"))!;
        var request = await grain.CreateAccessRequestAsync(ConsumerId.New(), server.Id, nodeId);
        var grant = await grain.ApproveAccessRequestAsync(request.Id, userId);
        Assert.Equal(GrantStatus.Active, grant.Status);

        // Same-space revoke must succeed.
        await grain.RevokeGrantAsync(grant.Id, userId);

        var grants = await grain.ListGrantsAsync();
        Assert.Equal(GrantStatus.Revoked, grants.Single(g => g.Id == grant.Id).Status);
    }

    /// <summary>
    /// Regression guard: the P2 SpaceId check must not break legit same-space approve.
    /// </summary>
    [Fact]
    public async Task ApproveAccessRequestAsync_SameSpace_Succeeds()
    {
        var (userId, spaceId) = await SeedSpaceAsync("approve-same");
        var nodeId = NodeId.New();
        await SeedNodeInRepoAsync(nodeId, spaceId);

        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var server = (await grain.PublishMcpServerAsync(nodeId, $"guard-srv-same-approve-{Guid.NewGuid():N}", "echo", "x"))!;
        var request = await grain.CreateAccessRequestAsync(ConsumerId.New(), server.Id, nodeId);

        var grant = await grain.ApproveAccessRequestAsync(request.Id, userId);

        Assert.Equal(GrantStatus.Active, grant.Status);
        Assert.Equal(server.Id, grant.McpServerId);
    }
}
