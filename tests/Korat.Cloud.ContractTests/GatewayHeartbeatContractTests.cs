using Korat.Cloud.IntegrationTests;
using Korat.Domain;
using Korat.Relay.V1;

namespace Korat.Cloud.ContractTests;

public sealed class GatewayHeartbeatContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Heartbeat_ReturnsHeartbeatAck()
    {
        // Seed a user with a space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"heartbeat-contract-{Guid.NewGuid():N}@example.com", "Heartbeat Contract Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var client = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = client.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "heartbeat-contract",
                // SpaceId resolved server-side from Bearer token.
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Heartbeat = new Heartbeat
            {
                NodeId = nodeId,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.HeartbeatAck, call.ResponseStream.Current.PayloadCase);
        Assert.Equal(nodeId, call.ResponseStream.Current.HeartbeatAck.NodeId);
    }
}
