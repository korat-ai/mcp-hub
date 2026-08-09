using System.Collections.Concurrent;
using System.Text.Json;
using Korat.Cli.Commands;
using Korat.Cli.Gateway;
using Korat.Cli.Mcp;
using Korat.Relay.V1;

namespace Korat.Cli.Service;

/// <summary>
/// Owns the published-server registry for the node service daemon.
///
/// Responsibilities:
///   1. <see cref="PublishAllAsync"/> — send PublishMcpServer for every server in the
///      initial identity snapshot and collect PublishMcpServerAcks to build the routing
///      map keyed by mcp_server_id.
///   2. <see cref="ReconcileAsync"/> — diff a new identity snapshot against the currently-
///      published set; publish newly-added servers, unpublish removed ones, and update
///      the <see cref="SessionBridge"/> routing map live.
///
/// This class is deliberately decoupled from FileSystemWatcher so it is independently
/// testable without real files or a live gRPC connection.
/// </summary>
internal sealed class NodeServiceHost
{
    private readonly NodeGatewayConnection _connection;
    private readonly SessionBridge _bridge;
    private readonly string _nodeId;

    // Currently-published set: display_name → mcp_server_id.
    // Populated by PublishAllAsync / ReconcileAsync.
    private readonly ConcurrentDictionary<string, string> _publishedByName = new(StringComparer.OrdinalIgnoreCase);

    // Reverse map: mcp_server_id → McpServerSpec (the routing map fed to SessionBridge).
    private readonly ConcurrentDictionary<string, McpServerSpec> _routingMap = new();

    // Test seam: injectable send delegate so unit tests can supply a stub that returns a
    // predictable requestId without a live gRPC connection. Production code leaves this null
    // and falls back to _connection.SendPublishMcpServerAsync.
    internal Func<LocalMcpServer, CancellationToken, Task<string>>? SendPublishOverride;

    // Hard timeout applied to every ack-collection loop so a lost message cannot wedge the
    // loop forever. Both PublishAllAsync, SyncAllAsync, and PublishAllInferencePointsAsync
    // use this constant — keep it in one place.
    private const int AckTimeoutSeconds = 30;

    public NodeServiceHost(NodeGatewayConnection connection, SessionBridge bridge, string nodeId)
    {
        _connection = connection;
        _bridge = bridge;
        _nodeId = nodeId;
    }

    /// <summary>
    /// Generic ack-collection helper shared by <see cref="PublishAllAsync"/>,
    /// <see cref="SyncAllAsync"/>, and <see cref="PublishAllInferencePointsAsync"/>.
    ///
    /// Reads messages from <paramref name="incomingMessages"/> until
    /// <paramref name="pending"/> is empty (every expected ack was resolved) or the
    /// <see cref="AckTimeoutSeconds"/>-second timeout fires.
    ///
    /// For each message, <paramref name="tryResolve"/> is called with the message and
    /// the pending dictionary. It should:
    /// <list type="bullet">
    ///   <item>Remove the matching entry from <paramref name="pending"/> and return
    ///         <see langword="true"/> if the message was consumed (ack or denial).</item>
    ///   <item>Return <see langword="false"/> if the message is unrelated and should be
    ///         returned to the caller for replay (Frame, CloseSession, unrelated AccessDenied).</item>
    /// </list>
    ///
    /// On timeout the unresolved keys are surfaced via <paramref name="onTimeout"/> and
    /// the method returns with whatever was buffered so far. The outer shutdown
    /// <see cref="CancellationToken"/> propagates normally.
    ///
    /// Returns the list of non-ack messages buffered while waiting for acks.
    /// </summary>
    private static async Task<List<GatewayToNodeMessage>> CollectAcksAsync<TPending>(
        Dictionary<string, TPending> pending,
        System.Threading.Channels.ChannelReader<GatewayToNodeMessage> incomingMessages,
        Func<GatewayToNodeMessage, Dictionary<string, TPending>, bool> tryResolve,
        Action<Dictionary<string, TPending>> onTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(AckTimeoutSeconds));
        var timeoutToken = timeoutCts.Token;

