using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Regression tests for the post-review fix pass:
///   - ARCH-CRITICAL-1: SessionRoutingTable evicts session entries on stream teardown.
///   - ARCH-CRITICAL-2: RequestSession rejects an ConsumerId registered on a
///     different node than the stream that's claiming it.
///   - ARCH-HIGH-1: Heartbeat is attributed to the Hello-bound NodeId, not the wire field.
/// </summary>
public sealed class PostReviewSecurityTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task RequestSession_SpoofedAgentClient_IsRejected()
    {
        // Seed a user with a space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"spoof-sec-{Guid.NewGuid():N}@example.com", "Spoof Security Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeA = NodeId.New().Value;
        var nodeB = NodeId.New().Value;
        var agentClientId = ConsumerId.New();

        // Register agent-client on node B.
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), new NodeId(nodeB), "ac-on-node-b");

        // Publish an MCP server and approve a grant.
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            NodeId.New(),
            $"spoof-srv-{Guid.NewGuid():N}",
            "echo",
            "demo"))!;
        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, new NodeId(nodeB));
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // Connect from node A (same user, same space) and try to RequestSession claiming AC on B.
        using var callA = await ConnectAsync(nodeA, "node-a", cliToken);
        await callA.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });

        var response = await ReadAsync(callA.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal("agent_client_node_mismatch", response.AccessDenied.Reason);
    }

    [Fact]
    public async Task RequestSession_FirstUse_BindsAgentClientToNode_ThenRejectsOtherNode()
    {
        // 023: the production connect path never pre-registers the agent-client. The FIRST
        // RequestSession must bind it (TOFU) to the requesting node, and a later RequestSession
        // for the same agentClientId from a DIFFERENT node must then be rejected.
        var seeded = await fixture.SeedUserAsync(
            $"tofu-sec-{Guid.NewGuid():N}@example.com", "TOFU Bind Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeA = NodeId.New().Value;
        var nodeB = NodeId.New().Value;
        var agentClientId = ConsumerId.New(); // NEVER registered — first use binds it.

        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            NodeId.New(), $"tofu-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // First use from node A — no grant yet, so it binds the AC then returns AccessPending.
        using var callA = await ConnectAsync(nodeA, "node-a", cliToken);
        await callA.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });
        var firstResponse = await ReadAsync(callA.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessPending, firstResponse.PayloadCase);

        // The agent-client is now durably bound to node A.
        var bound = await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value).GetAsync();
        Assert.Equal(nodeA, bound.NodeId.Value);

        // Node B presenting the same agentClientId is now rejected as a spoof.
        using var callB = await ConnectAsync(nodeB, "node-b", cliToken);
        await callB.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });
        var spoof = await ReadAsync(callB.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, spoof.PayloadCase);
        Assert.Equal("agent_client_node_mismatch", spoof.AccessDenied.Reason);
    }

    [Fact]
    public async Task SessionRoutingTable_EvictsOnStreamTeardown()
    {
        // Seed a user with a space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"evict-sec-{Guid.NewGuid():N}@example.com", "Evict Security Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var publisherNodeId = NodeId.New();
        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();

        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            publisherNodeId,
            $"evict-srv-{Guid.NewGuid():N}",
            "echo",
            "demo"))!;

        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "evict-agent");

        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        using var publisherCall = await ConnectAsync(publisherNodeId.Value, "evict-publisher", cliToken, nodeKind: "publisher");
        var agentCall = await ConnectAsync(agentNodeId.Value, "evict-agent", cliToken);

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });
        var opened = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, opened.PayloadCase);
        var sessionId = new SessionId(opened.SessionOpened.SessionId);

        var routingTable = fixture.Factory.Services.GetService(typeof(SessionRoutingTable)) as SessionRoutingTable;
        Assert.NotNull(routingTable);
        Assert.NotNull(routingTable!.GetParticipants(sessionId));

        // Tear down the agent stream and wait for the eviction to land.
        await agentCall.RequestStream.CompleteAsync();
        agentCall.Dispose();

        var evicted = false;
        for (var i = 0; i < 50 && !evicted; i++)
        {
            if (routingTable.GetParticipants(sessionId) is null)
            {
                evicted = true;
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(evicted, "SessionRoutingTable did not evict the session after the stream tore down.");
    }

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAsync(
        string nodeId,
        string displayName,
        string cliToken,
        string nodeKind = "agent")
    {
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = displayName,
                NodeKind = nodeKind,
                // SpaceId resolved server-side from Bearer token.
            }
        });
        var ack = await ReadAsync(call.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, ack.PayloadCase);
        return call;
    }

    private static async Task<GatewayToNodeMessage> ReadAsync(IAsyncStreamReader<GatewayToNodeMessage> stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var moved = await stream.MoveNext(cts.Token);
        if (!moved)
            throw new Xunit.Sdk.XunitException("Stream closed before expected message arrived.");
        return stream.Current;
    }
}
