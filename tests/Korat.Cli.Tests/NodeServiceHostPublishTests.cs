using System.Threading.Channels;
using Korat.Cli.Commands;
using Korat.Cli.Mcp;
using Korat.Cli.Service;
using Korat.Relay.V1;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="NodeServiceHost.PublishAllAsync"/> ack-drain loop (FIX-1)
/// and for the real <see cref="SessionBridge.OnFrameReceivedAsync"/> routing logic (FIX-8).
///
/// Uses the internal <see cref="NodeServiceHost.SendPublishOverride"/> seam so no live
/// gRPC connection is needed.
/// </summary>
public class NodeServiceHostPublishTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="NodeServiceHost"/> wired to a real <see cref="SessionBridge"/>
    /// (null-gateway is safe here because UpdateRoutingMap only replaces a volatile field
    /// and the tests never send outbound frames) with <see cref="NodeServiceHost.SendPublishOverride"/>
    /// set to the supplied delegate.
    /// </summary>
    private static (NodeServiceHost host, SessionBridge bridge) BuildHost(
        Func<LocalMcpServer, CancellationToken, Task<string>> sendOverride)
    {
        // Null gateway: UpdateRoutingMap is safe (volatile field replace). Outbound frame
        // sending via StdoutToFramePumpAsync is never reached in these tests.
        var bridge = new SessionBridge(gateway: null!, new Dictionary<string, McpServerSpec>());
        // NodeGatewayConnection cannot be constructed without a live server; we pass null
        // and rely entirely on SendPublishOverride so _connection is never called.
        var host = new NodeServiceHost(connection: null!, bridge, nodeId: "test-node");
        host.SendPublishOverride = sendOverride;
        return (host, bridge);
    }

    private static LocalMcpServer MakeServer(string name, string cmd = "node", string args = "server.js")
        => new() { DisplayName = name, LaunchCommand = cmd, LaunchArguments = args };

    // ─────────────────────────────────────────────────────────────────────────
    // FIX-1: AccessDenied unblocks the ack-drain loop
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the cloud responds with <c>AccessDenied</c> (matching the RequestId),
    /// <see cref="NodeServiceHost.PublishAllAsync"/> must complete (not hang) and
    /// must NOT add the server to the routing map.
    /// </summary>
    [Fact]
    public async Task PublishAllAsync_AccessDenied_completes_and_drops_server()
    {
        const string requestId = "req-access-denied-001";
        var server = MakeServer("github");

        // The stub returns our predictable requestId.
        var (host, _) = BuildHost((_, _) => Task.FromResult(requestId));

        // Pre-fill the incoming channel with an AccessDenied that matches the requestId.
        var incoming = Channel.CreateUnbounded<GatewayToNodeMessage>();
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            AccessDenied = new AccessDenied
            {
                RequestId = requestId,
                Reason = "node_not_found"
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffered = await host.PublishAllAsync([server], incoming.Reader, cts.Token);

        // The loop must have completed — no hang.
        Assert.Empty(buffered);

        // Server must NOT be in the routing map (access was denied).
        Assert.Empty(host.RoutingMap);
        Assert.Empty(host.PublishedByName);
    }

    /// <summary>
    /// When one server is accepted and another is denied, only the accepted server
    /// appears in the routing map, and the loop completes for both.
    /// </summary>
    [Fact]
    public async Task PublishAllAsync_mixed_ack_and_denied_only_accepted_in_map()
    {
        const string reqOk = "req-ok-001";
        const string reqDenied = "req-denied-001";
        const string mcpServerId = "srv_abc123";

        var serverOk = MakeServer("github");
        var serverDenied = MakeServer("badserver");

        var requestIds = new Queue<string>([reqOk, reqDenied]);
        var (host, _) = BuildHost((_, _) => Task.FromResult(requestIds.Dequeue()));

        var incoming = Channel.CreateUnbounded<GatewayToNodeMessage>();
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            PublishMcpServerAck = new PublishMcpServerAck
            {
                RequestId = reqOk,
                McpServerId = mcpServerId,
                DisplayName = "github"
            }
        });
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            AccessDenied = new AccessDenied
            {
                RequestId = reqDenied,
                Reason = "duplicate_server_name"
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffered = await host.PublishAllAsync([serverOk, serverDenied], incoming.Reader, cts.Token);

        Assert.Empty(buffered);
        Assert.Single(host.RoutingMap);
        Assert.True(host.RoutingMap.ContainsKey(mcpServerId));
        Assert.Equal("github", host.RoutingMap[mcpServerId].DisplayName);

        Assert.Single(host.PublishedByName);
        Assert.True(host.PublishedByName.ContainsKey("github"));
    }

    /// <summary>
    /// An <c>AccessDenied</c> whose RequestId does NOT match any pending publish is
    /// treated as an unrelated message and added to the buffered list (not consumed).
    /// </summary>
    [Fact]
    public async Task PublishAllAsync_unrelated_AccessDenied_is_buffered()
    {
        const string requestId = "req-mine";
        const string mcpServerId = "srv_xyz";
        var server = MakeServer("github");

        var (host, _) = BuildHost((_, _) => Task.FromResult(requestId));

        var incoming = Channel.CreateUnbounded<GatewayToNodeMessage>();
        // Unrelated AccessDenied (different requestId) arrives first.
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            AccessDenied = new AccessDenied
            {
                RequestId = "req-someone-else",
                Reason = "access_denied"
            }
        });
        // Then the real ack.
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            PublishMcpServerAck = new PublishMcpServerAck
            {
                RequestId = requestId,
                McpServerId = mcpServerId,
                DisplayName = "github"
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffered = await host.PublishAllAsync([server], incoming.Reader, cts.Token);

        // The unrelated AccessDenied is in the buffered list.
        Assert.Single(buffered);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, buffered[0].PayloadCase);
        Assert.Equal("req-someone-else", buffered[0].AccessDenied.RequestId);

        // The real server was published.
        Assert.True(host.RoutingMap.ContainsKey(mcpServerId));
    }

    /// <summary>
    /// Non-ack, non-denied messages (Frame / CloseSession) that arrive while waiting
    /// for acks are returned in the buffered list for the caller to replay.
    /// </summary>
    [Fact]
    public async Task PublishAllAsync_buffered_frame_messages_are_returned()
    {
        const string requestId = "req-frame-test";
        const string mcpServerId = "srv_frame";
        var server = MakeServer("github");

        var (host, _) = BuildHost((_, _) => Task.FromResult(requestId));

        var incoming = Channel.CreateUnbounded<GatewayToNodeMessage>();
        // A Frame arrives before the ack.
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            Frame = new RelayFrame { SessionId = "sess1", McpServerId = mcpServerId }
        });
        await incoming.Writer.WriteAsync(new GatewayToNodeMessage
        {
            PublishMcpServerAck = new PublishMcpServerAck
            {
                RequestId = requestId,
                McpServerId = mcpServerId,
                DisplayName = "github"
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffered = await host.PublishAllAsync([server], incoming.Reader, cts.Token);

        Assert.Single(buffered);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, buffered[0].PayloadCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIX-8: Real SessionBridge.OnFrameReceivedAsync routing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the real <see cref="SessionBridge.OnFrameReceivedAsync"/> with an unknown
    /// mcp_server_id and asserts the session is dropped (no subprocess spawned, active
    /// session count stays 0).
    /// </summary>
    [Fact]
    public async Task SessionBridge_unknown_mcp_server_id_drops_without_spawning()
    {
        // Build a routing map with one known server.
        var spec = new McpServerSpec("github", "npx", "-y @modelcontextprotocol/server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_known"] = spec };

        // SessionBridge requires a NodeGatewayConnection for outbound frame sending.
        // For this test path (drop before spawn) the gateway is never called, so we
        // pass a null-gateway bridge constructed via the internal routing-map overload.
        await using var bridge = new SessionBridge(gateway: null!, map);

        // Frame arrives for an UNKNOWN mcp_server_id — should be silently dropped.
        var payload = new byte[] { 0x01, 0x02 };
        await bridge.OnFrameReceivedAsync(
            sessionId: "sess-unknown",
            mcpServerId: "srv_UNKNOWN",
            bytes: payload,
            cancellationToken: CancellationToken.None);

        // No subprocess was spawned; the bridge has zero active sessions.
        Assert.Equal(0, bridge.ActiveSessionCount);
    }

    /// <summary>
    /// Drives the real <see cref="SessionBridge.OnFrameReceivedAsync"/> with an empty
    /// mcp_server_id on the first frame (no cache entry) — must drop gracefully.
    /// </summary>
    [Fact]
    public async Task SessionBridge_empty_mcp_server_id_on_first_frame_drops_gracefully()
    {
        var spec = new McpServerSpec("github", "npx", "-y @modelcontextprotocol/server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_known"] = spec };

        await using var bridge = new SessionBridge(gateway: null!, map);

        var payload = new byte[] { 0x01 };
        await bridge.OnFrameReceivedAsync(
            sessionId: "sess-empty",
            mcpServerId: "",
            bytes: payload,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, bridge.ActiveSessionCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FIX-029: UpCommand startup-publish contract for inference points
    // ─────────────────────────────────────────────────────────────────────────

}
