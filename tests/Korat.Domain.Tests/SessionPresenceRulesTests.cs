using Korat.Domain.Entities;

namespace Korat.Domain.Tests;

public sealed class SessionPresenceRulesTests
{
    [Fact]
    public void RealRelayParticipants_BothOnline_ReturnsTrue()
    {
        var client = OnlineNode();
        var publisher = OnlineNode();
        var server = Server(publisher.Id);
        var session = RelaySession(client.Id, publisher.Id, server.Id);

        Assert.True(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server, client, publisher));
    }

    [Fact]
    public void RealRelayParticipant_StalePublisher_ReturnsFalse()
    {
        var client = OnlineNode();
        var publisher = OnlineNode(
            DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(1));
        var server = Server(publisher.Id);
        var session = RelaySession(client.Id, publisher.Id, server.Id);

        Assert.False(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server, client, publisher));
    }

    [Fact]
    public void HttpCloudServer_DoesNotRequirePublisherNode()
    {
        var client = OnlineNode();
        var server = Server(new NodeId(string.Empty), McpServerTransports.HttpCloud);
        var session = RelaySession(client.Id, new NodeId(string.Empty), server.Id);

        Assert.True(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server, client, publisherNode: null));
    }

    [Fact]
    public void SpaceMcpConsumer_DoesNotRequireSentinelNode()
    {
        var publisher = OnlineNode();
        var server = Server(publisher.Id);
        var session = RelaySession(
            new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            publisher.Id,
            server.Id);

        Assert.True(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server, clientNode: null, publisher));
    }

    [Fact]
    public void HttpCloudSpaceMcpSession_RequiresNoNodeRows()
    {
        var server = Server(new NodeId(string.Empty), McpServerTransports.HttpCloud);
        var session = RelaySession(
            new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId),
            new NodeId(string.Empty),
            server.Id);

        Assert.True(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server, clientNode: null, publisherNode: null));
    }

    [Fact]
    public void MissingServer_DoesNotTreatMissingPublisherAsOnline()
    {
        var client = OnlineNode();
        var session = RelaySession(client.Id, new NodeId(string.Empty), McpServerId.New());

        Assert.False(SessionPresenceRules.RequiredParticipantsAreOnline(
            session, server: null, client, publisherNode: null));
    }

    private static Node OnlineNode(DateTimeOffset? lastSeenAt = null) => new()
    {
        Id = NodeId.New(),
        SpaceId = SpaceId.New(),
        Status = NodeStatus.Online,
        LastSeenAt = lastSeenAt ?? DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static McpServer Server(NodeId publisherNodeId, string transport = "Stdio") => new()
    {
        Id = McpServerId.New(),
        SpaceId = SpaceId.New(),
        PublisherNodeId = publisherNodeId,
        Transport = transport,
        Status = McpServerStatus.Published,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static RelaySession RelaySession(NodeId clientNodeId, NodeId publisherNodeId, McpServerId serverId) => new()
    {
        Id = SessionId.New(),
        SpaceId = SpaceId.New(),
        GrantId = GrantId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = serverId,
        ClientNodeId = clientNodeId,
        PublisherNodeId = publisherNodeId,
        HomeGatewayId = GatewayId.New(),
        Status = SessionStatus.Active,
        StartedAt = DateTimeOffset.UtcNow,
    };
}
