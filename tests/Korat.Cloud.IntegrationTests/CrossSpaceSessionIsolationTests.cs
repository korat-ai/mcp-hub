using Grpc.Core;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// P1 multi-tenant isolation: an agent authenticated in Space A must NEVER open a session
/// to an MCP server that lives in Space B.
///
/// Admission path (NodeGatewayService.HandleRequestSessionAsync):
///   conn.SpaceId != server.SpaceId  → AccessDenied (NotFound), nothing created (F45)
/// — conn.SpaceId is resolved server-side from A's bearer, so a request to B's server is denied
/// before any agent-client bind, grant lookup, or access-request creation in Space B.
///
/// This test is the headline guarantee: "another user's data will not be delivered to me."
/// The assertions are intentionally strong — we check not only the response code but also that
/// no live session, no grant, and no access-request were created in Space B for A's agent client.
/// </summary>
public sealed class CrossSpaceSessionIsolationTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    /// <summary>
    /// Space-A's agent, authenticated with Space-A's own valid bearer token, requests a
    /// session to an MCP server that belongs to Space B.
    ///
    /// Expected: AccessDenied (no SessionOpened, no AccessPending). The gateway detects
    ///   conn.SpaceId (A) != serverB.SpaceId (B)
    /// immediately after loading the server and denies the request as NotFound before any
    /// agent-client bind, grant lookup, or access-request creation in Space B (F45 hardening).
    /// </summary>
    [Fact]
    public async Task CrossSpace_AgentA_RequestSession_ToServerB_ReturnsAccessDenied_NotSessionOpened()
    {
        // ── Arrange: Space A ──────────────────────────────────────────────────
        // Space A: a real user with a CLI token (the bearer the agent will authenticate with).
        var (spaceAId, cliTokenA, _) = await SeedUserAndTokenAsync("cs-space-a");

        // Register an agent-client in Space A.  The node id doubles as the agent-client id
        // (same convention as ConnectAccessRequestTests).
        var agentNodeIdA = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeIdA, agentNodeIdA, spaceAId);

        // ── Arrange: Space B ──────────────────────────────────────────────────
        // Space B: an independent user who publishes an MCP server.
        var (spaceBId, _, _) = await SeedUserAndTokenAsync("cs-space-b");

        var publisherNodeB = NodeId.New();
        var serverB = (await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(spaceBId)
            .PublishMcpServerAsync(publisherNodeB, $"cross-space-srv-{Guid.NewGuid():N}", "echo", "b"))!;

        // ── Act ───────────────────────────────────────────────────────────────
        // A's agent connects using A's bearer and asks for a session to B's server.
        using var call = await ConnectAgentAsync(agentNodeIdA, cliTokenA);
        var response = await RequestSessionAsync(call, agentNodeIdA, serverB.Id.Value);

        // ── Assert: headline guarantee ────────────────────────────────────────
        // (1) The gateway must NOT open a session.
        Assert.NotEqual(
            GatewayToNodeMessage.PayloadOneofCase.SessionOpened,
            response.PayloadCase);

        // (2) F45: a cross-space request is hard-denied (NotFound), NOT parked as AccessPending.
        //     A pending request would leak the server's existence into B's approval queue and
        //     enable confused-deputy escalation on owner mis-approval.
        Assert.Equal(
            GatewayToNodeMessage.PayloadOneofCase.AccessDenied,
            response.PayloadCase);
        Assert.Equal(
            KoratError.Message(KoratErrorCode.NotFound),
            response.AccessDenied.Reason);

        // (3) Space B must have no active grant for A's agent on B's server.
        var grantsB = await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(spaceBId)
            .ListGrantsAsync();
        Assert.DoesNotContain(grantsB, g =>
            g.ConsumerId.Value == agentNodeIdA &&
            g.McpServerId == serverB.Id &&
            g.Status == GrantStatus.Active);

        // (4) Space A must have no grants at all (nothing was approved).
        var grantsA = await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(spaceAId)
            .ListGrantsAsync();
        Assert.Empty(grantsA);

        // (5) F45: Space B must have NO access-request row created by A's cross-space attempt.
        //     The guard runs before CreateAccessRequestAsync, so the foreign approval queue
        //     stays clean (no spam vector, no confused-deputy target).
        var requestsB = await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(spaceBId)
            .ListAccessRequestsAsync();
        Assert.DoesNotContain(requestsB, r =>
            r.ConsumerId.Value == agentNodeIdA &&
            r.McpServerId == serverB.Id);

        // (6) Space B must have no live sessions against B's server from A's agent.
        var sessionsB = await fixture.ClusterClient
            .GetGrain<ISpaceGrain>(spaceBId)
            .ListSessionsAsync(includeClosed: false);
        Assert.DoesNotContain(sessionsB, s =>
            s.McpServerId == serverB.Id &&
            s.Status is SessionStatus.Active or SessionStatus.Opening);
    }

    [Theory]
    [InlineData(McpServerStatus.Disabled)]
    [InlineData(McpServerStatus.NeedsReauth)]
    public async Task CrossSpace_ServerState_IsNotDisclosed(McpServerStatus serverStatus)
    {
        var (spaceAId, cliTokenA, _) = await SeedUserAndTokenAsync($"state-a-{serverStatus}");
        var agentNodeIdA = NodeId.New().Value;
        await RegisterAgentClientAsync(agentNodeIdA, agentNodeIdA, spaceAId);

        var (spaceBId, _, ownerBId) = await SeedUserAndTokenAsync($"state-b-{serverStatus}");
        var spaceB = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceBId);

        McpServer serverB;
        if (serverStatus == McpServerStatus.NeedsReauth)
        {
            serverB = await spaceB.CreateHttpMcpServerAsync(
                $"cross-space-reauth-{Guid.NewGuid():N}",
                "https://mcp.example.test/",
                McpServerAuthModes.Oauth,
                authHeaderName: null,
                secretHint: null);
        }
        else
        {
            serverB = (await spaceB.PublishMcpServerAsync(
                NodeId.New(),
                $"cross-space-disabled-{Guid.NewGuid():N}",
                "echo",
                "b"))!;
            await fixture.ClusterClient.GetGrain<IMcpServerGrain>(serverB.Id.Value)
                .DisableAsync(ownerBId);
        }

        using var call = await ConnectAgentAsync(agentNodeIdA, cliTokenA);
        var response = await RequestSessionAsync(call, agentNodeIdA, serverB.Id.Value);

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal(
            KoratError.Message(KoratErrorCode.NotFound),
            response.AccessDenied.Reason);
    }

    // ─── helpers (mirror ConnectAccessRequestTests) ───────────────────────────

    private async Task<(string SpaceId, string CliToken, Korat.Domain.Auth.UserId UserId)> SeedUserAndTokenAsync(string tag)
    {
        var seeded = await fixture.SeedUserAsync(
            $"cross-space-{tag}-{Guid.NewGuid():N}@example.com", $"CrossSpace-{tag}");
        var token = await fixture.IssueCliTokenAsync(seeded.UserId);
        return (seeded.SpaceId, token, seeded.UserId);
    }

    private Task RegisterAgentClientAsync(string agentClientId, string nodeId, string spaceId) =>
        fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId)
            .RegisterAsync(new SpaceId(spaceId), new NodeId(nodeId), "cross-space-agent");

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
                DisplayName = "cross-space-agent",
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
