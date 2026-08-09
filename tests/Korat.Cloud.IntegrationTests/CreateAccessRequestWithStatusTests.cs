using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 031 (mobile-push increment 2), Task 5: CreateAccessRequestWithStatusAsync is the NEW method
/// that signals Created (fresh insert) vs not (idempotent replay) — the trigger
/// AccessRequestNotifier (Task 6) keys on. CreateAccessRequestAsync becomes a thin wrapper over
/// it; this file proves BOTH: the new method's Created signal, and that the wrapper's existing
/// behavior (return shape, idempotency) is unaffected (MINIMAL RIPPLE — see Global Constraints).
/// </summary>
public sealed class CreateAccessRequestWithStatusTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task CreateAccessRequestWithStatusAsync_FirstCall_ReturnsCreatedTrue()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, $"status-first-{Guid.NewGuid():N}", "echo", "one"))!;
        var agentId = ConsumerId.New();

        var result = await space.CreateAccessRequestWithStatusAsync(agentId, server.Id, nodeId);

        Assert.True(result.Created);
        Assert.Equal(AccessRequestStatus.Pending, result.Request.Status);
    }

    [Fact]
    public async Task CreateAccessRequestWithStatusAsync_DuplicateCall_ReturnsCreatedFalse_SameRequestId()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, $"status-dup-{Guid.NewGuid():N}", "echo", "one"))!;
        var agentId = ConsumerId.New();

        var first = await space.CreateAccessRequestWithStatusAsync(agentId, server.Id, nodeId);
        var second = await space.CreateAccessRequestWithStatusAsync(agentId, server.Id, nodeId);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Request.Id, second.Request.Id);
    }

    [Fact]
    public async Task CreateAccessRequestAsync_Wrapper_StillReturnsRequest_Unaffected()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(nodeId, $"status-wrapper-{Guid.NewGuid():N}", "echo", "one"))!;
        var agentId = ConsumerId.New();

        // The OLD method must still behave exactly as before: idempotent, returns AccessRequest.
        var first = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);
        var second = await space.CreateAccessRequestAsync(agentId, server.Id, nodeId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(AccessRequestStatus.Pending, first.Status);
    }
}
