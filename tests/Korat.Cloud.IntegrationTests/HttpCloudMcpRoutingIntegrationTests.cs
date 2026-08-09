using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Task 5 (HTTP MCP direct-to-Space, Increment 1): the first test to exercise
/// <c>HttpMcpProxyGrain</c> THROUGH the real gateway path (RequestSession over the gRPC
/// NodeGatewayService, then a client_to_server RelayFrame, then the grain's asynchronously
/// pushed response) instead of calling the grain directly (that's <c>HttpMcpProxyGrainTests</c>).
///
/// Unlike <see cref="RelayFrameForwardingTests"/>, there is only ONE gRPC stream here — the
/// agent/consumer side. An http_cloud session has no publisher node/stream at all;
/// <c>HttpMcpProxyGrain</c> stands in for it and pushes the response back onto the SAME agent
/// stream (Crux Findings 2/3/13).
/// </summary>
public sealed class HttpCloudMcpRoutingIntegrationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConsumerSession_ToHttpCloudServer_ToolsCallRoundTrips_NoPublisherStreamInvolved()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var stub = builder.Build();
        stub.MapPost("/", async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            var responseJson = method switch
            {
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"routed-ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"error":{"code":-32601,"message":"unknown"}}"""
            };
            await ctx.Response.WriteAsync(responseJson);
        });
        await stub.StartAsync();
        await using var stubDisposable = stub;
        var stubUrl = stub.Urls.First();

        var seeded = await fixture.SeedUserAsync($"http-routing-{Guid.NewGuid():N}@example.com", "HTTP Routing Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-routing-srv-{Guid.NewGuid():N}", stubUrl, McpServerAuthModes.None, null, null);

        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "test-agent");

        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        using var agentCall = await ConnectAsync(agentNodeId.Value, "agent-node", cliToken, nodeKind: "agent");

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
        // Crux Finding 9: E2E cannot apply to a cloud-terminated http_cloud session.
        Assert.False(sessionResponse.SessionOpened.PeerSupportsE2E);
        var sessionId = sessionResponse.SessionOpened.SessionId;

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 1,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8(
                    """{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"echo","arguments":{}}}""")
            }
        });

        // The response arrives on the SAME agent stream — there is no second (publisher) stream
        // for an http_cloud session; HttpMcpProxyGrain stands in for it.
        var received = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, received.PayloadCase);
        Assert.Equal(sessionId, received.Frame.SessionId);

        var responseJson = JsonSerializer.Deserialize<JsonElement>(received.Frame.Ciphertext.ToByteArray());
        Assert.Equal(42, responseJson.GetProperty("id").GetInt32());
        Assert.Equal("routed-ok", responseJson.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// Finding 16, M1: the agent-bridge-disconnect teardown loop (`NodeGatewayService.cs:434-449`)
    /// is the MOST COMMON session-close path (a bridge process dying, not an explicit
    /// revoke/CloseSession). Before Task 5's fix it called `routingTable.CloseSession(sessionId)`
    /// directly with no route lookup first, so an http_cloud session's `IsHttpCloud` flag was
    /// already gone by the time any branch could check it — permanently leaking the grain's
    /// `ConsumerUpstream` (its `HttpClient` + FIFO worker task) on every ordinary bridge disconnect.
    ///
    /// This test proves the ROUTE side of the fix landed: after a live http_cloud session has
    /// exchanged at least one frame (so the grain has actually activated a `ConsumerUpstream` for
    /// it), simply tearing down the agent's gRPC stream WITHOUT sending `CloseSession` (simulating
    /// a bridge crash) must still evict the local route — proving the teardown loop ran the
    /// route-read-then-close-then-release sequence instead of skipping straight past it. (Per the
    /// plan's own Step 7 note: a direct grain-side assertion would need new diagnostic-only grain
    /// surface with no existing precedent in this codebase; the route-side assertion plus the
    /// code path itself — read-route-before-CloseSession, then branch on IsHttpCloud — is the
    /// practical verification bar here, mirroring the existing precedent
    /// `PostReviewSecurityTests.SessionRoutingTable_EvictsOnStreamTeardown`.)
    /// </summary>
    [Fact]
    public async Task AgentBridgeDisconnect_EvictsHttpCloudSessionRoute_WithoutExplicitCloseSession()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var stub = builder.Build();
        stub.MapPost("/", async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            var responseJson = method switch
            {
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"routed-ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"error":{"code":-32601,"message":"unknown"}}"""
            };
            await ctx.Response.WriteAsync(responseJson);
        });
        await stub.StartAsync();
        await using var stubDisposable = stub;
        var stubUrl = stub.Urls.First();

        var seeded = await fixture.SeedUserAsync($"http-teardown-{Guid.NewGuid():N}@example.com", "HTTP Teardown Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-teardown-srv-{Guid.NewGuid():N}", stubUrl, McpServerAuthModes.None, null, null);

        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "test-agent");

        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var agentCall = await ConnectAsync(agentNodeId.Value, "agent-node", cliToken, nodeKind: "agent");

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
        var sessionId = new SessionId(sessionResponse.SessionOpened.SessionId);

        // Dispatch one frame so HttpMcpProxyGrain actually activates a ConsumerUpstream for this
        // session before we simulate the disconnect (otherwise this test would trivially pass
        // even if the release call were never wired).
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId.Value,
                SequenceNumber = 1,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""")
            }
        });
        var received = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, received.PayloadCase);

        var routingTable = fixture.Services.GetRequiredService<SessionRoutingTable>();
        Assert.NotNull(routingTable.GetParticipants(sessionId));

        // Simulate a bridge crash: tear down the stream WITHOUT sending CloseSession.
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
        Assert.True(evicted, "SessionRoutingTable did not evict the http_cloud session route after agent-bridge disconnect.");
    }

    /// <summary>
    /// Finding 16, S8: `HandleE2eKeyOfferAsync` must short-circuit `IsHttpCloud` sessions straight
    /// to `E2eNotSupported` instead of activating a junk empty-key `NodeGrain` for
    /// `NodeId.Empty` (the http_cloud "publisher"). The outcome (E2eNotSupported) is the same
    /// either way; this test proves the short-circuit reason text is the cloud-terminated one,
    /// not the generic "publisher does not support e2e-v1" text the non-http_cloud path would
    /// send if it were reached for a NodeId.Empty publisher.
    /// </summary>
    [Fact]
    public async Task E2eKeyOffer_OnHttpCloudSession_ShortCircuitsToCloudTerminatedReason()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var stub = builder.Build();
        stub.MapPost("/", async ctx =>
        {
            await ctx.Response.WriteAsync("""{"jsonrpc":"2.0","id":null,"result":{}}""");
        });
        await stub.StartAsync();
        await using var stubDisposable = stub;
        var stubUrl = stub.Urls.First();

        var seeded = await fixture.SeedUserAsync($"http-e2e-{Guid.NewGuid():N}@example.com", "HTTP E2E Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-e2e-srv-{Guid.NewGuid():N}", stubUrl, McpServerAuthModes.None, null, null);

        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "test-agent");

        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        using var agentCall = await ConnectAsync(agentNodeId.Value, "agent-node", cliToken, nodeKind: "agent");

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

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyOffer = new E2eKeyOffer
            {
                SessionId = sessionId,
                Version = 1,
                Curve = "p256",
                PubKey = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
                Salt = ByteString.CopyFrom(new byte[16])
            }
        });

        var e2eResponse = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported, e2eResponse.PayloadCase);
        Assert.Contains("cloud-terminated", e2eResponse.E2ENotSupported.Reason);
    }

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAsync(
        string nodeId, string displayName, string cliToken, string nodeKind = "")
    {
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello { NodeId = nodeId, DisplayName = displayName, NodeKind = nodeKind }
        });
        var hello = await ReadAsync(call.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, hello.PayloadCase);
        return call;
    }

    private static async Task<GatewayToNodeMessage> ReadAsync(IAsyncStreamReader<GatewayToNodeMessage> stream)
    {
        using var cts = new CancellationTokenSource(MoveNextTimeout);
        var moved = await stream.MoveNext(cts.Token);
        Assert.True(moved, "Expected a message but the stream ended.");
        return stream.Current;
    }
}
