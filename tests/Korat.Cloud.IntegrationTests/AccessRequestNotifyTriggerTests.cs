using Grpc.Core;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 031 (mobile-push increment 2), Task 7: regression + wiring coverage for the
/// NodeGatewayService.HandleRequestSessionAsync switch from CreateAccessRequestAsync to
/// CreateAccessRequestWithStatusAsync + the detached notify trigger. The Testing environment has
/// neither Korat:Apns:KeyId nor Korat:Fcm:ProjectId configured, so the real DI-wired
/// AccessRequestNotifier → RoutingAlertPushSender pipeline runs end-to-end against TWO
/// NullAlertPushSender legs — this proves the wiring is correct (no crash, no hang) without
/// needing a fake IAlertPushSender override. DetachedNotifyRunnerTests (pure unit, no Orleans)
/// separately proves the exception/timeout-swallowing contract.
/// </summary>
public sealed class AccessRequestNotifyTriggerTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task RequestSession_FirstCall_WithPushEnabledOwnerNode_StillReturnsAccessPending()
    {
        var (spaceId, cliToken) = await SeedUserSpaceAndTokenAsync();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"notify-trigger-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Seed an owner-side push-enabled node in the SAME space so AccessRequestNotifier's
        // ListNodesAsync/fan-out has a real, non-empty target — exercises the full detached path
        // (both legs land on NullAlertPushSender in Testing, so this must complete without error).
        //
        // Post-review correction (Fable holistic plan review): seeding via repo.UpsertNodeAsync
        // AFTER the SpaceGrain has hydrated does NOT enter the grain's in-memory _nodes
        // membership (hydrate-once + RegisterNodeAsync), so ISpaceGrain.ListNodesAsync() would
        // return it empty and the fan-out would not actually be exercised. Register through the
        // grain path instead: ISpaceGrain.RegisterNodeAsync adds the node to _nodes membership
        // AND persists it, then INodeGrain.RegisterPushTokenAsync sets the push token on the
        // canonical per-node state that ListNodesAsync fans out to live via INodeGrain.GetAsync().
        var ownerNodeId = NodeId.New();
        await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId).RegisterNodeAsync(new Node
        {
            Id = ownerNodeId,
            SpaceId = new SpaceId(spaceId),
            DisplayName = "owner-iphone",
            Status = NodeStatus.Offline,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.ClusterClient.GetGrain<INodeGrain>(ownerNodeId.Value)
            .RegisterPushTokenAsync("aabbccdd00000000", "apns");

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        using var call = await ConnectAgentAsync(agentNodeId, cliToken);
        var response = await RequestSessionAsync(call, agentNodeId, server.Id.Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessPending, response.PayloadCase);
        Assert.False(string.IsNullOrWhiteSpace(response.AccessPending.AccessRequestId));
    }

    [Fact]
    public async Task RequestSession_DuplicateCall_StillReturnsSameAccessRequestId_NoSecondNotifyThrow()
    {
        var (spaceId, cliToken) = await SeedUserSpaceAndTokenAsync();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"notify-trigger-dup-{Guid.NewGuid():N}", "echo", "demo"))!;

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);

        string firstId;
        using (var call = await ConnectAgentAsync(agentNodeId, cliToken))
        {
            var first = await RequestSessionAsync(call, agentNodeId, server.Id.Value);
            firstId = first.AccessPending.AccessRequestId;
        }

        using var secondCall = await ConnectAgentAsync(agentNodeId, cliToken);
        var second = await RequestSessionAsync(secondCall, agentNodeId, server.Id.Value);

        // Created=false path (idempotent replay) — CreateAccessRequestWithStatusAsync must
        // return the SAME request, and the switch must not throw/hang the second RequestSession.
        Assert.Equal(firstId, second.AccessPending.AccessRequestId);
    }

    // ─── helpers (mirrors ConnectAccessRequestTests) ────────────────────────

    private async Task<(string SpaceId, string CliToken)> SeedUserSpaceAndTokenAsync()
    {
        var seeded = await fixture.SeedUserAsync($"notify-trigger-{Guid.NewGuid():N}@example.com", "Notify Trigger Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        return (seeded.SpaceId, cliToken);
    }

    private Task RegisterAgentClientAsync(string agentClientId, string nodeId, string spaceId) =>
        fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId)
            .RegisterAsync(new SpaceId(spaceId), new NodeId(nodeId), "test-agent");

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAgentAsync(
        string nodeId, string cliToken)
    {
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "agent",
                NodeKind = "agent",
            }
        });
        await call.ResponseStream.MoveNext(CancellationToken.None);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);
        return call;
    }

    private static async Task<GatewayToNodeMessage> RequestSessionAsync(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call,
        string agentNodeId,
        string serverId)
    {
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentNodeId,
                McpServerId = serverId
            }
        });
        await call.ResponseStream.MoveNext(CancellationToken.None);
        return call.ResponseStream.Current;
    }
}