        var buffered = new List<GatewayToNodeMessage>();
        try
        {
            while (pending.Count > 0)
            {
                var msg = await incomingMessages.ReadAsync(timeoutToken);
                if (!tryResolve(msg, pending))
                    buffered.Add(msg);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout fired (not the outer shutdown token) — notify caller and proceed.
            onTimeout(pending);
        }
        // OperationCanceledException from the outer cancellationToken propagates normally.

        return buffered;
    }

    /// <summary>
    /// Sends <c>PublishMcpServer</c> for every server in <paramref name="servers"/>,
    /// collects the acks (or <c>AccessDenied</c> rejections) from
    /// <paramref name="incomingMessages"/>, and builds the routing map entries for the
    /// servers that were accepted.
    ///
    /// <paramref name="incomingMessages"/> must be the same channel reader that the frame
    /// dispatch loop will later consume; this method filters only PublishMcpServerAck and
    /// AccessDenied messages and re-queues everything else (Frame / CloseSession) for the
    /// caller to process. This is achieved by draining into a temporary list and returning
    /// the non-ack messages so the caller can re-enqueue them — but in practice acks arrive
    /// before any frames, so the list stays empty.
    ///
    /// A hard timeout of 30 s is applied (linked with <paramref name="cancellationToken"/>)
    /// so a genuinely lost message cannot wedge the loop forever; on timeout the acks that
    /// did not arrive are logged and the method returns with whatever was successfully acked.
    ///
    /// Returns the non-ack messages that were buffered while waiting for acks.
    /// </summary>
    public async Task<IReadOnlyList<GatewayToNodeMessage>> PublishAllAsync(
        IReadOnlyList<LocalMcpServer> servers,
        System.Threading.Channels.ChannelReader<GatewayToNodeMessage> incomingMessages,
        CancellationToken cancellationToken)
    {
        if (servers.Count == 0)
            return Array.Empty<GatewayToNodeMessage>();

        // FIX-3: send LaunchCommand directly as command; tokenize LaunchArguments separately.
        // The old code did ShellSplit(cmd+args) then discarded the parsed cmd and re-sent
        // the original LaunchCommand — redundant and lossy when LaunchCommand contained spaces.
        var pendingAcks = new Dictionary<string, LocalMcpServer>(servers.Count); // requestId → server
        foreach (var server in servers)
        {
            string requestId;
            if (SendPublishOverride is not null)
            {
                requestId = await SendPublishOverride(server, cancellationToken);
            }
            else
            {
                var args = McpAddCommand.TokenizeArgs(server.LaunchArguments);
                requestId = await _connection.SendPublishMcpServerAsync(
                    _nodeId, server.DisplayName, server.LaunchCommand, args, cancellationToken);
            }
            pendingAcks[requestId] = server;

            Console.WriteLine($"[service] publish requested: '{server.DisplayName}' requestId={requestId}");
        }

        // FIX-1: drain with a timeout so a lost message cannot wedge the loop.
        // Both PublishMcpServerAck (success) and AccessDenied (rejection) resolve a
        // pending request — if only PublishMcpServerAck is handled, an AccessDenied
        // falls to the buffer branch and pendingAcks never empties (hang on cold start
        // or duplicate name).
        var buffered = await CollectAcksAsync(
            pendingAcks,
            incomingMessages,
            tryResolve: (msg, pending) =>
            {
                switch (msg.PayloadCase)
                {
                    case GatewayToNodeMessage.PayloadOneofCase.PublishMcpServerAck:
                    {
                        var ack = msg.PublishMcpServerAck;
                        if (pending.TryGetValue(ack.RequestId, out var server))
                        {
                            pending.Remove(ack.RequestId);
                            var spec = new McpServerSpec(server.DisplayName, server.LaunchCommand, server.LaunchArguments);
                            _publishedByName[server.DisplayName] = ack.McpServerId;
                            _routingMap[ack.McpServerId] = spec;
                            Console.WriteLine(
                                $"[service] published: '{ack.DisplayName}' mcp_server_id={ack.McpServerId}");
                        }
                        // else: stale ack from a previous connection — ignore but do not buffer.
                        return true;
                    }

                    case GatewayToNodeMessage.PayloadOneofCase.AccessDenied:
                    {
                        // FIX-1: cloud rejects the publish (node-not-found, DuplicateServerName, …).
                        // Resolve the pending ack so the loop can make progress; do NOT add
                        // this server to the routing map.
                        var denied = msg.AccessDenied;
                        if (pending.TryGetValue(denied.RequestId, out var server))
                        {
                            pending.Remove(denied.RequestId);
                            Console.Error.WriteLine(
                                $"[service] publish rejected: '{server.DisplayName}' reason={denied.Reason}");
                            return true;
                        }
                        // Unrelated AccessDenied (e.g. session request) — buffer for caller.
                        return false;
                    }

                    default:
                        return false;
                }
            },
            onTimeout: remaining =>
            {
                foreach (var (_, server) in remaining)
                    Console.Error.WriteLine(
                        $"[service] publish ack timed out for '{server.DisplayName}' — proceeding without it.");
            },
            cancellationToken);

        // Push the updated routing map into the bridge.
        _bridge.UpdateRoutingMap(new Dictionary<string, McpServerSpec>(_routingMap));
        return buffered;
    }

