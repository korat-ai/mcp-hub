using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Persistence.Tests.Infrastructure;

namespace Korat.Persistence.Tests;

public sealed class SessionRepositoryTests
{
    private readonly PersistenceTestFixture _fixture = new();

    [Fact]
    public async Task UpsertAndGetSession_RoundTripsMetadataOnly()
    {
        var repository = _fixture.CreateRepository();
        var session = CreateSession();

        await repository.UpsertSessionAsync(session);
        var loaded = await repository.GetSessionAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(SessionStatus.Active, loaded.Status);
        Assert.Equal(42, loaded.BytesClientToServer);
        Assert.Equal(24, loaded.BytesServerToClient);
    }

    [Fact]
    public async Task ListSessions_ReturnsPersistedRowsForSpace()
    {
        var repository = _fixture.CreateRepository();
        var spaceId = SpaceId.New();
        var session = CreateSession(spaceId);
        await repository.UpsertSessionAsync(session);

        var listed = await repository.ListSessionsAsync(spaceId);
        Assert.Single(listed);
    }

    private static RelaySession CreateSession(SpaceId? spaceId = null) => new()
    {
        Id = SessionId.New(),
        SpaceId = spaceId ?? SpaceId.New(),
        GrantId = GrantId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = McpServerId.New(),
        ClientNodeId = NodeId.New(),
        PublisherNodeId = NodeId.New(),
        HomeGatewayId = GatewayId.New(),
        Status = SessionStatus.Active,
        StartedAt = DateTimeOffset.UtcNow,
        BytesClientToServer = 42,
        BytesServerToClient = 24
    };
}
