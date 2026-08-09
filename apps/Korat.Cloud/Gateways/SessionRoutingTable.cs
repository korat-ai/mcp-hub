using System.Collections.Concurrent;
using Grpc.Core;
using Korat.Cloud.Observability;
using Korat.Domain;
using DomainPayloadLimitPolicy = Korat.Domain.Entities.PayloadLimitPolicy;
using Korat.GrainInterfaces;
using Korat.Protocol;
using Korat.Relay.V1;
using Orleans;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Delivery result used by callers that need to distinguish a retry-safe missing peer from an
/// ambiguous transport failure or a policy rejection. The legacy bool API intentionally maps
/// every value except <see cref="Delivered"/> to <c>false</c>.
/// </summary>
internal enum FrameForwardOutcome
{
    Delivered,
    PeerUnavailable,
    Rejected,
    DeliveryUnknown,
}

/// <summary>
/// Routes RelayFrame / control messages between the agent and publisher nodes of a session.
///
/// 005-mvp-relay-minimal introduced this as an in-process-only registry. 009-nats-relay-backplane
/// makes it machine-independent:
/// - Live stream writers stay LOCAL (a writer is a local TCP stream) in <see cref="_streamsByNode"/>
///   (publishers) or <see cref="_agentStreamsByConnection"/> (agents).
/// - Peer/target is resolved from the Orleans control plane (<see cref="ISessionRouteResolver"/>),
///   never from the wire <c>target_node_id</c>.
/// - Same-machine peer → write directly (fast path). Other-machine peer → publish over the
///   <see cref="IRelayBackplane"/> (Core NATS); the peer's machine, subscribed to its inbox,
///   writes to its local stream.
/// - When NATS is absent the backplane is a no-op and behaviour is byte-for-byte the old
///   in-process relay (fallback kept ≥6 months per specs/009).
///
/// 022: agent vs publisher routing asymmetry.
/// - PUBLISHER streams (1 per node, long-lived, reconnect-robust): UNCHANGED — keyed by NodeId
///   in <see cref="_streamsByNode"/>. Publisher reconnect keeps working exactly as before.
/// - AGENT streams (N per node, ephemeral bridge processes): keyed by ConnectionId in
///   <see cref="_agentStreamsByConnection"/>. Each bridge process = its own ConnectionId = its
///   own slot → no same-NodeId eviction. Publisher→agent frames are routed to the specific
///   ConnectionId that opened the session (<see cref="SessionRouteInfo.AgentConnectionId"/>).
///
/// Thread-safety: maps are <see cref="ConcurrentDictionary{TKey,TValue}"/>. gRPC
/// <see cref="IAsyncStreamWriter{T}"/> is NOT thread-safe per stream — writes are serialized
/// with a per-writer <see cref="SemaphoreSlim"/> in <see cref="StreamEntry"/>.
///
/// Reconnect safety (publisher path): each registration carries a unique epoch (Guid).
/// UnregisterStreamAsync only removes the entry and disposes the subscription when the stored
/// epoch matches the epoch this stream registered under. Agent path is epoch-free because each
/// ConnectionId is inherently unique per stream (LOCKED #7, 022).
///
/// Byte accounting (018-Bug3): ForwardFrameAsync accumulates per-session byte deltas in
/// <see cref="_byteAccumulators"/> (O(1), lock-light ConcurrentDictionary). A background timer
/// flushes accumulated deltas to <see cref="ISessionGrain.RecordBytesAsync"/> every
/// <see cref="FlushIntervalMs"/> ms. CloseSession also triggers a final flush so no bytes are
/// lost at session end. Flush failures are swallowed (best-effort, ARCH-HIGH-3 preserved).
/// </summary>
public sealed class SessionRoutingTable : IAsyncDisposable
{
    // Flush interval for the background byte-counter flush timer.
    internal const int FlushIntervalMs = 5_000;

    // MAJOR-2: sweep interval for evicting stale closed-session route caches.
    // Aligns with the reaper grace (15 min) but is intentionally shorter so closed sessions
    // are evicted well before the reaper runs. One-shot + self-rearm (same pattern as flush timer).
    internal const int SweepIntervalMs = 60_000; // 1 minute

    private readonly IRelayBackplane _backplane;
    private readonly ISessionRouteResolver _routeResolver;
    private readonly McpToolCallInspector _inspector;
    private readonly Func<string, ISessionGrain> _sessionGrainFactory;
    private readonly Func<string, IHttpMcpProxyGrain> _httpMcpProxyGrainFactory;
    private readonly ILogger<SessionRoutingTable> _logger;

    // PUBLISHER streams — keyed by NodeId (one slot per publisher node, epoch-protected).
    private readonly ConcurrentDictionary<NodeId, StreamEntry> _streamsByNode = new();
    private readonly ConcurrentDictionary<SessionId, SessionRouteInfo> _routes = new();
    // Backplane subscriptions kept separate from stream entries so a failed local write can
    // evict the writer without disposing the subscription from within its own callback loop
    // (which would deadlock). The subscription is disposed only via UnregisterStreamAsync.
    private readonly ConcurrentDictionary<NodeId, (Guid Epoch, IAsyncDisposable Sub)> _subscriptions = new();

    // 022: AGENT streams — keyed by ConnectionId (one slot per bridge process, epoch-free).
    // Parallel to _streamsByNode but for the agent data plane.  Each bridge process that
    // presents the same agent NodeId gets its own unique ConnectionId slot → no eviction.
    private readonly ConcurrentDictionary<ConnectionId, StreamEntry> _agentStreamsByConnection = new();
    private readonly ConcurrentDictionary<ConnectionId, IAsyncDisposable> _connSubscriptions = new();

    // 018-Bug3: per-session byte accumulators — written by ForwardFrameAsync, drained by the
    // flush timer and by CloseSession. ConcurrentDictionary keeps hot-path lock contention O(1).
    private readonly ConcurrentDictionary<SessionId, ByteAccumulator> _byteAccumulators = new();

    // F22: test-only handle on the fire-and-forget final flush kicked off by CloseSession.
    // Production code never reads this; tests await it instead of sleeping on an arbitrary
    // Task.Delay so the background flush is observed deterministically (no scheduler race).
    private volatile Task _lastCloseFlush = Task.CompletedTask;

    // F1: per-session payload limit trackers — enforce PerMessageLimitBytes and
    // SessionHardLimitBytes advertised in SessionOpened.PayloadLimits. Created in
    // OpenSession with the policy from the session negotiation; removed in CloseSession.
    private readonly ConcurrentDictionary<SessionId, PayloadLimitTracker> _payloadTrackers = new();