    /// <summary>
    /// 021 (Layer 1): connect-time bootstrap via declarative sync (#13 ghost fix).
    ///
    /// Sends ONE <c>SyncMcpServers</c> carrying the daemon's complete current server set.
    /// The cloud reconciles (upsert + soft-retire missing servers) and replies with ONE
    /// <c>PublishMcpServerAck</c> per server so we can rebuild <c>_publishedByName</c> and
    /// <c>_routingMap</c> from scratch — they are no longer the source of truth for retire
    /// decisions on (re)connect (the cloud now holds that authority).
    ///
    /// Acks from sync carry an EMPTY RequestId; we match them by DisplayName instead.
    /// This method clears both maps before collecting acks so a reconnect with the same
    /// config always produces a clean routing map from the fresh acks.
    ///
    /// A hard timeout of 30 s is applied (same as <see cref="PublishAllAsync"/>); unresolved
    /// acks are logged and the method returns with whatever was successfully acked.
    ///
    /// Returns non-ack messages buffered while waiting for acks (same contract as PublishAllAsync).
    /// </summary>
    public async Task<IReadOnlyList<GatewayToNodeMessage>> SyncAllAsync(
        IReadOnlyList<LocalMcpServer> servers,
        System.Threading.Channels.ChannelReader<GatewayToNodeMessage> incomingMessages,
        CancellationToken cancellationToken)
    {
        // Clear maps on every (re)connect so we rebuild from the sync acks, not stale state.
        _publishedByName.Clear();
        _routingMap.Clear();

        if (servers.Count == 0)
        {
            // Authoritatively empty — cloud retires all servers for this node; routing map stays empty.
            var serverDescs = Enumerable.Empty<ServerDesc>();
            await _connection.SendSyncMcpServersAsync(_nodeId, serverDescs, cancellationToken);
            Console.WriteLine("[service] sync: 0 server(s) (authoritative empty — retiring all)");
            _bridge.UpdateRoutingMap(new Dictionary<string, McpServerSpec>(_routingMap));
            return Array.Empty<GatewayToNodeMessage>();
        }

        // Build the ServerDesc list mirroring how PublishAllAsync builds PublishMcpServer:
        // LaunchCommand is the executable, LaunchArguments is tokenized into individual args.
        var descs = servers.Select(s =>
        {
            var desc = new ServerDesc
            {
                DisplayName = s.DisplayName,
                Command = s.LaunchCommand,
                // Transport field not stored locally (CLI publish never set it either); leave empty.
            };
            desc.Args.AddRange(McpAddCommand.TokenizeArgs(s.LaunchArguments));
            return desc;
        }).ToList();

        await _connection.SendSyncMcpServersAsync(_nodeId, descs, cancellationToken);
        Console.WriteLine($"[service] sync: {servers.Count} server(s)");

        // Collect acks — one per server, matched by DisplayName (RequestId is empty for sync acks).
        // Build a pending set keyed by DisplayName; acks are matched by ack.DisplayName.
        var pendingByName = new Dictionary<string, LocalMcpServer>(
            servers.ToDictionary(s => s.DisplayName, StringComparer.OrdinalIgnoreCase));

        var buffered = await CollectAcksAsync(
            pendingByName,
            incomingMessages,
            tryResolve: (msg, pending) =>
            {
                switch (msg.PayloadCase)
                {
                    case GatewayToNodeMessage.PayloadOneofCase.PublishMcpServerAck:
                    {
                        var ack = msg.PublishMcpServerAck;
                        // Sync acks have empty RequestId — match by DisplayName.
                        if (pending.TryGetValue(ack.DisplayName, out var server))
                        {
                            pending.Remove(ack.DisplayName);
                            var spec = new McpServerSpec(server.DisplayName, server.LaunchCommand, server.LaunchArguments);
                            _publishedByName[server.DisplayName] = ack.McpServerId;
                            _routingMap[ack.McpServerId] = spec;
                            Console.WriteLine(
                                $"[service] sync ack: '{ack.DisplayName}' mcp_server_id={ack.McpServerId}");
                        }
                        // Non-empty RequestId means it's a delta-publish ack from a previous connection — ignore but do not buffer.
                        return true;
                    }

                    case GatewayToNodeMessage.PayloadOneofCase.AccessDenied:
                    {
                        var denied = msg.AccessDenied;
                        // A sync-level AccessDenied has empty RequestId; match by presence in our pending set.
                        // If it has a RequestId it may be unrelated — buffer for caller.
                        if (string.IsNullOrEmpty(denied.RequestId))
                        {
                            // The entire sync was rejected (e.g. node not found). Clear pending and log.
                            Console.Error.WriteLine(
                                $"[service] sync rejected by cloud: {denied.Reason} — skipping sync acks.");
                            pending.Clear();
                            return true;
                        }
                        return false;
                    }

                    default:
                        return false;
                }
            },
            onTimeout: remaining =>
            {
                foreach (var name in remaining.Keys)
                    Console.Error.WriteLine(
                        $"[service] sync ack timed out for '{name}' — proceeding without it.");
            },
            cancellationToken);

        _bridge.UpdateRoutingMap(new Dictionary<string, McpServerSpec>(_routingMap));
        return buffered;
    }

