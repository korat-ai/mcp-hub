using Korat.Domain.Entities;

namespace Korat.Domain.Tests;

public class NodePresenceRulesTests
{
    private static Node OnlineNode(DateTimeOffset? lastSeen) => new()
    {
        Id = NodeId.New(),
        SpaceId = SpaceId.New(),
        Status = NodeStatus.Online,
        LastSeenAt = lastSeen,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Node OfflineNode() => new()
    {
        Id = NodeId.New(),
        SpaceId = SpaceId.New(),
        Status = NodeStatus.Offline,
        LastSeenAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void EffectiveStatus_OnlineWithLastSeenJustInsideThreshold_ReturnsOnline()
    {
        // LastSeen is 1 second inside the 90s stale threshold.
        var lastSeen = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold + TimeSpan.FromSeconds(1);
        var node = OnlineNode(lastSeen);
        Assert.Equal(NodeStatus.Online, NodePresenceRules.EffectiveStatus(node));
    }

    [Fact]
    public void EffectiveStatus_OnlineWithLastSeenJustPastThreshold_ReturnsOffline()
    {
        // LastSeen is 1 second past the 90s stale threshold.
        var lastSeen = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(1);
        var node = OnlineNode(lastSeen);
        Assert.Equal(NodeStatus.Offline, NodePresenceRules.EffectiveStatus(node));
    }

    [Fact]
    public void EffectiveStatus_OfflineNode_PassesThroughAsOffline()
    {
        var node = OfflineNode();
        Assert.Equal(NodeStatus.Offline, NodePresenceRules.EffectiveStatus(node));
    }

    [Fact]
    public void EffectiveStatus_OnlineWithNullLastSeen_ReturnsOffline()
    {
        // A real ConnectAsync stamps LastSeenAt. A legacy/synthetic Online row without any
        // successful hello or heartbeat is not evidence of a live transport.
        var node = OnlineNode(lastSeen: null);
        Assert.Equal(NodeStatus.Offline, NodePresenceRules.EffectiveStatus(node));
    }

    // ---------------------------------------------------------------------------
    // MAJOR-3: SessionGrain liveness-gate logic (domain rule verification)
    // The IsSessionAbandonedAsync private method in SessionGrain applies exactly
    // the rule below — both nodes must be Offline to warrant a force-close.
    // These tests document and lock in the decision rules.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Helper that mirrors SessionGrain.IsSessionAbandonedAsync logic without requiring Orleans.
    /// Both nodes must report Offline to be considered abandoned.
    /// </summary>
    private static bool IsAbandoned(Node clientNode, Node publisherNode)
        => NodePresenceRules.EffectiveStatus(clientNode) == NodeStatus.Offline
        && NodePresenceRules.EffectiveStatus(publisherNode) == NodeStatus.Offline;

    [Fact]
    public void Liveness_BothNodesStale_IsAbandoned()
    {
        var staleTime = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(5);
        var client = OnlineNode(staleTime);
        var publisher = OnlineNode(staleTime);
        Assert.True(IsAbandoned(client, publisher),
            "Session with both nodes stale beyond threshold should be considered abandoned.");
    }

    [Fact]
    public void Liveness_ClientNodeFresh_NotAbandoned()
    {
        var staleTime = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(5);
        var freshTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        var client = OnlineNode(freshTime);    // fresh
        var publisher = OnlineNode(staleTime); // stale
        Assert.False(IsAbandoned(client, publisher),
            "Session with a live client node must NOT be force-closed.");
    }

    [Fact]
    public void Liveness_PublisherNodeFresh_NotAbandoned()
    {
        var staleTime = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(5);
        var freshTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        var client = OnlineNode(staleTime);    // stale
        var publisher = OnlineNode(freshTime); // fresh
        Assert.False(IsAbandoned(client, publisher),
            "Session with a live publisher node must NOT be force-closed.");
    }

    [Fact]
    public void Liveness_BothNodesFresh_NotAbandoned()
    {
        var freshTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        var client = OnlineNode(freshTime);
        var publisher = OnlineNode(freshTime);
        Assert.False(IsAbandoned(client, publisher),
            "Session with both nodes live must NOT be force-closed (cross-silo activation case).");
    }

    [Fact]
    public void Liveness_OfflineNode_StillStale()
    {
        // A node explicitly marked Offline (e.g. graceful disconnect) is considered stale.
        var client = OfflineNode();
        var publisher = OfflineNode();
        Assert.True(IsAbandoned(client, publisher),
            "Explicitly offline nodes indicate a genuinely terminated session.");
    }
}