    private readonly Timer _flushTimer;

    // MAJOR-2: background sweep timer — evicts route-cache entries for sessions the grain
    // reports as Closed. Prevents _routes / _payloadTrackers / _byteAccumulators from
    // leaking after a revoke/close that only evicted the home silo's local cache.
    private readonly Timer _sweepTimer;

    /// <summary>
    /// Production constructor: injects IClusterClient and wraps it as a grain factory delegate
    /// so tests can inject a lightweight stub without implementing the full IClusterClient interface.
    /// </summary>
    public SessionRoutingTable(
        IRelayBackplane backplane,
        ISessionRouteResolver routeResolver,
        McpToolCallInspector inspector,
        IClusterClient clusterClient,
        ILogger<SessionRoutingTable> logger)
        : this(backplane, routeResolver, inspector,
               key => clusterClient.GetGrain<ISessionGrain>(key),
               key => clusterClient.GetGrain<IHttpMcpProxyGrain>(key),
               logger)
    {
    }

    /// <summary>
    /// Internal constructor used by unit tests to inject a grain stub without a real Orleans cluster.
    /// </summary>
    internal SessionRoutingTable(
        IRelayBackplane backplane,
        ISessionRouteResolver routeResolver,
        McpToolCallInspector inspector,
        Func<string, ISessionGrain> sessionGrainFactory,
        Func<string, IHttpMcpProxyGrain> httpMcpProxyGrainFactory,
        ILogger<SessionRoutingTable> logger)
    {
        _backplane = backplane;
        _routeResolver = routeResolver;
        _inspector = inspector;
        _sessionGrainFactory = sessionGrainFactory;
        _httpMcpProxyGrainFactory = httpMcpProxyGrainFactory;
        _logger = logger;
        // M2: one-shot + self-rearm timer prevents reentrancy when a flush takes
        // longer than the period. The timer fires once, FlushAllAccumulatorsAsync
        // runs to completion, then rearms at the end of its finally block.
        _flushTimer = new Timer(
            _ => _ = FlushAllAccumulatorsAsync(),
            state: null,
            dueTime: FlushIntervalMs,
            period: Timeout.Infinite);
        // MAJOR-2: one-shot + self-rearm sweep timer — evicts stale closed-session caches.
        // Starts after SweepIntervalMs so startup is not immediately burdened.
        _sweepTimer = new Timer(
            _ => _ = SweepClosedSessionsAsync(),
            state: null,
            dueTime: SweepIntervalMs,
            period: Timeout.Infinite);
    }

    public async ValueTask DisposeAsync()
    {
        // M3: flush any accumulated bytes before disposing the timer so up-to-5 s
        // of bytes aren't silently dropped on graceful shutdown. Errors are swallowed
        // (best-effort, ARCH-HIGH-3 preserved).
        try { await FlushAllAccumulatorsAsync(rearm: false); } catch { /* swallow */ }
        await _flushTimer.DisposeAsync();
        // MAJOR-2: dispose the sweep timer — no rearm after dispose.
        await _sweepTimer.DisposeAsync();

        // 022: dispose any remaining connection subscriptions (agents that did not
        // disconnect cleanly before shutdown).
        foreach (var kvp in _connSubscriptions)
        {
            try { await kvp.Value.DisposeAsync(); } catch { /* best-effort */ }
        }
    }

    // ---------------------------------------------------------------------------
    // PUBLISHER stream registration (unchanged from pre-022)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Register a publisher node's outbound stream writer (it is connected to THIS machine)
    /// and subscribe the backplane inbox so frames from other machines reach this node.
    ///
    /// Returns the <paramref name="epoch"/> that identifies this specific registration.
    /// Pass the same epoch to <see cref="UnregisterStreamAsync"/> so that a reconnect under the
    /// same NodeId does not tear down the newer stream's registration.
    /// </summary>
    public async Task<Guid> RegisterStreamAsync(NodeId nodeId, IAsyncStreamWriter<GatewayToNodeMessage> writer, CancellationToken cancellationToken)
    {
        var epoch = Guid.NewGuid();

        // cloud-m6 fix: subscribe BEFORE inserting into the map so that if SubscribeNodeAsync
        // throws, _streamsByNode is not left with a dangling entry (the old code set the map
        // entry first, so any exception left a stale writer that would never be unregistered).
        var subscription = await _backplane.SubscribeNodeAsync(
            nodeId,
            async (message, ct) => await WriteLocalAsync(nodeId, message, ct),
            cancellationToken);

        // Insert into the stream map only after subscribe has succeeded.
        _streamsByNode[nodeId] = new StreamEntry(writer, epoch);

        // Evict and dispose the previous subscription (if any) only AFTER the new one is live
        // so the node is never transiently unsubscribed from the backplane inbox.
        if (_subscriptions.TryRemove(nodeId, out var prior))
            await prior.Sub.DisposeAsync();
        _subscriptions[nodeId] = (epoch, subscription);

        return epoch;
    }

    /// <summary>
    /// Unregister a publisher node's stream writer and tear down its backplane subscription —
    /// but only if the stored epoch matches <paramref name="epoch"/>. A mismatch means a newer
    /// stream has already re-registered under the same NodeId; the old stream's teardown must
    /// NOT remove the new stream's entry.
    ///
    /// Returns <c>true</c> when this call performed the removal (the epoch matched, no newer
    /// stream is active). Returns <c>false</c> when a newer stream has taken over — the caller
    /// should NOT MarkOffline in that case (fix #2: presence TOCTOU).
    /// </summary>
    public async Task<bool> UnregisterStreamAsync(NodeId nodeId, Guid epoch)
    {
        // Compare-and-remove the stream entry: only evict when the epoch matches.
        _streamsByNode.TryGetValue(nodeId, out var entry);
        if (entry?.Epoch == epoch)
            _streamsByNode.TryRemove(new KeyValuePair<NodeId, StreamEntry>(nodeId, entry));

        // Compare-and-remove the subscription by the same epoch.
        _subscriptions.TryGetValue(nodeId, out var sub);
        if (sub.Epoch == epoch && _subscriptions.TryRemove(new KeyValuePair<NodeId, (Guid, IAsyncDisposable)>(nodeId, sub)))
        {
            await sub.Sub.DisposeAsync();
            return true; // This stream was the active one — safe to MarkOffline.
        }

        return false; // A newer stream has taken over — do NOT MarkOffline.
    }

