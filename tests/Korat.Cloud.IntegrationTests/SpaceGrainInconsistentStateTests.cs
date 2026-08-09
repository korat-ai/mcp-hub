using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Tests the defense-in-depth inconsistent-state branch in SpaceGrain.ApproveAccessRequestAsync:
/// an already-Approved request with no corresponding active grant in the DB should throw
/// KoratDomainException(InvalidStateTransition) rather than silently succeed or return null.
/// </summary>
public sealed class SpaceGrainInconsistentStateTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ApproveAccessRequest_ApprovedRequestWithNoActiveGrant_Throws()
    {
        // Arrange: create a SpaceGrain for a fresh space and publish an MCP server.
        var spaceId = SpaceId.New();
        var nodeId = NodeId.New();
        var ownerUserId = UserId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        // Seed the space and node directly in the repository (bypass the full web host seeding).
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            var now = DateTimeOffset.UtcNow;
            // Seed a SpaceRecord so the space exists.
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            db.Spaces.Add(new SpaceRecord
            {
                Id = spaceId.Value,
                OwnerUserId = ownerUserId.Value.ToString("N"),
                DisplayName = "Inconsistent State Test Space",
                IsDefault = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();

            await repo.UpsertNodeAsync(new Node
            {
                Id = nodeId,
                SpaceId = spaceId,
                DisplayName = "test-node",
                Status = NodeStatus.Offline,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // Publish a server and create a pending access request.
        var server = (await space.PublishMcpServerAsync(nodeId, "test-server-inconsistent", "echo", "x"))!;
        var agentId = ConsumerId.New();
        var request = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);

        // Directly mutate the AccessRequest row in the DB to "Approved" status without
        // creating a Grant row — this forces the inconsistent state the branch guards against.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            // Mark the request as Approved without inserting a grant.
            var rawRequest = await repo.GetAccessRequestAsync(request.Id);
            Assert.NotNull(rawRequest);
            rawRequest!.Status = AccessRequestStatus.Approved;
            rawRequest.ResolvedAt = DateTimeOffset.UtcNow;
            rawRequest.ResolvedByUserId = ownerUserId;
            await repo.UpsertAccessRequestAsync(rawRequest);
        }

        // Force the grain to reload from the DB so it picks up the mutated state.
        await space.InvalidateCacheAsync();

        // Act + Assert: calling ApproveAccessRequestAsync on an already-Approved request with
        // no active grant must throw the defense-in-depth InvalidStateTransition exception.
        // Orleans TestCluster preserves the original KoratDomainException across the grain call
        // boundary (JSON serializer + [GenerateSerializer] keep the type intact in-process).
        var ex = await Assert.ThrowsAsync<KoratDomainException>(async () =>
            await space.ApproveAccessRequestAsync(request.Id, ownerUserId));

        Assert.Equal(KoratErrorCode.InvalidStateTransition, ex.Code);
        Assert.Contains("no active grant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
