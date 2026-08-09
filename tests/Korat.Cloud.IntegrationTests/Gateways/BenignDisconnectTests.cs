using Grpc.Core;
using Korat.Cloud.Gateways;

namespace Korat.Cloud.IntegrationTests.Gateways;

/// <summary>
/// Unit tests for <see cref="NodeGatewayService.IsBenignDisconnect"/>.
/// Verifies that normal ungraceful-disconnect exception types are classified as benign
/// (logged at Information, not shipped to Sentry/GlitchTip), while unexpected exceptions
/// are not.
/// </summary>
public sealed class BenignDisconnectTests
{
    [Fact]
    public void IOException_IsBenign()
    {
        Assert.True(NodeGatewayService.IsBenignDisconnect(new IOException("connection reset")));
    }

    [Fact]
    public void OperationCanceledException_IsBenign()
    {
        Assert.True(NodeGatewayService.IsBenignDisconnect(new OperationCanceledException()));
    }

    [Fact]
    public void RpcException_Cancelled_IsBenign()
    {
        var ex = new RpcException(new Status(StatusCode.Cancelled, "cancelled"));
        Assert.True(NodeGatewayService.IsBenignDisconnect(ex));
    }

    [Fact]
    public void RpcException_Unavailable_IsBenign()
    {
        var ex = new RpcException(new Status(StatusCode.Unavailable, "unavailable"));
        Assert.True(NodeGatewayService.IsBenignDisconnect(ex));
    }

    [Fact]
    public void InvalidOperationException_IsNotBenign()
    {
        Assert.False(NodeGatewayService.IsBenignDisconnect(new InvalidOperationException("unexpected")));
    }

    [Fact]
    public void WrappedIOException_IsBenign()
    {
        var ex = new Exception("outer wrapper", new IOException("inner connection reset"));
        Assert.True(NodeGatewayService.IsBenignDisconnect(ex));
    }
}