    /// <summary>
    /// Computes the diff between <paramref name="newServers"/> and the currently-published
    /// set. Publishes added servers (waits for their acks), unpublishes removed servers, and
    /// updates the bridge routing map.
    ///
    /// Also reconciles inference points: newly-added points in <paramref name="newPoints"/>
    /// that are not yet published are sent via <see cref="PublishAllInferencePointsAsync"/>.
    /// This mirrors the MCP-server reconcile so that a config-file change triggered by
    /// <c>korat agent add</c> (which writes config.json) causes the running daemon to publish
    /// the new inference point without requiring a full restart.
    ///
    /// Unchanged servers (same display_name, already published) are left alone — their
    /// sessions remain live.
    ///
    /// Returns any Frame/CloseSession messages that arrived while waiting for publish acks
    /// so the caller (<see cref="ServiceCommand.FrameDispatchLoopAsync"/>) can replay them
    /// — mirroring the startup path (FIX-2).
    /// </summary>
    public async Task<IReadOnlyList<GatewayToNodeMessage>> ReconcileAsync(
        IReadOnlyList<LocalMcpServer> newServers,
        System.Threading.Channels.ChannelReader<GatewayToNodeMessage> incomingMessages,
        CancellationToken cancellationToken,
        IReadOnlyList<Korat.Cli.Commands.InferencePointIdentity>? newPoints = null)
    {
        var newByName = new HashSet<string>(
            newServers.Select(s => s.DisplayName), StringComparer.OrdinalIgnoreCase);

        // ── Unpublish removed servers ────────────────────────────────────────────
        var toRemove = _publishedByName.Keys
            .Where(n => !newByName.Contains(n))
            .ToList();

        foreach (var name in toRemove)
        {
            if (_publishedByName.TryRemove(name, out var mcpServerId))
            {
                _routingMap.TryRemove(mcpServerId, out _);
                await _connection.SendUnpublishMcpServerAsync(_nodeId, mcpServerId, cancellationToken);
                Console.WriteLine($"[service] unpublished: '{name}' mcp_server_id={mcpServerId}");
            }
        }

        // ── Publish added servers ────────────────────────────────────────────────
        var toAdd = newServers
            .Where(s => !_publishedByName.ContainsKey(s.DisplayName))
            .ToList();

        var buffered = new List<GatewayToNodeMessage>();

        if (toAdd.Count > 0)
        {
            // FIX-2: return the buffered messages so the caller can replay them,
            // instead of discarding Frame/CloseSession frames that arrived during the ack wait.
            var mcpBuffered = await PublishAllAsync(toAdd, incomingMessages, cancellationToken);
            buffered.AddRange(mcpBuffered);
        }
        else
        {
            // Still update the routing map (unpublish may have shrunk it).
            _bridge.UpdateRoutingMap(new Dictionary<string, McpServerSpec>(_routingMap));
        }

        return buffered;
    }

