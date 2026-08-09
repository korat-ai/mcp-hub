using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// #165 (`korat nodes prune`): grain-level semantics for
/// <see cref="ISpaceGrain.PruneAgentNodesAsync"/> — which nodes qualify for GC (Agent-kind,
/// stale by LastSeenAt or, if never seen, by CreatedAt), that Publisher nodes are NEVER pruned
/// (v1 scope — publishers are precious), and that Active grants reachable from a pruned node's
/// AccessRequests are revoked with their live sessions surfaced for teardown (mirrors
/// DeleteMcpServerAsync's grant sweep — see SessionTeardownOnRevokeTests for that sibling
/// coverage).
/// </summary>
public sealed class SpaceGrainPruneTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static async Task<NodeId> RegisterNodeAsync(
        KoratIntegrationFixture fixture, string spaceId, string displayName, NodeKind kind,
        DateTimeOffset? lastSeenAt, DateTimeOffset createdAt)
    {
        var nodeId = NodeId.New();
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId);
        await grain.RegisterNodeAsync(new Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(spaceId),
            DisplayName = displayName,
            Status = NodeStatus.Offline,
            Kind = kind,
            LastSeenAt = lastSeenAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        return nodeId;
    }

    [Fact]
    public async Task Fresh_agent_node_survives_prune()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var freshId = await RegisterNodeAsync(fixture, spaceId.Value, "fresh-agent", NodeKind.Agent,
            lastSeenAt: now.AddMinutes(-1), createdAt: now.AddDays(-60));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Empty(result.PrunedNames);
        var nodes = await space.ListNodesAsync();
        Assert.Contains(nodes, n => n.Id == freshId);
    }

    [Fact]
    public async Task Stale_agent_node_is_removed_from_space_and_its_grain_is_reset()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var staleId = await RegisterNodeAsync(fixture, spaceId.Value, "stale-agent", NodeKind.Agent,
            lastSeenAt: now.AddDays(-45), createdAt: now.AddDays(-90));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Contains("stale-agent", result.PrunedNames);
        var nodes = await space.ListNodesAsync();
        Assert.DoesNotContain(nodes, n => n.Id == staleId);

        // NodeGrain.RemoveAsync reset + deactivated the grain — a fresh activation finds no row.
        var grainState = await fixture.ClusterClient.GetGrain<INodeGrain>(staleId.Value).GetAsync();
        Assert.Equal(default(NodeId), grainState.Id);
    }

    [Fact]
    public async Task Publisher_node_is_never_pruned_even_when_extremely_stale()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var publisherId = await RegisterNodeAsync(fixture, spaceId.Value, "stale-publisher", NodeKind.Publisher,
            lastSeenAt: now.AddDays(-365), createdAt: now.AddDays(-400));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Empty(result.PrunedNames);
        var nodes = await space.ListNodesAsync();
        Assert.Contains(nodes, n => n.Id == publisherId);
    }

    [Fact]
    public async Task Never_seen_node_falls_back_to_CreatedAt_for_the_cutoff_check()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        // Never connected (LastSeenAt null) but registered long ago — eligible via CreatedAt.
        await RegisterNodeAsync(fixture, spaceId.Value, "never-seen-old", NodeKind.Agent,
            lastSeenAt: null, createdAt: now.AddDays(-90));
        // Never connected, registered recently — NOT eligible.
        await RegisterNodeAsync(fixture, spaceId.Value, "never-seen-fresh", NodeKind.Agent,
            lastSeenAt: null, createdAt: now.AddDays(-1));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Contains("never-seen-old", result.PrunedNames);
        Assert.DoesNotContain("never-seen-fresh", result.PrunedNames);
    }

    [Fact]
    public async Task Prune_revokes_active_grants_reachable_from_the_pruned_node_and_returns_affected_sessions()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var publisherNodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNodeId, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;

        var agentNodeId = await RegisterNodeAsync(fixture, spaceId.Value, "stale-agent-with-grant", NodeKind.Agent,
            lastSeenAt: now.AddDays(-45), createdAt: now.AddDays(-90));
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNodeId, "stale-agent-with-grant");

        // A Grant only ever exists via an approved AccessRequest, and the AccessRequest records
        // RequestedByNodeId — exactly the linkage PruneAgentNodesAsync walks to find grants
        // reachable from the node being pruned (see the method's doc comment).
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        var sessionId = SessionId.New();
        await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).OpenAsync(
            grant.Id, agentClientId, server.Id, agentNodeId, publisherNodeId,
            new GatewayId("gw"), spaceId, new ConnectionId("conn-prune-1"));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Contains("stale-agent-with-grant", result.PrunedNames);
        Assert.Contains(sessionId, result.AffectedSessionIds);

        var grants = await space.ListGrantsAsync();
        Assert.Equal(GrantStatus.Revoked, grants.Single(g => g.Id == grant.Id).Status);
    }

    [Fact]
    public async Task Prune_with_no_candidates_returns_empty_result_and_touches_nothing()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var freshId = await RegisterNodeAsync(fixture, spaceId.Value, "fresh-agent", NodeKind.Agent,
            lastSeenAt: now, createdAt: now.AddDays(-1));

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Empty(result.PrunedNames);
        Assert.Empty(result.AffectedSessionIds);
        var nodes = await space.ListNodesAsync();
        Assert.Contains(nodes, n => n.Id == freshId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #167 review (fix 2): pending AccessRequests filed from a pruned node must be denied, not
    // left dangling — otherwise the owner's approvals UI shows a pending approval attributed to a
    // now-deleted node with no way to resolve it.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prune_denies_pending_access_request_from_a_pruned_node()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var publisherNodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNodeId, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;

        var agentNodeId = await RegisterNodeAsync(fixture, spaceId.Value, "stale-agent-with-pending", NodeKind.Agent,
            lastSeenAt: now.AddDays(-45), createdAt: now.AddDays(-90));
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNodeId, "stale-agent-with-pending");

        // Deliberately left Pending (never approved/denied) — this is what PruneAgentNodesAsync
        // must resolve for a node it is about to delete.
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Contains("stale-agent-with-pending", result.PrunedNames);

        var requests = await space.ListAccessRequestsAsync();
        Assert.Equal(AccessRequestStatus.Denied, requests.Single(r => r.Id == ar.Id).Status);
    }

    [Fact]
    public async Task Prune_leaves_pending_access_request_from_a_surviving_node_untouched()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var now = DateTimeOffset.UtcNow;

        var publisherNodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNodeId, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;

        // Fresh agent node — NOT eligible for pruning.
        var agentNodeId = await RegisterNodeAsync(fixture, spaceId.Value, "fresh-agent-with-pending", NodeKind.Agent,
            lastSeenAt: now.AddMinutes(-1), createdAt: now.AddDays(-60));
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNodeId, "fresh-agent-with-pending");

        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);

        var result = await space.PruneAgentNodesAsync(
            KoratIntegrationFixture.DevSpaceOwnerUserId, now.AddDays(-30));

        Assert.Empty(result.PrunedNames);

        var requests = await space.ListAccessRequestsAsync();
        Assert.Equal(AccessRequestStatus.Pending, requests.Single(r => r.Id == ar.Id).Status);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #167 review (fix 3): the grain rejects a cutoff newer than "1 day ago" — defense in depth
    // for a future internal/programmatic caller that bypasses the HTTP endpoint's own >= 1 day
    // validation.
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneAgentNodesAsync_rejects_a_cutoff_newer_than_one_day_ago()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        var ex = await Assert.ThrowsAsync<KoratDomainException>(() =>
            space.PruneAgentNodesAsync(
                KoratIntegrationFixture.DevSpaceOwnerUserId, DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal(KoratErrorCode.Validation, ex.Code);
    }
}
