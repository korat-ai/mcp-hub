using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

public sealed class LocalDevAccessFlowTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task PublishConnectApproveGrantFlow_Succeeds()
    {
        // Seed a user with a space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"local-dev-flow-{Guid.NewGuid():N}@example.com", "Local Dev Flow Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var publisherNode = NodeId.New().Value;
        var publisherClient = GrpcTestClient.Create(fixture.Factory);
        var publisherCallOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var publisherCall = publisherClient.Connect(publisherCallOptions);
        await publisherCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = publisherNode,
                DisplayName = "publisher",
                // SpaceId resolved server-side from Bearer token.
            }
        });
        Assert.True(await publisherCall.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, publisherCall.ResponseStream.Current.PayloadCase);

        var server = (await fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId)
            .PublishMcpServerAsync(new NodeId(publisherNode), "demo-flow", "echo", "korat-demo-server"))!;

        var agentNode = NodeId.New().Value;
        // ARCH-CRITICAL-2: register the agent-client up-front so the gateway's NodeId
        // mismatch check passes (the test reuses agentNode as the agent-client id).
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentNode)
            .RegisterAsync(new SpaceId(seeded.SpaceId), new NodeId(agentNode), "test-agent");
        var agentClient = GrpcTestClient.Create(fixture.Factory);
        var agentCallOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var agentCall = agentClient.Connect(agentCallOptions);
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = agentNode,
                DisplayName = "agent",
                NodeKind = "agent",
                // SpaceId resolved server-side from Bearer token.
            }
        });
        Assert.True(await agentCall.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, agentCall.ResponseStream.Current.PayloadCase);

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentNode,
                McpServerId = server.Id.Value
            }
        });
        await agentCall.ResponseStream.MoveNext(CancellationToken.None);
        var pending = agentCall.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessPending, pending.PayloadCase);

        using var ownerClient = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        (await ownerClient.PostAsync($"/api/access-requests/{pending.AccessPending.AccessRequestId}/approve", null)).EnsureSuccessStatusCode();

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentNode,
                McpServerId = server.Id.Value
            }
        });
        await agentCall.ResponseStream.MoveNext(CancellationToken.None);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, agentCall.ResponseStream.Current.PayloadCase);
    }
}