    // ── 079: Inference Point declarative sync on connect ──────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // 029 Phase B3: InferenceRequest handling (T3 / milestone ①)
    // ─────────────────────────────────────────────────────────────────────────

    // In-flight jobs: correlation_id → CancellationTokenSource.
    // Used to honour InferenceCancel messages from the cloud.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobs =
        new(StringComparer.Ordinal);

    // ── helpers to call the send delegates or the real connection ────────────────────────────

    /// <summary>
    /// Flattens a provider's internal error detail to a bounded single-line log snippet.
    /// The provider already redacts secret-shaped substrings; this only normalizes shape
    /// for the one-line node log (newlines → spaces, hard cap).
    /// </summary>
    internal static string ToLogSnippet(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "(none)";
        var flat = detail.Replace('\r', ' ').Replace('\n', ' ');
        const int maxChars = 500;
        return flat.Length <= maxChars ? flat : flat[..maxChars] + "…";
    }

    /// <summary>Computes the diff between two server lists without touching the connection (for tests).</summary>
    public static (IReadOnlyList<string> toAdd, IReadOnlyList<string> toRemove) ComputeDiff(
        IEnumerable<string> currentNames,
        IEnumerable<string> newNames)
    {
        var current = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);
        var next = new HashSet<string>(newNames, StringComparer.OrdinalIgnoreCase);
        var toAdd = next.Where(n => !current.Contains(n)).ToList();
        var toRemove = current.Where(n => !next.Contains(n)).ToList();
        return (toAdd, toRemove);
    }

    /// <summary>Returns the current routing map snapshot (mcp_server_id → spec) for testing.</summary>
    public IReadOnlyDictionary<string, McpServerSpec> RoutingMap =>
        new Dictionary<string, McpServerSpec>(_routingMap);

    /// <summary>Returns the currently-published set (display_name → mcp_server_id) for testing.</summary>
    public IReadOnlyDictionary<string, string> PublishedByName =>
        new Dictionary<string, string>(_publishedByName, StringComparer.OrdinalIgnoreCase);
}
