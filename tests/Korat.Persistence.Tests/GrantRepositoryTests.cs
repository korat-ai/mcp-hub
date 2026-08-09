using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Persistence.Tests.Infrastructure;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Persistence.Tests;

public sealed class GrantRepositoryTests
{
    private readonly PersistenceTestFixture _fixture = new();

    [Fact]
    public async Task UpsertAndGetActiveGrant_RoundTrips()
    {
        var repository = _fixture.CreateRepository();
        var spaceId = SpaceId.New();
        var agentId = ConsumerId.New();
        var serverId = McpServerId.New();
        var grant = CreateActive(spaceId, agentId, serverId);

        await repository.UpsertGrantAsync(grant);
        var active = await repository.GetActiveGrantAsync(spaceId, agentId, serverId);

        Assert.NotNull(active);
        Assert.Equal(grant.Id, active.Id);
    }

    [Fact]
    public async Task ApproveAccessRequest_RejectsDuplicateActiveGrant()
    {
        var repository = _fixture.CreateRepository();
        var spaceId = SpaceId.New();
        var agentId = ConsumerId.New();
        var serverId = McpServerId.New();
        var request = new AccessRequest
        {
            Id = AccessRequestId.New(),
            SpaceId = spaceId,
            ConsumerId = agentId,
            McpServerId = serverId,
            RequestedByNodeId = NodeId.New(),
            PublisherNodeId = NodeId.New(),
            RequestedAt = DateTimeOffset.UtcNow
        };
        var firstGrant = CreateActive(spaceId, agentId, serverId);
        StateTransitions.ApproveAccessRequest(request, UserId.New(), DateTimeOffset.UtcNow);
        await repository.ApproveAccessRequestAsync(request, firstGrant);

        var secondRequest = new AccessRequest
        {
            Id = AccessRequestId.New(),
            SpaceId = spaceId,
            ConsumerId = agentId,
            McpServerId = serverId,
            RequestedByNodeId = NodeId.New(),
            PublisherNodeId = NodeId.New(),
            RequestedAt = DateTimeOffset.UtcNow
        };
        StateTransitions.ApproveAccessRequest(secondRequest, UserId.New(), DateTimeOffset.UtcNow);
        var secondGrant = CreateActive(spaceId, agentId, serverId, GrantId.New());

        await Assert.ThrowsAsync<KoratDomainException>(() =>
            repository.ApproveAccessRequestAsync(secondRequest, secondGrant));
    }

    private static Grant CreateActive(SpaceId spaceId, ConsumerId agentId, McpServerId serverId, GrantId? id = null) => new()
    {
        Id = id ?? GrantId.New(),
        SpaceId = spaceId,
        ConsumerId = agentId,
        McpServerId = serverId,
        ApprovedByUserId = UserId.New(),
        ApprovedAt = DateTimeOffset.UtcNow
    };
}