    // ---------------------------------------------------------------------------
    // 022: AGENT stream registration (epoch-free — ConnectionId is inherently unique)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// 022: Register an agent bridge's outbound stream writer (connected to THIS machine)
    /// and subscribe the per-connection backplane inbox.
    ///
    /// Why ConnectionId instead of NodeId: an agent can open N concurrent bridge processes,
    /// each presenting the same NodeId. Keying by NodeId would evict the prior stream
    /// (the pre-022 bug). Each stream gets its own unique ConnectionId so N bridges coexist
    /// without collision (022 spec §"Design"). Epoch-free because unique-per-stream ConnectionId
    /// cannot have a same-key reconnect race (LOCKED #7, 022).
    ///
    /// IMPORTANT: SubscribeConnectionAsync is awaited to completion BEFORE returning so the
    /// inbox is live before any RequestSession is processed (LOCKED #6, 022).
    /// </summary>
    public async Task RegisterAgentStreamAsync(
        ConnectionId connectionId,
        IAsyncStreamWriter<GatewayToNodeMessage> writer,
        CancellationToken cancellationToken)
    {
        _agentStreamsByConnection[connectionId] = new StreamEntry(writer, Guid.Empty /* epoch unused for agents */);

        // Subscribe BEFORE returning — at-most-once guarantee: a frame published to a
        // not-yet-established subject is silently dropped.  RegisterAgentStreamAsync runs
        // at Hello, strictly before any RequestSession, so the inbox is live before the
        // first frame (LOCKED #6, 022).
        var subscription = await _backplane.SubscribeConnectionAsync(
            connectionId,
            async (message, ct) => await WriteLocalToConnectionAsync(connectionId, message, ct),
            cancellationToken);

        _connSubscriptions[connectionId] = subscription;
    }

