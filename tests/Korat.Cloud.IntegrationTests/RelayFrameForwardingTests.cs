using Google.Protobuf;
using Grpc.Core;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 005-mvp-relay-minimal: end-to-end proof that a RelayFrame sent by one node is forwarded
/// through the cloud gateway to the opposite end of the session and vice versa.
///
/// This is the MVP demo in test form. It deliberately does NOT exercise:
///   - E2E encryption (cleartext bytes for MVP)
///   - Payload size limits (constitution IX, deferred)
///   - Revoke-during-session behavior
///   - Multi-session or cross-silo routing
/// </summary>
public sealed class RelayFrameForwardingTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FrameRoundTrip_AgentToPublisher_AndBack()
    {
        // Seed a user with a space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"relay-test-{Guid.NewGuid():N}@example.com", "Relay Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        // ── Arrange: publish an MCP server, register an agent client, approve a grant ──────
        var publisherNodeId = NodeId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            publisherNodeId,
            $"relay-srv-{Guid.NewGuid():N}",
            "echo",
            "demo"))!;

        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();

        // ARCH-CRITICAL-2: gateway validates agent-client.NodeId matches the stream's
        // Hello NodeId — register the agent-client grain explicitly.
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "test-agent");

        // CreateAccessRequest + Approve in-process — bypasses the HTTP approval endpoint.
        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // ── Open both gRPC streams concurrently ───────────────────────────────────────────
        using var publisherCall = await ConnectAsync(publisherNodeId.Value, "publisher-node", cliToken);
        using var agentCall = await ConnectAsync(agentNodeId.Value, "agent-node", cliToken, nodeKind: "agent");

        // Agent requests a session against the (now-approved) MCP server.
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });

        var sessionResponse = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, sessionResponse.PayloadCase);
        var sessionId = sessionResponse.SessionOpened.SessionId;
        Assert.False(string.IsNullOrEmpty(sessionId));

        // ── Agent → Publisher ─────────────────────────────────────────────────────────────
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 1,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8("hello")
            }
        });

        var pubReceived = await ReadAsync(publisherCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, pubReceived.PayloadCase);
        Assert.Equal(sessionId, pubReceived.Frame.SessionId);
        Assert.Equal(1ul, pubReceived.Frame.SequenceNumber);
        Assert.Equal("hello", pubReceived.Frame.Ciphertext.ToStringUtf8());

        // ── Publisher → Agent ─────────────────────────────────────────────────────────────
        await publisherCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 2,
                Direction = "server_to_client",
                Ciphertext = ByteString.CopyFromUtf8("world")
            }
        });

        var agentReceived = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, agentReceived.PayloadCase);
        Assert.Equal(sessionId, agentReceived.Frame.SessionId);
        Assert.Equal(2ul, agentReceived.Frame.SequenceNumber);
        Assert.Equal("world", agentReceived.Frame.Ciphertext.ToStringUtf8());

        // ── Clean shutdown ───────────────────────────────────────────────────────────────
        await agentCall.RequestStream.CompleteAsync();
        await publisherCall.RequestStream.CompleteAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────
    // #167 review (fix 5): PublishInferencePoint (and its Unpublish/Sync siblings) are
    // publisher-only messages — an agent-kind stream (a one-shot `korat connect --agent`
    // identity, which `korat nodes prune` deletes) must not be able to register a durable
    // inference point that would be orphaned once its node is pruned. No existing test exercised
    // this at the gRPC level (all prior coverage called ISpaceGrain.PublishInferencePointAsync
    // directly, or used a CLI-side mocked send override) — these two are new coverage for the
    // NodeGatewayService.Connect switch's role guard added alongside this fix.
    // ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAsync(
        string nodeId,
        string displayName,
        string cliToken,
        string nodeKind = "")
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
                // 022: agent connections MUST send node_kind="agent" so the gateway registers them
                // by ConnectionId (publisher→agent frames route by connection). Real agent bridges
                // always send this (ConnectCommand). Empty ⇒ publisher (node-keyed), as before.
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
        using var cts = new CancellationTokenSource(MoveNextTimeout);
        var moved = await stream.MoveNext(cts.Token);
        if (!moved)
            throw new Xunit.Sdk.XunitException("Stream closed before expected message arrived.");
        return stream.Current;
    }
}
