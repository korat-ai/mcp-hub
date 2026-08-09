using Grpc.Core;
using Grpc.Net.Client;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

public static class GrpcTestClient
{
    public static NodeGatewayService.NodeGatewayServiceClient Create(KoratTestHost factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler()
        });
        return new NodeGatewayService.NodeGatewayServiceClient(channel);
    }

    /// <summary>
    /// Returns a <see cref="CallOptions"/> pre-populated with an
    /// <c>Authorization: Bearer &lt;token&gt;</c> metadata entry.
    /// Pass the returned options to <c>client.Connect(options)</c> so the
    /// gateway sees the Bearer token before the first Hello message arrives.
    /// </summary>
    public static CallOptions BearerCallOptions(string cliToken)
    {
        var metadata = new Metadata { { "authorization", $"Bearer {cliToken}" } };
        return new CallOptions(metadata);
    }
}