    /// <summary>
    /// 022: Unregister an agent bridge's stream writer and tear down its backplane subscription.
    /// Epoch-free — no compare-and-swap needed because ConnectionId is unique per stream
    /// (LOCKED #7, 022).
    /// </summary>
    public async Task UnregisterAgentStreamAsync(ConnectionId connectionId)
    {
        _agentStreamsByConnection.TryRemove(connectionId, out _);

        if (_connSubscriptions.TryRemove(connectionId, out var sub))
            await sub.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // RelaySession lifecycle
    // ---------------------------------------------------------------------------

    /// <summary>Record an opened session and its participants + MCP/space context (seeds the local cache).
    /// <paramref name="payloadPolicy"/> seeds the per-session payload limit tracker (F1); when null
    /// the default policy from <see cref="DomainPayloadLimitPolicy"/> is used.</summary>
    public void OpenSession(
        SessionId id,
        NodeId agent,
        NodeId publisher,
        McpServerId mcpServerId,
        SpaceId spaceId,
        ConnectionId agentConnectionId = default,
        DomainPayloadLimitPolicy? payloadPolicy = null,
        bool isHttpCloud = false)
    {
        // 022: AgentConnectionId recorded so publisher→agent frames can be routed to the
        // exact bridge stream rather than to the shared NodeId slot.
        _routes[id] = new SessionRouteInfo(agent, publisher, mcpServerId, spaceId, agentConnectionId, isHttpCloud);

        // F1: seed the per-session payload limit tracker with the negotiated policy.
        _payloadTrackers[id] = new PayloadLimitTracker(payloadPolicy);
    }

    /// <summary>
    /// Increment 1 (HTTP MCP direct-to-Space): pushes HttpMcpProxyGrain's response bytes to its
    /// consumer. This is the ONLY http_cloud delivery path (the grain never returns bytes to its
    /// caller — Crux Finding 13), so it independently applies what ForwardFrameAsync's request
    /// leg gets for free: byte accounting into the same ByteAccumulator/flush-to-
    /// ISessionGrain.RecordBytesAsync pipeline, and delivery via the same local-fast-path-then-
    /// backplane primitive (SendToConnectionAsync) every other push in this file uses — NOT
    /// IRelayBackplane.PublishToConnectionAsync directly, which would silently no-op under
    /// NullRelayBackplane (single-silo/no-NATS — see Crux Finding 13).
    ///
    /// Finding 16 (M3) — corrected reasoning vs. the pre-review draft: the per-session
    /// PayloadLimitTracker looked up here is a BEST-EFFORT, byte-accounting-parity check only,
    /// not the authoritative response-leg cap. `_payloadTrackers` only has an entry on a silo
    /// that has already called OpenSession/GetRouteAsync for this session — under 2-silo random
    /// Orleans placement, HttpMcpProxyGrain's activation silo routinely has neither, so a missing
    /// tracker here is a NORMAL cross-silo placement outcome, not evidence the session is closed
    /// (the pre-review draft's comment claimed the opposite). The AUTHORITATIVE response-leg cap
    /// (PayloadLimitPolicy.DefaultSessionHardLimitBytes) is enforced inside the grain itself,
    /// per ConsumerUpstream, which is reliable regardless of which silo the grain activates on —
    /// see HttpMcpProxyGrain.ConsumerUpstream.BytesPushed. When a tracker IS present here
    /// (same-silo or a prior cache-fill), it still enforces/accounts exactly like the request leg
    /// for parity, and a violation here still tears the session down the same way.
    ///
    /// On a limit violation: sends PayloadLimitExceeded + CloseSession to the consumer, evicts
    /// the route (mirrors ForwardFrameAsync's existing violation handling), and returns false so
    /// the caller (HttpMcpProxyGrain) knows the response was NOT pushed.
    ///
    /// TODO(optional, Finding 16 S7 — not implemented, deliberately, per the reconciliation's
    /// "mark optional, don't force"): request-leg frames get McpToolCallInspector telemetry for
    /// free inside ForwardFrameAsync (frame.Enc/frame.Meta branch); this response-leg push
    /// bypasses that entirely, so http_cloud sessions' tool-call telemetry is currently
    /// request-only, not full-duplex. Parity would mean an _inspector.Observe/ObserveMetadata
    /// call here mirroring ForwardFrameAsync's — left undone this increment; not a correctness
    /// or security gap, purely an observability completeness one.
    /// </summary>
    public async Task<bool> PushHttpCloudResponseAsync(
        SessionId sessionId, ConnectionId consumerConnectionId, byte[] responseBytes, CancellationToken cancellationToken)
    {
        // Framing: deliver every http_cloud response as a NEWLINE-TERMINATED JSON-RPC line, matching
        // the newline-delimited framing every other backend already uses — a stdio publisher emits
        // "{...}\n", and SpaceBackendSession.SendLineAsync likewise appends '\n' on the REQUEST leg.
        // HttpMcpProxyGrain builds this response from a raw HTTP JSON body with NO trailing newline,
        // so without this a LINE-BUFFERED consumer — the Space aggregator's
        // SpaceBackendSession.OnInboundBytesAsync — never sees a COMPLETE line, buffers the response
        // forever, and the backend handshake hangs until PerBackendTimeout (the http_cloud ×
        // aggregator "a granted server surfaces no tools" bug: initialize/tools/list time out, the
        // open silently fails, the server vanishes from the catalog). Harmless to a per-frame JSON
        // consumer (a gRPC/CLI node bridge): trailing whitespace after a JSON value is ignored by
        // JSON parsers, and stdio MCP clients require newline delimiting anyway. Idempotent — the
        // length check never double-terminates a response that already ends in '\n'.
        //
        // Byte-accounting: the framing '\n' is added BEFORE the per-session PayloadLimitTracker
        // check below, so the (best-effort, half-the-silos) tracker counts the delivered wire size
        // — deliberate parity with the stdio path, whose frames already carry their own '\n' inside
        // the counted Ciphertext.Length. The authoritative grain-owned cap
        // (HttpMcpProxyGrain.ConsumerUpstream.BytesPushed) counts the unframed bytes; the 1-byte
        // difference only matters for a response whose compact JSON is EXACTLY the 16 MiB
        // per-message limit (frames to 16 MiB + 1 → a spurious PayloadLimitExceeded+reconnect, never
        // corruption). Left as documented parity — an stdio 16 MiB+'\n' line is gRPC-rejected before
        // the tracker anyway, so http_cloud is if anything more lenient here.
        if (responseBytes.Length == 0 || responseBytes[^1] != (byte)'\n')
        {
            var framed = new byte[responseBytes.Length + 1];
            Array.Copy(responseBytes, framed, responseBytes.Length);
            framed[^1] = (byte)'\n';
            responseBytes = framed;
        }

        if (_payloadTrackers.TryGetValue(sessionId, out var tracker))
        {
            var violation = tracker.RecordFrame(responseBytes.Length);
            if (violation != PayloadLimitViolation.None)
            {
                var limitName = violation == PayloadLimitViolation.PerMessage ? "per_message_limit" : "session_hard_limit";
                var limitBytes = violation == PayloadLimitViolation.PerMessage
                    ? (ulong)tracker.Policy.PerMessageLimitBytes
                    : (ulong)tracker.Policy.SessionHardLimitBytes;

                _logger.LogWarning(
                    "Payload limit {LimitName} exceeded (http_cloud response push) session={SessionId} bytes={Bytes}",
                    limitName, sessionId.Value, responseBytes.Length);

                CloseSession(sessionId);
                await SendToConnectionAsync(consumerConnectionId, new GatewayToNodeMessage
                {
                    PayloadLimitExceeded = new PayloadLimitExceeded { SessionId = sessionId.Value, LimitName = limitName, LimitBytes = limitBytes }
                }, cancellationToken);
                await SendToConnectionAsync(consumerConnectionId, new GatewayToNodeMessage
                {
                    CloseSession = new CloseSession { SessionId = sessionId.Value, Reason = limitName }
                }, cancellationToken);
                return false;
            }

            // 018-Bug3 parity: this push never goes through ForwardFrameAsync's own accumulator
            // update, so it must record its own server→client bytes here.
            var acc = _byteAccumulators.GetOrAdd(sessionId, _ => new ByteAccumulator());
            acc.Add(0, responseBytes.Length);
        }
        // Finding 16 (M3): a missing tracker here is a normal cross-silo placement outcome (see
        // this method's doc comment above) — still attempt best-effort delivery; the
        // AUTHORITATIVE cap was already enforced by the caller (HttpMcpProxyGrain) before this
        // method was ever invoked. SendToConnectionAsync/backplane also no-op harmlessly against
        // a long-gone consumer stream.

        var message = new GatewayToNodeMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId.Value,
                Direction = "server_to_client",
                Ciphertext = Google.Protobuf.ByteString.CopyFrom(responseBytes)
            }
        };
        return await SendToConnectionAsync(consumerConnectionId, message, cancellationToken);
    }

    /// <summary>
    /// Increment 1 (HTTP MCP direct-to-Space): releases ONE consumer session's upstream MCP
    /// session inside its HttpMcpProxyGrain (Crux Finding 14 — per-session upstream, so closing
    /// a session must release it, not rely on server-wide idle eviction). Called from Task 5's
    /// four authoritative session-close paths (Finding 16, M1 added the 4th): the payload-limit-
    /// violation branch inside `ForwardFrameAsync`, `NodeGatewayService.HandleCloseSessionAsync`
    /// (peer-initiated close), `SessionTerminator.TerminateSessionAsync` (revoke/delete), and
    /// `NodeGatewayService`'s agent-bridge-disconnect teardown loop (the most common close of
    /// all). Best-effort — swallows failures (mirrors SessionTerminator.SendBestEffortAsync's
    /// spirit) so a dead/unreachable grain activation never blocks session teardown. NOT called
    /// from the periodic SweepClosedSessionsAsync cache-eviction sweep — that only prunes a stale
    /// LOCAL cache on silos that never saw the authoritative close; the grain's entry was already
    /// released once by whichever of the four paths above did the authoritative close.
    /// </summary>
    public async Task CloseHttpCloudConsumerSessionAsync(McpServerId mcpServerId, SessionId consumerSessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _httpMcpProxyGrainFactory(mcpServerId.Value).CloseConsumerSessionAsync(consumerSessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("CloseHttpCloudConsumerSessionAsync failed serverId={ServerId} session={SessionId} errorType={ErrorType}",
                mcpServerId.Value, consumerSessionId.Value, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Finding 16, M3: called by HttpMcpProxyGrain when ITS OWN grain-owned cumulative byte
    /// counter (reliable regardless of which silo the grain activated on) detects a
    /// session_hard_limit violation on the response leg. Mirrors PushHttpCloudResponseAsync's own
    /// violation-handling shape exactly (same two messages, same eviction) — just invoked
    /// directly from the grain instead of via a `_payloadTrackers` lookup that might not even
    /// exist on this silo (see PushHttpCloudResponseAsync's doc comment for why that lookup alone
    /// is not authoritative).
    /// </summary>
    public async Task CloseForResponsePayloadLimitAsync(SessionId sessionId, ConnectionId consumerConnectionId, CancellationToken cancellationToken)
    {
        const string limitName = "session_hard_limit";
        var limitBytes = (ulong)DomainPayloadLimitPolicy.DefaultSessionHardLimitBytes;
        _logger.LogWarning(
            "Payload limit {LimitName} exceeded (http_cloud response push, grain-owned) session={SessionId}",
            limitName, sessionId.Value);

        CloseSession(sessionId);
        await SendToConnectionAsync(consumerConnectionId, new GatewayToNodeMessage
        {
            PayloadLimitExceeded = new PayloadLimitExceeded { SessionId = sessionId.Value, LimitName = limitName, LimitBytes = limitBytes }
        }, cancellationToken);
        await SendToConnectionAsync(consumerConnectionId, new GatewayToNodeMessage
        {
            CloseSession = new CloseSession { SessionId = sessionId.Value, Reason = limitName }
        }, cancellationToken);
    }

    /// <summary>
    /// Close a session: flush any remaining accumulated bytes to the SessionGrain, evict the
    /// local route, tool-call buffers, and payload limit tracker.
    /// </summary>
    public void CloseSession(SessionId id)
    {
        _routes.TryRemove(id, out _);
        _inspector.ForgetSession(id);

        // F1: evict the payload limit tracker so it does not leak memory for closed sessions.
        _payloadTrackers.TryRemove(id, out _);

        // 018-Bug3: flush any remaining bytes collected since the last periodic flush so the
        // final counters appear in the console Sessions view. Fire-and-forget (best-effort).
        if (_byteAccumulators.TryRemove(id, out var acc))
        {
            var (c2s, s2c) = acc.Drain();
            if (c2s > 0 || s2c > 0)
            {
                // F22: keep the discarded Task observable to tests via _lastCloseFlush so they
                // can await completion deterministically. Production behaviour is unchanged
                // (still fire-and-forget — no caller awaits CloseSession).
                _lastCloseFlush = FlushSessionAsync(id, c2s, s2c, isClose: true);
            }
        }
    }

    /// <summary>
    /// Find all sessions in which the given PUBLISHER node participates (from the local route
    /// cache). Used on PUBLISHER stream teardown so cache entries do not leak after disconnect.
    ///
    /// 022: do NOT call this for agent teardown — it would match all sessions for the shared
    /// NodeId, nuking sibling bridges' sessions. Use <see cref="FindSessionsForConnection"/>
    /// for agent teardown (LOCKED #4, 022).
    /// </summary>
    public IReadOnlyCollection<SessionId> FindSessionsForNode(NodeId nodeId)
    {
        var matches = new List<SessionId>();
        foreach (var kvp in _routes)
        {
            if (kvp.Value.Agent == nodeId || kvp.Value.Publisher == nodeId)
                matches.Add(kvp.Key);
        }
        return matches;
    }

    /// <summary>
    /// 022: Find all sessions opened by a specific agent bridge stream (from the local route
    /// cache). Used on AGENT stream teardown to close only the sessions that THIS specific
    /// bridge opened, not all sessions for the shared NodeId (LOCKED #4, 022).
    /// </summary>
    public IReadOnlyCollection<SessionId> FindSessionsForConnection(ConnectionId connectionId)
    {
        var matches = new List<SessionId>();
        foreach (var kvp in _routes)
        {
            if (kvp.Value.AgentConnectionId == connectionId)
                matches.Add(kvp.Key);
        }
        return matches;
    }

    /// <summary>
    /// Local-cache peek of a session's participants — NO control-plane fallback. Returns null
    /// if the session is not cached on this machine. Used to assert the local route cache does
    /// not leak after stream teardown (ARCH-CRITICAL-1); routing itself uses <see cref="GetRouteAsync"/>.
    /// </summary>
    public (NodeId Agent, NodeId Publisher)? GetParticipants(SessionId id)
        => _routes.TryGetValue(id, out var route) ? (route.Agent, route.Publisher) : null;

    /// <summary>
    /// Resolve a session's route from the local cache, falling back to the Orleans control
    /// plane (and caching the result). Returns null for unknown sessions.
    /// </summary>
    public async Task<SessionRouteInfo?> GetRouteAsync(SessionId id, CancellationToken cancellationToken)
    {
        if (_routes.TryGetValue(id, out var cached))
            return cached;

        var resolved = await _routeResolver.ResolveAsync(id, cancellationToken);
        if (resolved is { } route)
        {
            _routes[id] = route;
            // MAJOR-1 fix: when filling the route cache from the Orleans resolver (cross-silo path)
            // we must also seed a payload limit tracker so ForwardFrameAsync enforces limits.
            // OpenSession does this on the home silo; cross-silo activations miss it because
            // OpenSession was never called here. Use the default policy (same as NodeGatewayService
            // passes to OpenSession on the home silo — see NodeGatewayService.cs line ~1169).
            _payloadTrackers.TryAdd(id, new PayloadLimitTracker());
        }
        return resolved;
    }

    /// <summary>
    /// Send a message to a publisher node — local fast path if its stream is on this machine,
    /// otherwise over the backplane to whichever machine holds it. Returns false if undeliverable.
    /// </summary>
    public async Task<bool> SendToNodeAsync(NodeId nodeId, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        var local = await WriteLocalAsync(nodeId, message, cancellationToken);
        if (local.HasValue)
            return local.Value;
        return await _backplane.PublishToNodeAsync(nodeId, message, cancellationToken);
    }

    /// <summary>
    /// 022/Step-A: send a message to an AGENT bridge stream identified by ConnectionId —
    /// local fast path if its stream is on this machine, otherwise over the backplane to
    /// whichever machine holds it. Mirrors <see cref="SendToNodeAsync"/> for the agent end.
    /// Returns false if undeliverable.
    /// </summary>
    public async Task<bool> SendToConnectionAsync(ConnectionId connectionId, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        var local = await WriteLocalToConnectionAsync(connectionId, message, cancellationToken);
        if (local.HasValue)
            return local.Value;
        return await _backplane.PublishToConnectionAsync(connectionId, message, cancellationToken);
    }

    /// <summary>
    /// Forward a frame to the opposite end of the session. Resolves the peer from the control
    /// plane (never the wire target), records tool-call telemetry, then delivers locally or
    /// over the backplane. Returns false if the session is unknown, the sender is not part of
    /// it, or the peer is unreachable.
    ///
    /// ARCH-HIGH-3 preserved: a delivery failure to the peer must NOT take down the sender's
    /// stream — WriteLocalAsync/backplane swallow IO errors and report undeliverable.
    ///
    /// 018-Bug3: payload size (frame.Ciphertext.Length) is accumulated in-memory by direction
    /// (sender == Agent → client→server; sender == Publisher → server→client). The accumulator
    /// is flushed to ISessionGrain.RecordBytesAsync periodically and on CloseSession.
    ///
    /// 022: publisher→agent frames are routed to <see cref="SessionRouteInfo.AgentConnectionId"/>
    /// (connection-keyed) instead of the agent's NodeId, so the correct bridge stream among N
    /// concurrent bridges receives the frame.
    /// </summary>
    public async Task<bool> ForwardFrameAsync(
        NodeId senderNode,
        RelayFrame frame,
        CancellationToken cancellationToken) =>
        await ForwardFrameWithOutcomeAsync(senderNode, frame, cancellationToken) == FrameForwardOutcome.Delivered;

    /// <summary>
    /// Detailed forwarding result for retry-sensitive callers. Only
    /// <see cref="FrameForwardOutcome.PeerUnavailable"/> proves that the payload was not delivered
    /// and is therefore safe to retry. A failed local gRPC write or NATS publish is ambiguous: the
    /// peer may have accepted the frame before the failure became observable.
    /// </summary>
    internal async Task<FrameForwardOutcome> ForwardFrameWithOutcomeAsync(
        NodeId senderNode,
        RelayFrame frame,
        CancellationToken cancellationToken)
    {
        var sessionId = new SessionId(frame.SessionId);
        var route = await GetRouteAsync(sessionId, cancellationToken);
        if (route is not { } r)
            return FrameForwardOutcome.PeerUnavailable;

        bool isClientToServer;
        if (r.Agent == senderNode)
        {
            // Agent → publisher direction: route by publisher NodeId (unchanged).
            isClientToServer = true;
        }
        else if (r.Publisher == senderNode)
        {
            // Publisher → agent direction: route by AgentConnectionId (022).
            isClientToServer = false;
        }
        else
        {
            return FrameForwardOutcome.Rejected; // sender is not part of this session — drop
        }

        // F1: enforce payload limits BEFORE accounting and forwarding.
        // Check per-message and cumulative session limits using the tracker seeded at OpenSession.
        // On any violation: emit PayloadLimitExceeded + CloseSession to the sender, CloseSession
        // to the peer, evict the session, and return false (fail securely, do not just log).
        var payloadBytes = frame.Ciphertext.Length;
        if (_payloadTrackers.TryGetValue(sessionId, out var tracker))
        {
            var violation = tracker.RecordFrame(payloadBytes);
            if (violation != PayloadLimitViolation.None)
            {
                var limitName = violation == PayloadLimitViolation.PerMessage
                    ? "per_message_limit"
                    : "session_hard_limit";
                var limitBytes = violation == PayloadLimitViolation.PerMessage
                    ? (ulong)tracker.Policy.PerMessageLimitBytes
                    : (ulong)tracker.Policy.SessionHardLimitBytes;

                _logger.LogWarning(
                    "Payload limit {LimitName} exceeded session={SessionId} bytes={Bytes} violator={ViolatorNode}",
                    limitName, sessionId.Value, payloadBytes, senderNode.Value);

                // Build enforcement messages.
                var exceeded = new GatewayToNodeMessage
                {
                    PayloadLimitExceeded = new PayloadLimitExceeded
                    {
                        SessionId = sessionId.Value,
                        LimitName = limitName,
                        LimitBytes = limitBytes
                    }
                };
                var closeMsg = new GatewayToNodeMessage
                {
                    CloseSession = new CloseSession
                    {
                        SessionId = sessionId.Value,
                        Reason = limitName
                    }
                };

                // Evict the session first so any concurrent call also sees it as gone.
                CloseSession(sessionId);

                // Notify the violating sender (PayloadLimitExceeded then CloseSession).
                if (isClientToServer)
                {
                    // Agent is the sender.
                    if (r.AgentConnectionId != default)
                    {
                        await SendToConnectionAsync(r.AgentConnectionId, exceeded, cancellationToken);
                        await SendToConnectionAsync(r.AgentConnectionId, closeMsg, cancellationToken);
                    }
                    else
                    {
                        await SendToNodeAsync(r.Agent, exceeded, cancellationToken);
                        await SendToNodeAsync(r.Agent, closeMsg, cancellationToken);
                    }
                    // Increment 1 (Crux Finding 5/14): for an http_cloud session there is no
                    // publisher stream to notify — release this consumer's upstream MCP session
                    // inside HttpMcpProxyGrain instead of a wasted SendToNodeAsync(NodeId.Empty,
                    // ...) no-op (per-session upstream means a session close must always release
                    // it, not just leave it for idle GC).
                    if (r.IsHttpCloud)
                        await CloseHttpCloudConsumerSessionAsync(r.McpServerId, sessionId, cancellationToken);
                    else
                        await SendToNodeAsync(r.Publisher, closeMsg, cancellationToken);
                }
                else
                {
                    // Publisher is the sender.
                    await SendToNodeAsync(r.Publisher, exceeded, cancellationToken);
                    await SendToNodeAsync(r.Publisher, closeMsg, cancellationToken);
                    // Notify the peer agent.
                    if (r.AgentConnectionId != default)
                        await SendToConnectionAsync(r.AgentConnectionId, closeMsg, cancellationToken);
                    else
                        await SendToNodeAsync(r.Agent, closeMsg, cancellationToken);
                }

                return FrameForwardOutcome.Rejected;
            }
        }

        // 018-Bug3: accumulate ciphertext payload size by direction (O(1), no lock).
        if (payloadBytes > 0)
        {
            var acc = _byteAccumulators.GetOrAdd(sessionId, _ => new ByteAccumulator());
            acc.Add(isClientToServer ? payloadBytes : 0, isClientToServer ? 0 : payloadBytes);
        }

        // 031-relay-confidentiality: inspection path branches on enc field.
        // enc==1 + meta present → E2E frame: read ONLY the cleartext metadata header (tool name/category).
        //   The cloud MUST NOT attempt to parse the ciphertext payload (which is tag||ct).
        // enc==0 + meta absent  → legacy plaintext frame: use the original line-buffering inspector.
        // enc!=0 + meta absent  → encrypted with no metadata; skip telemetry for this frame.
        if (frame.Enc == 1 && frame.Meta != null)
        {
            _inspector.ObserveMetadata(
                frame.Meta.ToolName,
                frame.Meta.Category,
                r.McpServerId,
                r.SpaceId,
                frame.Direction ?? string.Empty);
        }
        else if (frame.Enc == 0 && frame.Meta == null)
        {
            // Legacy plaintext path — old CLIs and backward-compat non-E2E sessions.
            _inspector.Observe(sessionId, r.McpServerId, r.SpaceId, frame.Direction, frame.Ciphertext.Span);
        }
        // else: enc!=0 && meta==null → skip telemetry (partial/corrupt frame guard)

        // 016: stamp the MCP server id ONLY on frames going to the publisher (serving) node
        // so a multi-server node service can route the session to the right local server.
        // The agent side never needs it. Frames are forwarded once, so mutating here is safe.
        if (!isClientToServer)
        {
            // Publisher → agent: route by ConnectionId (022).
            var message = new GatewayToNodeMessage { Frame = frame };
            var localConn = await WriteLocalToConnectionAsync(r.AgentConnectionId, message, cancellationToken);
            if (localConn.HasValue)
                return localConn.Value ? FrameForwardOutcome.Delivered : FrameForwardOutcome.DeliveryUnknown;
            var published = await _backplane.PublishToConnectionAsync(
                r.AgentConnectionId, message, cancellationToken);
            return ClassifyBackplaneResult(published);
        }
        else
        {
            frame.McpServerId = r.McpServerId.Value;

            // Increment 1 (Crux Finding 13/15): dispatch ONE-WAY to HttpMcpProxyGrain and return
            // as soon as it ACCEPTS the frame — this leg's bytes were already limit-checked/
            // accounted above like any other frame; only the delivery TARGET differs. The grain
            // performs the upstream call and pushes the response to r.AgentConnectionId
            // asynchronously via PushHttpCloudResponseAsync — NOT a synchronous return.
            if (r.IsHttpCloud)
            {
                try
                {
                    await _httpMcpProxyGrainFactory(r.McpServerId.Value)
                        .DispatchFrameAsync(frame.Ciphertext.ToByteArray(), r.AgentConnectionId, sessionId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Defense in depth — DispatchFrameAsync itself is designed to accept-and-
                    // return without throwing; this only guards a truly unexpected grain-call
                    // failure (e.g. Orleans transport fault). Push a generic error directly since
                    // the grain never got the chance to.
                    _logger.LogWarning("ForwardFrameAsync: http_cloud dispatch failed serverId={ServerId} errorType={ErrorType}",
                        r.McpServerId.Value, ex.GetType().Name);
                    var errorBytes = System.Text.Encoding.UTF8.GetBytes(
                        """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Internal relay error."}}""");
                    await PushHttpCloudResponseAsync(sessionId, r.AgentConnectionId, errorBytes, cancellationToken);
                }
                return FrameForwardOutcome.Delivered;
            }

            // Agent → publisher: route by NodeId (unchanged from pre-022).
            var message = new GatewayToNodeMessage { Frame = frame };
            var local = await WriteLocalAsync(r.Publisher, message, cancellationToken);
            if (local.HasValue)
                return local.Value ? FrameForwardOutcome.Delivered : FrameForwardOutcome.DeliveryUnknown;
            var published = await _backplane.PublishToNodeAsync(r.Publisher, message, cancellationToken);
            return ClassifyBackplaneResult(published);
        }
    }

    private FrameForwardOutcome ClassifyBackplaneResult(bool published)
    {
        if (published)
            return FrameForwardOutcome.Delivered;

        // With the single-silo no-op backplane and no local stream, no transport attempted a
        // write. A real backplane can fail after handing bytes to the network, so its false result
        // is not strong enough to authorize an automatic retry.
        return _backplane is NullRelayBackplane
            ? FrameForwardOutcome.PeerUnavailable
            : FrameForwardOutcome.DeliveryUnknown;
    }

    // ---------------------------------------------------------------------------
    // 018-Bug3: byte-counter flush helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Exposed for unit tests: manually trigger a full accumulator flush without waiting for
    /// the background timer. Do NOT call from production code paths.
    /// </summary>
    internal Task FlushAccumulatorsForTestAsync() => FlushAllAccumulatorsAsync(rearm: false);

    /// <summary>
    /// Exposed for unit tests: await the fire-and-forget final flush started by the most recent
    /// <see cref="CloseSession"/> call. Lets tests observe the close-path flush deterministically
    /// instead of racing it with a fixed delay. Do NOT call from production code paths.
    /// </summary>
    internal Task LastCloseFlushForTestAsync() => _lastCloseFlush;

    /// <summary>
    /// Flush accumulated byte deltas for all live sessions to their SessionGrains.
    /// Called by the background timer every FlushIntervalMs ms. Errors are swallowed
    /// (best-effort, ARCH-HIGH-3 preserved).
    ///
    /// M2: <paramref name="rearm"/> controls whether the one-shot timer is re-armed
    /// at the end (true = normal timer path; false = dispose path / test helper).
    /// </summary>
    private async Task FlushAllAccumulatorsAsync(bool rearm = true)
    {
        try
        {
            foreach (var kvp in _byteAccumulators)
            {
                var (c2s, s2c) = kvp.Value.Drain();
                if (c2s == 0 && s2c == 0)
                    continue;
                await FlushSessionAsync(kvp.Key, c2s, s2c, isClose: false);
            }
        }
        finally
        {
            // M2: self-rearm the one-shot timer after each flush completes so the
            // interval is measured from flush-completion, preventing reentrancy.
            if (rearm)
            {
                try { _flushTimer.Change(FlushIntervalMs, Timeout.Infinite); }
                catch (ObjectDisposedException) { /* disposed between flush and rearm — ignore */ }
            }
        }
    }

    /// <summary>
    /// Send a single byte-count delta to the SessionGrain. Best-effort: any exception is
    /// logged at debug level and swallowed so it cannot affect the relay hot path.
    ///
    /// M1: on failure for non-close flushes, re-credit the drained bytes back into
    /// the accumulator so the next timer tick retries rather than losing the delta.
    /// For close-path flushes (<paramref name="isClose"/> = true) re-crediting is
    /// skipped — the accumulator entry is already removed, and resurrection would
    /// create a new entry for a closed session that will never be drained.
    /// </summary>
    private async Task FlushSessionAsync(SessionId sessionId, long c2s, long s2c, bool isClose)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _sessionGrainFactory(sessionId.Value)
                .RecordBytesAsync(c2s, s2c)
                .WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // M1: on a non-close flush failure, re-credit the drained bytes so the
            // next timer tick retries. On a close-path failure, just log — we must
            // not resurrect a removed accumulator for a session that is already closed.
            if (!isClose && (c2s > 0 || s2c > 0))
                _byteAccumulators.GetOrAdd(sessionId, _ => new ByteAccumulator()).Add(c2s, s2c);

            // Best-effort — a flush failure must not break forwarding or throw into the relay path.
            _logger.LogDebug("Byte-counter flush failed session={SessionId} isClose={IsClose} errorType={ErrorType}",
                sessionId.Value, isClose, ex.GetType().Name);
        }
    }

    // ---------------------------------------------------------------------------
    // MAJOR-2: stale closed-session cache eviction sweep
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Exposed for unit tests: manually trigger a closed-session sweep without waiting for
    /// the background timer. Do NOT call from production code paths.
    /// </summary>
    internal Task SweepClosedSessionsForTestAsync() => SweepClosedSessionsAsync(rearm: false);

    /// <summary>
    /// Periodically asks the session grain for each locally-cached route whether the session
    /// is still live. For any session the grain reports as Closed (or Failed / unknown), evict
    /// <c>_routes</c>, <c>_payloadTrackers</c>, and <c>_byteAccumulators</c> on this silo so
    /// they do not leak after a revoke/close that was only applied on the home silo.
    ///
    /// MAJOR-2: SessionTerminator evicts the local route on the silo that handled the revoke,
    /// but other silos only have the resolver-filled cache and never learn of the close.
    /// This sweep closes that gap without adding a new NATS control subject.
    ///
    /// Design constraints:
    /// - One-shot + self-rearm (same pattern as flush timer) — no reentrancy.
    /// - Per-entry catch+log so a single grain failure does not abort the sweep.
    /// - No re-credit on sweep eviction — the accumulator for a closed session must not be
    ///   resurrected by FlushSessionAsync's re-credit path (the grain will have default Id and
    ///   skip the write anyway — see SessionGrain.RecordBytesAsync H1 guard).
    /// - Eviction uses CloseSession so the inspector is also cleaned up.
    /// </summary>
    private async Task SweepClosedSessionsAsync(bool rearm = true)
    {
        // Snapshot the keys so concurrent modifications during the sweep don't affect iteration.
        var candidates = _routes.Keys.ToArray();

        foreach (var sessionId in candidates)
        {
            try
            {
                // Ask the grain (single-activation, cluster-visible) whether the session is live.
                // Use a short timeout so a slow/unavailable grain does not block the sweep tick.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var session = await _sessionGrainFactory(sessionId.Value)
                    .GetAsync()
                    .WaitAsync(cts.Token);

                // Only evict when we have a definitive "closed" verdict from the grain.
                // If the grain is unavailable the catch below fires and we skip this entry safely.
                if (session.Status is SessionStatus.Closed or SessionStatus.Failed)
                {
                    _logger.LogDebug(
                        "Sweep: evicting closed session route session={SessionId} reason={Reason}",
                        sessionId.Value, session.CloseReason?.ToString() ?? "unknown");
                    CloseSession(sessionId);
                }
            }
            catch (Exception ex)
            {
                // A grain failure (timeout, unavailable) is not a reason to evict — skip safely.
                _logger.LogDebug(
                    "Sweep: grain check failed session={SessionId} errorType={ErrorType} — skipping",
                    sessionId.Value, ex.GetType().Name);
            }
        }

        if (rearm)
        {
            try { _sweepTimer.Change(SweepIntervalMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* disposed between sweep and rearm — ignore */ }
        }
    }

    /// <summary>
    /// Per-session byte accumulator. Add() is called on the relay hot path (concurrent);
    /// Drain() is called by the flush timer or CloseSession (single-flusher context).
    /// Uses Interlocked for lock-free updates — no SemaphoreSlim on the hot path.
    /// </summary>
    internal sealed class ByteAccumulator
    {
        private long _c2s;
        private long _s2c;

        public void Add(long clientToServer, long serverToClient)
        {
            if (clientToServer > 0)
                Interlocked.Add(ref _c2s, clientToServer);
            if (serverToClient > 0)
                Interlocked.Add(ref _s2c, serverToClient);
        }

        /// <summary>Atomically read and reset both counters, returning the drained deltas.</summary>
        public (long ClientToServer, long ServerToClient) Drain()
        {
            var c2s = Interlocked.Exchange(ref _c2s, 0);
            var s2c = Interlocked.Exchange(ref _s2c, 0);
            return (c2s, s2c);
        }
    }

    /// <summary>
    /// Write a message to a PUBLISHER node's LOCAL stream with per-writer serialization.
    /// Returns null when the node has no local stream on this machine (caller should try the
    /// backplane), true on success, false when the local write failed (peer half-closed) — in
    /// which case the dead writer is evicted (its subscription is torn down later by
    /// UnregisterStreamAsync on stream teardown).
    /// </summary>
    private async Task<bool?> WriteLocalAsync(NodeId nodeId, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        if (!_streamsByNode.TryGetValue(nodeId, out var entry))
            return null;

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            await entry.Writer.WriteAsync(message, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Peer stream is dead — evict so subsequent writes don't retry a doomed stream.
            // Do NOT dispose its subscription here (we may be inside that subscription's own
            // delivery callback; disposing would await our own loop → deadlock).
            _streamsByNode.TryRemove(nodeId, out _);
            _logger.LogDebug("Local write failed node={NodeId} errorType={ErrorType}", nodeId.Value, ex.GetType().Name);
            return false;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// 022: Write a message to an AGENT bridge stream's LOCAL stream with per-writer serialization.
    /// Mirrors <see cref="WriteLocalAsync"/> but reads/evicts <see cref="_agentStreamsByConnection"/>
    /// by ConnectionId (LOCKED #5, 022).
    /// Returns null when the connection has no local stream (caller should try the backplane),
    /// true on success, false when the local write failed.
    /// </summary>
    private async Task<bool?> WriteLocalToConnectionAsync(ConnectionId connectionId, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        if (!_agentStreamsByConnection.TryGetValue(connectionId, out var entry))
            return null;

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            await entry.Writer.WriteAsync(message, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Dead agent bridge stream — evict so subsequent frames don't retry.
            _agentStreamsByConnection.TryRemove(connectionId, out _);
            _logger.LogDebug("Local write failed connId={ConnectionId} errorType={ErrorType}", connectionId.Value, ex.GetType().Name);
            return false;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private sealed class StreamEntry(IAsyncStreamWriter<GatewayToNodeMessage> writer, Guid epoch)
    {
        public IAsyncStreamWriter<GatewayToNodeMessage> Writer { get; } = writer;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public Guid Epoch { get; } = epoch;
    }
}
