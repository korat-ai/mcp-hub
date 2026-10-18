using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;

namespace Korat.Auth.Tests.SpaceMcp;

public sealed class SpaceBackendNotificationTests
{
    [Fact]
    public async Task Utf8CharacterSplitAcrossRelayFrames_IsNotCorrupted()
    {
        // Notification-only parser test: no outbound route is used.
        var backend = new SpaceBackendSession(null!, NodeId.New(), "server", "docs", "session");
        JsonObject? received = null;
        backend.Notification += (_, p) => received = p;
        var prefix = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/updated\",\"params\":{\"uri\":\"file:///caf");
        var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/updated\",\"params\":{\"uri\":\"file:///café\"}}\n");
        await backend.OnInboundBytesAsync(bytes[..(prefix.Length + 1)], 0, default);
        Assert.Null(received);
        await backend.OnInboundBytesAsync(bytes[(prefix.Length + 1)..], 0, default);
        Assert.Equal("file:///café", received?["uri"]?.GetValue<string>());
    }

    [Fact]
    public async Task EncryptedFrame_IsNotTreatedAsAResourceNotification()
    {
        var backend = new SpaceBackendSession(null!, NodeId.New(), "server", "docs", "session");
        var notifications = 0;
        backend.Notification += (_, _) => notifications++;
        await backend.OnInboundBytesAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}\n"), 1, default);
        Assert.Equal(0, notifications);
        Assert.False(backend.IsAlive);
    }
}
