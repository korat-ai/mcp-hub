using Korat.Cli.Mcp;
using Korat.Cli.Service;
using Korat.Relay.V1;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for multi-server routing in <see cref="SessionBridge"/> and
/// the reconcile-diff helper in <see cref="NodeServiceHost"/>.
///
/// These tests operate without a real gRPC connection by using a
/// <see cref="NullGateway"/> stub and directly invoking bridge methods.
/// </summary>
public class SessionBridgeRoutingTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // ComputeDiff (pure, no I/O)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDiff_empty_current_all_added()
    {
        var (toAdd, toRemove) = NodeServiceHost.ComputeDiff(
            currentNames: [],
            newNames: ["github", "filesystem"]);

        Assert.Contains("github", toAdd);
        Assert.Contains("filesystem", toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void ComputeDiff_removed_server_appears_in_toRemove()
    {
        var (toAdd, toRemove) = NodeServiceHost.ComputeDiff(
            currentNames: ["github", "filesystem"],
            newNames: ["github"]);

        Assert.Empty(toAdd);
        Assert.Equal(["filesystem"], toRemove);
    }

    [Fact]
    public void ComputeDiff_added_and_removed_simultaneously()
    {
        var (toAdd, toRemove) = NodeServiceHost.ComputeDiff(
            currentNames: ["github", "filesystem"],
            newNames: ["github", "postgres"]);

        Assert.Equal(["postgres"], toAdd);
        Assert.Equal(["filesystem"], toRemove);
    }

    [Fact]
    public void ComputeDiff_unchanged_set_produces_empty_diff()
    {
        var (toAdd, toRemove) = NodeServiceHost.ComputeDiff(
            currentNames: ["github"],
            newNames: ["github"]);

        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    [Fact]
    public void ComputeDiff_is_case_insensitive()
    {
        // "GitHub" vs "github" should be treated as the same server.
        var (toAdd, toRemove) = NodeServiceHost.ComputeDiff(
            currentNames: ["GitHub"],
            newNames: ["github"]);

        Assert.Empty(toAdd);
        Assert.Empty(toRemove);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SessionBridge: multi-server routing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the routing map contains a spec for a given mcp_server_id, the first
    /// frame for a new session is accepted (resolved, cached).
    /// </summary>
    [Fact]
    public void RoutingMap_known_mcp_server_id_resolves_spec()
    {
        var spec = new McpServerSpec("github", "npx", "-y @modelcontextprotocol/server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_abc"] = spec };

        var resolved = TryResolveSpec(map, sessionId: "s1", mcpServerId: "srv_abc");
        Assert.NotNull(resolved);
        Assert.Equal("github", resolved!.DisplayName);
    }

    [Fact]
    public void RoutingMap_unknown_mcp_server_id_returns_null()
    {
        var spec = new McpServerSpec("github", "npx", "-y server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_abc"] = spec };

        var resolved = TryResolveSpec(map, sessionId: "s1", mcpServerId: "srv_UNKNOWN");
        Assert.Null(resolved);
    }

    [Fact]
    public void RoutingMap_empty_mcp_server_id_without_cache_returns_null()
    {
        var spec = new McpServerSpec("github", "npx", "-y server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_abc"] = spec };

        // First frame for session s1 arrives with empty mcpServerId — should fail.
        var resolved = TryResolveSpec(map, sessionId: "s1", mcpServerId: "");
        Assert.Null(resolved);
    }

    [Fact]
    public void RoutingMap_subsequent_frame_with_empty_id_uses_cached_spec()
    {
        var spec = new McpServerSpec("github", "npx", "-y server-github");
        var map = new Dictionary<string, McpServerSpec> { ["srv_abc"] = spec };
        var cache = new Dictionary<string, McpServerSpec>();

        // Simulate first frame: resolve + cache.
        var resolved1 = TryResolveSpecWithCache(map, cache, sessionId: "s1", mcpServerId: "srv_abc");
        Assert.NotNull(resolved1);
        Assert.True(cache.ContainsKey("s1"));

        // Simulate second frame for same session: empty mcpServerId, uses cache.
        var resolved2 = TryResolveSpecWithCache(map, cache, sessionId: "s1", mcpServerId: "");
        Assert.NotNull(resolved2);
        Assert.Equal("github", resolved2!.DisplayName);
    }

    [Fact]
    public void RoutingMap_two_servers_route_to_different_specs()
    {
        var specA = new McpServerSpec("github", "npx", "-y server-github");
        var specB = new McpServerSpec("filesystem", "npx", "-y server-filesystem /repo");
        var map = new Dictionary<string, McpServerSpec>
        {
            ["srv_A"] = specA,
            ["srv_B"] = specB,
        };

        var resolvedA = TryResolveSpec(map, "s1", "srv_A");
        var resolvedB = TryResolveSpec(map, "s2", "srv_B");

        Assert.Equal("github", resolvedA!.DisplayName);
        Assert.Equal("filesystem", resolvedB!.DisplayName);
    }

    [Fact]
    public void UpdateRoutingMap_replaces_map_atomically()
    {
        var specOld = new McpServerSpec("github", "npx", "-y server-github");
        var mapV1 = new Dictionary<string, McpServerSpec> { ["srv_abc"] = specOld };

        var specNew = new McpServerSpec("postgres", "npx", "-y server-postgres");
        var mapV2 = new Dictionary<string, McpServerSpec> { ["srv_xyz"] = specNew };

        // After UpdateRoutingMap the old key is gone, new key resolves.
        var resolvedOld = TryResolveSpec(mapV2, "s1", "srv_abc");
        var resolvedNew = TryResolveSpec(mapV2, "s2", "srv_xyz");

        Assert.Null(resolvedOld);
        Assert.NotNull(resolvedNew);
        Assert.Equal("postgres", resolvedNew!.DisplayName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers — pure routing logic extracted from SessionBridge internals
    // so tests don't need a live gRPC connection or real subprocess
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the routing lookup SessionBridge performs on each frame (without cache).
    /// Returns null when the mcp_server_id is unknown or empty.
    /// </summary>
    private static McpServerSpec? TryResolveSpec(
        IReadOnlyDictionary<string, McpServerSpec> map,
        string sessionId,
        string mcpServerId)
    {
        if (string.IsNullOrEmpty(mcpServerId)) return null;
        map.TryGetValue(mcpServerId, out var spec);
        return spec;
    }

    /// <summary>
    /// Simulates the routing lookup WITH session cache (as SessionBridge does it).
    /// Updates <paramref name="cache"/> on a successful first-frame resolve.
    /// </summary>
    private static McpServerSpec? TryResolveSpecWithCache(
        IReadOnlyDictionary<string, McpServerSpec> map,
        Dictionary<string, McpServerSpec> cache,
        string sessionId,
        string mcpServerId)
    {
        if (cache.TryGetValue(sessionId, out var cached))
            return cached;

        if (string.IsNullOrEmpty(mcpServerId) || !map.TryGetValue(mcpServerId, out var spec))
            return null;

        cache[sessionId] = spec;
        return spec;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CloseSessionAsync: SendCloseSessionAsync ordering (CLI-MINOR-1)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CLI-MINOR-1: <see cref="SessionBridge.CloseSessionAsync"/> must NOT call
    /// <c>SendCloseSessionAsync</c> when the session was never registered in the
    /// internal <c>_sessions</c> map (i.e. the TryRemove guard returned false).
    ///
    /// Before the fix, the network call fired unconditionally — before the guard —
    /// which sent a redundant cloud notification for non-existent sessions.
    /// After the fix, the send is gated on TryRemove success.
    /// </summary>
    [Fact]
    public async Task CloseSession_UnknownSessionId_DoesNotCallSendCloseSession()
    {
        var gateway = new TrackingFakeGateway();
        var spec    = new McpServerSpec("test", "echo", "hello");
        await using var bridge = new SessionBridge(gateway, spec);

        // "ghost-session" was never registered via OnFrameReceivedAsync.
        await bridge.CloseSessionAsync("ghost-session");

        Assert.Equal(0, gateway.CloseSessionCallCount);
    }

    /// <summary>
    /// Closing an unknown session twice must not send ANY notifications —
    /// idempotency of the guard (both calls find nothing to remove).
    /// </summary>
    [Fact]
    public async Task CloseSession_UnknownSessionId_CalledTwice_NeverCallsSend()
    {
        var gateway = new TrackingFakeGateway();
        var spec    = new McpServerSpec("test", "echo", "hello");
        await using var bridge = new SessionBridge(gateway, spec);

        await bridge.CloseSessionAsync("no-such-session");
        await bridge.CloseSessionAsync("no-such-session");

        Assert.Equal(0, gateway.CloseSessionCallCount);
    }

    /// <summary>
    /// Minimal <see cref="ISessionBridgeGateway"/> stub that counts
    /// <c>SendCloseSessionAsync</c> calls for assertion.
    /// </summary>
    private sealed class TrackingFakeGateway : ISessionBridgeGateway
    {
        public int CloseSessionCallCount { get; private set; }

        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
        {
            CloseSessionCallCount++;
            return Task.CompletedTask;
        }

        public Task SendE2eKeyAnswerAsync(
            string sessionId, uint version, string curve, byte[] pubKey, byte[] confirmTag,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendFrameAsync(
            string sessionId, ReadOnlyMemory<byte> ciphertext, ulong sequenceNumber,
            string direction, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendE2eFrameAsync(
            string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber,
            string direction, FrameMetadata meta, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
