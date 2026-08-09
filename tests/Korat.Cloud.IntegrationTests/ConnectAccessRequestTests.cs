using Grpc.Core;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

public sealed class ConnectAccessRequestTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    // Each test creates its own seeded user so tests are fully isolated even when run
    // concurrently within the same fixture lifetime.

    [Fact]
    public async Task RequestSession_WithoutGrant_ReturnsAccessPending()
    {
        var (spaceId, cliToken) = await SeedUserSpaceAndTokenAsync();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"connect-pending-{Guid.NewGuid():N}", "echo", "demo"))!;

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        using var call = await ConnectAgentAsync(agentNodeId, cliToken);
        var response = await RequestSessionAsync(call, agentNodeId, server.Id.Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessPending, response.PayloadCase);
        Assert.False(string.IsNullOrWhiteSpace(response.AccessPending.AccessRequestId));
    }

    [Fact]
    public async Task RequestSession_DisabledServer_ReturnsAccessDenied()
    {
        var (spaceId, cliToken, userId) = await SeedUserSpaceAndTokenAsync2();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"connect-disabled-{Guid.NewGuid():N}", "echo", "demo"))!;
        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value)
            .DisableAsync(userId);

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        using var call = await ConnectAgentAsync(agentNodeId, cliToken);
        var response = await RequestSessionAsync(call, agentNodeId, server.Id.Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
    }

    [Fact]
    public async Task RequestSession_NeedsReauthServer_ReturnsAccessDenied()
    {
        var (spaceId, cliToken, userId) = await SeedUserSpaceAndTokenAsync2();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"connect-needsreauth-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);
        Assert.Equal(McpServerStatus.NeedsReauth, server.Status); // oauth create is pre-consent, no /enable needed

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        using var call = await ConnectAgentAsync(agentNodeId, cliToken);
        var response = await RequestSessionAsync(call, agentNodeId, server.Id.Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
    }

    [Fact]
    public async Task RequestSession_UnknownServerId_ReturnsAccessDenied()
    {
        var (spaceId, cliToken) = await SeedUserSpaceAndTokenAsync();

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        using var call = await ConnectAgentAsync(agentNodeId, cliToken);
        var response = await RequestSessionAsync(call, agentNodeId, McpServerId.New().Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
    }

    [Fact]
    public async Task ApproveAfterDisconnect_NextRequestSessionOpensSession()
    {
        var (spaceId, cliToken, userId) = await SeedUserSpaceAndTokenAsync2();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"late-approve-{Guid.NewGuid():N}", "echo", "demo"))!;
        // 021: opening a session requires the publisher node to be Online (admission gate). In
        // production the publisher connects (Hello → ConnectAsync → Status=Online) before publishing;
        // this test seeds the server via the grain directly, so mark the node Online to match reality.
        await MarkPublisherOnlineAsync(publisherNode, spaceId);

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        string accessRequestId;
        using (var call = await ConnectAgentAsync(agentNodeId, cliToken))
        {
            var pending = await RequestSessionAsync(call, agentNodeId, server.Id.Value);
            accessRequestId = pending.AccessPending.AccessRequestId;
        }

        using var ownerClient = await fixture.CreateAuthenticatedClientAsync(userId);
        (await ownerClient.PostAsync($"/api/access-requests/{accessRequestId}/approve", null)).EnsureSuccessStatusCode();

        using var secondCall = await ConnectAgentAsync(agentNodeId, cliToken);
        var opened = await RequestSessionAsync(secondCall, agentNodeId, server.Id.Value);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, opened.PayloadCase);
    }

    [Fact]
    public async Task PendingRequest_VisibleAfterClientDisconnect()
    {
        var (spaceId, cliToken, userId) = await SeedUserSpaceAndTokenAsync2();
        var publisherNode = NodeId.New();
        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(publisherNode, $"timeout-pending-{Guid.NewGuid():N}", "echo", "demo"))!;

        var agentNodeId = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeId, agentNodeId, spaceId);
        string accessRequestId;
        using (var call = await ConnectAgentAsync(agentNodeId, cliToken))
        {
            var pending = await RequestSessionAsync(call, agentNodeId, server.Id.Value);
            accessRequestId = pending.AccessPending.AccessRequestId;
        }

        using var client = await fixture.CreateAuthenticatedClientAsync(userId);
        var json = await client.GetStringAsync("/api/access-requests");
        Assert.Contains(accessRequestId, json);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Seeds a user+space and issues a CLI token. Returns (SpaceId, cliToken).</summary>
    private async Task<(string SpaceId, string CliToken)> SeedUserSpaceAndTokenAsync()
    {
        var seeded = await fixture.SeedUserAsync(
            $"connect-test-{Guid.NewGuid():N}@example.com", "Connect Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        return (seeded.SpaceId, cliToken);
    }

    /// <summary>Seeds a user+space and issues a CLI token. Returns (SpaceId, cliToken, UserId).</summary>
    private async Task<(string SpaceId, string CliToken, Korat.Domain.Auth.UserId UserId)> SeedUserSpaceAndTokenAsync2()
    {
        var seeded = await fixture.SeedUserAsync(
            $"connect-test-{Guid.NewGuid():N}@example.com", "Connect Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        return (seeded.SpaceId, cliToken, seeded.UserId);
    }

    private Task RegisterAgentClientAsync(string agentClientId, string nodeId, string spaceId) =>
        fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId)
            .RegisterAsync(new SpaceId(spaceId), new NodeId(nodeId), "test-agent");

    /// <summary>021: marks a publisher NodeGrain Online (Status set by ConnectAsync) so the
    /// session-open admission gate (publisher must be online) passes — mirrors a connected publisher.</summary>
    private Task MarkPublisherOnlineAsync(NodeId publisherNode, string spaceId) =>
        fixture.ClusterClient.GetGrain<INodeGrain>(publisherNode.Value)
            .ConnectAsync(new SpaceId(spaceId), "publisher", new GatewayId("test-gateway"));

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
                // SpaceId intentionally omitted — resolved server-side from Bearer token.
            }
        });
        await call.ResponseStream.MoveNext(CancellationToken.None);
        // Verify Hello was accepted.
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
