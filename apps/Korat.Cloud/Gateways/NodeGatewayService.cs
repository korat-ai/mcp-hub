using Grpc.Core;
using Korat.Cloud.Gateways.Admission;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Relay.V1;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Per-stream context captured from the Hello handshake.
/// Shared mutably only within the single-threaded await-foreach loop.
/// </summary>
internal sealed class ConnectionContext
{
    /// <summary>True after a Hello message has been processed.</summary>
    public bool HelloReceived { get; set; }

    /// <summary>Node that opened this stream (from Hello.NodeId).</summary>
    public NodeId NodeId { get; set; }

    /// <summary>Space this node belongs to (from Hello.SpaceId or ISpaceResolver for Bearer callers).</summary>
    public SpaceId SpaceId { get; set; }

    /// <summary>
    /// Non-null when the call was authenticated via <c>Authorization: Bearer &lt;cli-token&gt;</c>
    /// at the stream level (resolved before the Hello message loop) and the token was valid.
    /// When set, <see cref="HandleHelloAsync"/> resolves <see cref="SpaceId"/> via
    /// <c>ISpaceResolver</c> instead of trusting <c>hello.SpaceId</c>.
    /// </summary>
    public Guid? BearerUserId { get; set; }

    /// <summary>
    /// Set when a Bearer header was present but the token was invalid/expired/revoked.
    /// <see cref="HandleHelloAsync"/> will reject the Hello (fail closed — sec M1).
    /// </summary>
    public bool BearerPresentButInvalid { get; set; }

    /// <summary>
    /// 022: unique per-stream id generated once in HandleHelloAsync and stored here so the
    /// teardown finally-block and HandleRequestSessionAsync can use the same value.
    /// Also echoed in GatewayHello.connection_id (replacing the prior throwaway New() call).
    ///
    /// Why: agent bridges are addressed by ConnectionId (not NodeId) in the routing table,
    /// so we need a stable per-stream id that survives the full lifetime of the gRPC stream.
    /// </summary>
    public ConnectionId ConnectionId { get; set; }

    /// <summary>
    /// 022: role of this node (Agent / Publisher), parsed from Hello.NodeKind in HandleHelloAsync.
    /// Stored so the teardown finally-block and frame dispatch can branch agent vs publisher
    /// routing without re-parsing the wire field.
    /// </summary>
    public NodeKind NodeKind { get; set; }

    /// <summary>
    /// PR-5 (agent-id-identity, additive): the hosted Agent this bridge connection is acting for,
    /// echoed by the CLI on <c>NodeHello.agent_id</c> — set ONLY by an "agent" node_kind
    /// connection (korat connect --agent). Empty for publisher connections and for legacy CLIs
    /// that predate this field. Captured here (like DisplayName) in HandleHelloAsync so
    /// HandleRequestSessionAsync's once-per-client TOFU bind can stamp
    /// <see cref="Korat.Domain.Entities.Agent.ConsumerAgentClientId"/> by the EXACT AgentId — no
    /// id8-parsing, no scan.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>User-facing runtime/consumer name captured from NodeHello.</summary>
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class NodeGatewayService(
    IClusterClient clusterClient,
    IMetadataRepository repository,
    IConfiguration configuration,
    SessionRoutingTable routingTable,
    IRelayBackplane inferenceBackplane,
    ICliTokenService cliTokens,
    ISsoTokenValidator ssoTokens,
    ISsoIdentityResolver ssoIdentities,
    SpaceResolver spaceResolver,
    ISessionAdmission admission,
    SessionTerminator sessionTerminator,
    Korat.Cloud.Security.Audit.IAuditLog auditLog,
    ILogger<NodeGatewayService> logger)
    : Relay.V1.NodeGatewayService.NodeGatewayServiceBase
{
    // G2: stable gateway id for this silo instance derived from configuration or machine name.
    // Resolved once per service instance (registered as scoped/transient by gRPC, so keep it as a
    // property backed by configuration rather than a field so it is re-read per request if needed).
    private GatewayId StableGatewayId =>
        new(configuration["Korat:Cloud:GatewayId"] ?? Environment.MachineName);

    /// <summary>
    /// F8: write a single AccessDenied envelope to the node stream. Centralizes the
    /// repeated <c>WriteAsync(new GatewayToNodeMessage { AccessDenied = new AccessDenied { ... } })</c>
    /// shape so a change to the envelope (e.g. an added field) touches one place, not ~27 call sites.
    /// <paramref name="requestId"/> defaults to empty for the pre-Hello / fire-and-forget rejections
    /// that carry no request correlation id.
    /// </summary>
    private static Task DenyAsync(
        IServerStreamWriter<GatewayToNodeMessage> stream,
        string reason,
        string requestId = "",
        CancellationToken cancellationToken = default) =>
        stream.WriteAsync(new GatewayToNodeMessage
        {
            AccessDenied = new AccessDenied
            {
                RequestId = requestId,
                Reason = reason
            }
        }, cancellationToken);

    /// <summary>
    /// F8: emit the AccessDenied for a role-guarded message (publisher-only / agent-only) and log
    /// the rejection. Collapses the near-identical role-guard blocks in the Connect switch into one
    /// call. <paramref name="reason"/> is the wire reason ("publisher-only message" / "agent-only
    /// message"); <paramref name="messageName"/> is used only in the LogWarning text.
    /// </summary>
    private Task RejectWrongRoleAsync(
        IServerStreamWriter<GatewayToNodeMessage> stream,
        string messageName,
        NodeId nodeId,
        string offendingRole,
        string reason,
        string requestId = "",
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "{MessageName} rejected — {OffendingRole} node sent {Reason} nodeId={NodeId}",
            messageName, offendingRole, reason, nodeId.Value);
        return DenyAsync(stream, reason, requestId, cancellationToken);
    }

    public override async Task Connect(
        IAsyncStreamReader<NodeToGatewayMessage> requestStream,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        ServerCallContext context)
    {
        // G1 / G5: per-stream state — captures NodeId from Hello so subsequent handlers
        // can attribute messages to the correct node without re-reading the proto field.
        var conn = new ConnectionContext();

        // Reconnect safety (fix #1/#2): each accepted Hello issues a unique epoch from
        // RegisterStreamAsync. The epoch is passed to UnregisterStreamAsync in the finally
        // block so an old stream's teardown cannot evict the newer stream's registration
        // (compare-and-remove semantics). Sentinel Guid.Empty = Hello not yet accepted.
        var streamEpoch = Guid.Empty;

        // Resolve call-level Bearer auth from gRPC metadata before entering the message loop.
        // Uses a tri-state outcome:
        //   Valid   → store UserId; HandleHelloAsync uses Bearer path.
        //   Invalid → mark BearerPresentButInvalid so HandleHelloAsync fails closed
        //             (revoked CLI token must not be accepted).
        //   Absent  → BearerUserId/BearerPresentButInvalid stay at defaults;
        //             HandleHelloAsync will reject with "Invalid node auth token".
        var bearerResult = await GrpcAuthHelper.TryResolveBearerAsync(
            context.RequestHeaders,
            cliTokens,
            ssoTokens,
            ssoIdentities,
            context.CancellationToken);
        conn.BearerUserId = bearerResult.UserId;
        conn.BearerPresentButInvalid = bearerResult.Outcome == BearerOutcome.Invalid;

        try
        {
            // G3: wrap the loop so we can clean up on disconnect or exception.
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                // G5: reject any message before Hello (except Hello itself).
                if (!conn.HelloReceived && message.PayloadCase != NodeToGatewayMessage.PayloadOneofCase.Hello)
                {
                    await DenyAsync(responseStream, "Handshake required", cancellationToken: context.CancellationToken);
                    continue;
                }

                switch (message.PayloadCase)
                {
                    case NodeToGatewayMessage.PayloadOneofCase.Hello:
                        // 022: reject a SECOND Hello on the same stream. Re-running HandleHelloAsync
                        // would mint a new conn.ConnectionId and re-register, leaking the first
                        // agent stream's entry + NATS subscription (the teardown finally only
                        // unregisters the CURRENT ConnectionId). Real clients send exactly one Hello.
                        if (conn.HelloReceived)
                        {
                            await DenyAsync(responseStream, "Duplicate Hello", cancellationToken: context.CancellationToken);
                            continue;
                        }
                        var helloAccepted = await HandleHelloAsync(message.Hello, conn, responseStream, context.CancellationToken);
                        // 005-mvp-relay-minimal: register this stream so relayed frames can be
                        // delivered to this node. Registered after HandleHelloAsync sets conn.NodeId
                        // and conn.NodeKind (022).
                        if (helloAccepted)
                        {
                            // 022: branch on node kind.
                            // Agent streams → keyed by ConnectionId (epoch-free; unique per stream).
                            //   RegisterAgentStreamAsync awaits SubscribeConnectionAsync before
                            //   returning, so the inbox is live before the first RequestSession
                            //   (LOCKED #6, 022).
                            // Publisher streams → keyed by NodeId with epoch (unchanged).
                            if (conn.NodeKind == NodeKind.Agent)
                            {
                                await routingTable.RegisterAgentStreamAsync(conn.ConnectionId, responseStream, context.CancellationToken);
                                // streamEpoch stays Guid.Empty for agents (epoch-free path, LOCKED #7).
                            }
                            else
                            {
                                // Returns the epoch that identifies THIS specific stream registration.
                                // The epoch is used in the finally block to implement compare-and-remove
                                // so a reconnecting node's old teardown path does not evict the new stream.
                                streamEpoch = await routingTable.RegisterStreamAsync(conn.NodeId, responseStream, context.CancellationToken);
                            }
                        }
                        else
                        {
                            // W8: invalid node auth token — terminate the stream. The finally block
                            // will not run any MarkOffline / session cleanup because conn.HelloReceived
                            // remains false (HandleHelloAsync did not set it on the rejection path).
                            return;
                        }
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.Heartbeat:
                        // ARCH-HIGH-1: bind heartbeat attribution to the Hello-authenticated
                        // NodeId on this stream — ignore whatever the wire payload claims.
                        await HandleHeartbeatAsync(message.Heartbeat, conn, responseStream, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.PublishMcpServer:
                        // cloud-m3: publisher-only message — reject if this stream is an agent.
                        if (conn.NodeKind == NodeKind.Agent)
                        {
                            await RejectWrongRoleAsync(responseStream, "PublishMcpServer", conn.NodeId, "agent", "publisher-only message", cancellationToken: context.CancellationToken);
                            break;
                        }
                        await HandlePublishAsync(message.PublishMcpServer, conn, responseStream, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.UnpublishMcpServer:
                        // cloud-m3: publisher-only message — reject if this stream is an agent.
                        if (conn.NodeKind == NodeKind.Agent)
                        {
                            await RejectWrongRoleAsync(responseStream, "UnpublishMcpServer", conn.NodeId, "agent", "publisher-only message", cancellationToken: context.CancellationToken);
                            break;
                        }
                        await HandleUnpublishAsync(message.UnpublishMcpServer, conn, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.SyncMcpServers:
                        // 021 (Layer 1): node declares its complete server set on (re)connect.
                        // Cloud makes state match: upsert all declared servers, soft-retire the rest.
                        // cloud-m3: publisher-only message — reject if this stream is an agent.
                        if (conn.NodeKind == NodeKind.Agent)
                        {
                            await RejectWrongRoleAsync(responseStream, "SyncMcpServers", conn.NodeId, "agent", "publisher-only message", cancellationToken: context.CancellationToken);
                            break;
                        }
                        await HandleSyncMcpServersAsync(message.SyncMcpServers, conn, responseStream, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.RequestSession:
                        // cloud-m3: agent-only message — reject if this stream is a publisher.
                        if (conn.NodeKind == NodeKind.Publisher)
                        {
                            await RejectWrongRoleAsync(responseStream, "RequestSession", conn.NodeId, "publisher", "agent-only message", message.RequestSession.RequestId, context.CancellationToken);
                            break;
                        }
                        await HandleRequestSessionAsync(message.RequestSession, conn, responseStream, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.Frame:
                        // 005-mvp-relay-minimal: forward the frame to the opposite end of the
                        // session via the in-process routing table. Cleartext frames only —
                        // ciphertext is interpreted as plaintext bytes for MVP (constitution II
                        // amendment documented in docs/decision-log.md).
                        if (string.IsNullOrEmpty(message.Frame.SessionId))
                            break;
                        var delivered = await routingTable.ForwardFrameAsync(
                            conn.NodeId,
                            message.Frame,
                            context.CancellationToken);
                        if (!delivered)
                        {
                            logger.LogWarning(
                                "Relay frame undeliverable session={SessionId} senderNode={NodeId}",
                                message.Frame.SessionId,
                                conn.NodeId.Value);
                        }
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.CloseSession:
                        // ARCH-HIGH-2: handle peer-initiated session close instead of
                        // silently dropping the message. Validate the sender is part of
                        // the session, evict the routing entry, close the SessionGrain,
                        // and forward CloseSession to the peer so it can tear down its
                        // subprocess (publisher) or terminate the agent call.
                        await HandleCloseSessionAsync(message.CloseSession, conn, context.CancellationToken);
                        break;
                    // 031: E2E key exchange — relay-mediated ECDH. The cloud validates that the sender
                    // is a participant of the session and forwards the message to the peer unchanged.
                    // The cloud NEVER derives or sees session key material; it only routes public keys
                    // and confirm tags between authenticated stream identities.
                    //
                    // Trust model: passive cloud cannot derive K_payload. Active cloud (key-swap MITM)
                    // is a DOCUMENTED RESIDUAL — see CRYPTO.md §2.
                    case NodeToGatewayMessage.PayloadOneofCase.E2EKeyOffer:
                        if (!string.IsNullOrEmpty(message.E2EKeyOffer.SessionId))
                            await HandleE2eKeyOfferAsync(message.E2EKeyOffer, conn, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.E2EKeyAnswer:
                        if (!string.IsNullOrEmpty(message.E2EKeyAnswer.SessionId))
                            await HandleE2eKeyAnswerAsync(message.E2EKeyAnswer, conn, context.CancellationToken);
                        break;
                    case NodeToGatewayMessage.PayloadOneofCase.E2EKeyConfirm:
                        if (!string.IsNullOrEmpty(message.E2EKeyConfirm.SessionId))
                            await HandleE2eKeyConfirmAsync(message.E2EKeyConfirm, conn, context.CancellationToken);
                        break;

                    case NodeToGatewayMessage.PayloadOneofCase.RegisterPushToken:
                        // 030 (push-to-wake): mobile node reports its APNs device token so the
                        // cloud can wake it when an agent requests a session while the node is
                        // offline. Attribution mirrors Heartbeat: the Hello-bound NodeId is used;
                        // the advisory wire field node_id is validated but not trusted for authz.
                        await HandleRegisterPushTokenAsync(message.RegisterPushToken, conn, context.CancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation — client disconnected or server shutting down.
        }
        catch (Exception ex) when (IsBenignDisconnect(ex))
        {
            // Ungraceful client disconnect (node restart, network blip, deploy restart) surfaces as
            // IOException/connection-reset/cancelled-RPC on the bidi stream read. Expected, not an error.
            logger.LogInformation("Node stream ended (disconnect) node={NodeId} errorType={ErrorType}",
                conn.NodeId.Value, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // G3: log error class only, no payload contents (FR-016 / Principle II).
            logger.LogError("Stream error on node={NodeId} errorType={ErrorType}",
                conn.NodeId.Value, ex.GetType().Name);
            throw;
        }
        finally
        {
            // G3: ensure node is cleaned up whenever this stream ends.
            if (conn.HelloReceived)
            {
                // 022: branch on node kind for teardown.
                // AGENT teardown (LOCKED #1, 022): skip MarkOfflineAsync entirely — a sibling
                // bridge process may still be live under the same NodeId. The display-only stale
                // heuristic (019) ages out a fully-gone agent. No admission path reads agent
                // Status — the 021 admission check reads only the PUBLISHER node's Status.
                // Clean up only the sessions opened by THIS bridge connection (by ConnectionId,
                // NOT by NodeId — FindSessionsForNode would nuke sibling bridges' sessions).
                //
                // PUBLISHER teardown: unchanged — epoch compare-and-remove + MarkOffline.
                if (conn.NodeKind == NodeKind.Agent)
                {
                    await routingTable.UnregisterAgentStreamAsync(conn.ConnectionId);

                    // ARCH-CRITICAL-1 (agent side): evict only sessions opened by THIS bridge
                    // (matched by AgentConnectionId, not by NodeId — LOCKED #4, 022).
                    foreach (var sessionId in routingTable.FindSessionsForConnection(conn.ConnectionId))
                    {
                        // Finding 16, M1: resolve the route BEFORE CloseSession evicts it — this
                        // is the 4th (and most common) session-close path; without reading
                        // IsHttpCloud here first, an http_cloud session's grain ConsumerUpstream
                        // would never be released on an ordinary bridge disconnect, only on the
                        // other three paths (peer CloseSession, revoke/delete, payload-limit
                        // violation).
                        var route = await routingTable.GetRouteAsync(sessionId, CancellationToken.None);
                        routingTable.CloseSession(sessionId);
                        if (route is { IsHttpCloud: true } r)
                            await routingTable.CloseHttpCloudConsumerSessionAsync(r.McpServerId, sessionId, CancellationToken.None);

                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await clusterClient.GetGrain<ISessionGrain>(sessionId.Value)
                                .CloseAsync(SessionCloseReason.Completed)
                                .WaitAsync(cts.Token);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning("Session close on agent teardown failed session={SessionId} errorType={ErrorType}",
                                sessionId.Value, ex.GetType().Name);
                        }
                    }
                    // No MarkOfflineAsync for agents (LOCKED #1, 022).
                }
                else
                {
                    // Publisher path: epoch compare-and-remove (unchanged from pre-022).
                    // 005-mvp-relay-minimal: drop this node's stream-writer entry so subsequent
                    // ForwardFrame calls targeting it return "undeliverable" instead of writing
                    // to a closed stream.
                    //
                    // Reconnect safety (fix #1): pass the epoch so UnregisterStreamAsync performs
                    // a compare-and-remove — if the node has already reconnected under the same
                    // NodeId and registered a new stream, the new stream's entry is left intact.
                    // Returns true when THIS stream was the active one (epoch matched), false when
                    // a newer stream has taken over.
                    var wasActiveStream = await routingTable.UnregisterStreamAsync(conn.NodeId, streamEpoch);

                    // ARCH-CRITICAL-1: evict any sessions in which this publisher node participates
                    // so SessionRoutingTable._routes does not grow monotonically. Close the
                    // corresponding SessionGrain best-effort. Only do session cleanup when this
                    // stream was the authoritative one to avoid double-closing sessions that the
                    // newer stream will handle itself.
                    if (wasActiveStream)
                    {
                        foreach (var sessionId in routingTable.FindSessionsForNode(conn.NodeId))
                        {
                            // Notify the agent connection through the cluster-wide terminator,
                            // not just the local session grain. Space-MCP aggregators use this
                            // close to evict the dead relay session while retaining its catalog for
                            // a lazy reopen. This is also required when the aggregator lives on a
                            // different silo from the publisher's gRPC stream.
                            try
                            {
                                await sessionTerminator.TerminateSessionAsync(
                                    sessionId, SessionCloseReason.Completed, CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                // Repository/control-plane failure must not abort cleanup for the
                                // publisher's remaining sessions or skip MarkOffline below.
                                routingTable.CloseSession(sessionId);
                                logger.LogWarning(
                                    "Session termination on publisher teardown failed session={SessionId} errorType={ErrorType}",
                                    sessionId.Value, ex.GetType().Name);
                            }
                        }
                    }

                    // Presence TOCTOU fix (fix #2): only MarkOffline when THIS stream was the
                    // active registration (UnregisterStreamAsync returned true). If the node has
                    // already reconnected and issued a new epoch, the online mark set by the new
                    // Hello must not be overwritten by this old stream's teardown.
                    if (wasActiveStream)
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await clusterClient.GetGrain<INodeGrain>(conn.NodeId.Value)
                                .MarkOfflineAsync()
                                .WaitAsync(cts.Token);
                        }
                        catch (Exception ex)
                        {
                            // Swallow — best-effort offline mark; the stale threshold will handle the rest.
                            logger.LogWarning("MarkOffline failed node={NodeId} errorType={ErrorType}",
                                conn.NodeId.Value, ex.GetType().Name);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// ARCH-HIGH-2: handle a peer-initiated CloseSession message. Validates the sender is
    /// actually a participant of the session, drops the routing entry, closes the SessionGrain,
    /// and forwards CloseSession to the peer so it can tear down its side.
    /// </summary>
    private async Task HandleCloseSessionAsync(
        CloseSession message,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.SessionId))
            return;

        var sessionId = new SessionId(message.SessionId);
        var route = await routingTable.GetRouteAsync(sessionId, cancellationToken);
        if (route is not { } participants)
            return;

        var (agent, publisher) = (participants.Agent, participants.Publisher);
        if (agent != conn.NodeId && publisher != conn.NodeId)
        {
            logger.LogWarning(
                "CloseSession from non-participant rejected session={SessionId} senderNode={NodeId}",
                message.SessionId, conn.NodeId.Value);
            return;
        }

        // Drop routing entry first so any in-flight ForwardFrameAsync returns false.
        routingTable.CloseSession(sessionId);

        // Increment 1 (Crux Finding 5): for an http_cloud session there is no publisher stream
        // to forward CloseSession to — release this consumer's upstream MCP session inside
        // HttpMcpProxyGrain instead of the pre-hardening draft's wasted CloseSession send to
        // NodeId.Empty (peer would always resolve to publisher = NodeId.Empty for these sessions
        // since agent == conn.NodeId is the only participant that can ever hold a stream).
        if (participants.IsHttpCloud)
        {
            await routingTable.CloseHttpCloudConsumerSessionAsync(participants.McpServerId, sessionId, cancellationToken);
        }
        else
        {
            // Forward CloseSession to the peer (best-effort).
            var peer = agent == conn.NodeId ? publisher : agent;
            try
            {
                await routingTable.SendToNodeAsync(peer, new GatewayToNodeMessage
                {
                    CloseSession = new CloseSession
                    {
                        SessionId = message.SessionId,
                        Reason = message.Reason
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Forward CloseSession to peer failed peer={NodeId} errorType={ErrorType}",
                    peer.Value, ex.GetType().Name);
            }
        }

        // Close the grain.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await clusterClient.GetGrain<ISessionGrain>(sessionId.Value)
                .CloseAsync(SessionCloseReason.Completed)
                .WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Close SessionGrain failed session={SessionId} errorType={ErrorType}",
                sessionId.Value, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Returns true if the Hello was accepted (per-node auth token verified, grain registered).
    /// Returns false after writing an AccessDenied response so the caller can break the stream
    /// before exposing any subsequent message handling to an unauthenticated peer.
    /// </summary>
    private async Task<bool> HandleHelloAsync(
        NodeHello hello,
        ConnectionContext conn,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hello.NodeId))
        {
            logger.LogWarning("Hello rejected — empty NodeId");
            await DenyAsync(responseStream, "Missing NodeId", cancellationToken: cancellationToken);
            return false;
        }

        // N-2 (defense-in-depth): reject NodeIds that are not well-formed 32-hex GUIDs.
        // The NATS relay subject is `korat.relay.frame.<encode(NodeId)>`, so the subject
        // key's entropy is otherwise delegated entirely to client goodwill.  The CLI mints
        // NodeId via NodeId.New() (Guid "N" format), so well-formed clients are unaffected.
        //
        // TOCTOU disposition: two concurrent brand-new registrations of the SAME NodeId by
        // different spaces could theoretically both pass the `existingBearerNode is null` check
        // below (SEC-CRITICAL-1) before either has persisted.  This race is closed by the DB
        // PRIMARY KEY constraint on NodeRecord.Id — a duplicate insert yields a PK violation
        // rather than a silent double-subscribe.  The 122-bit entropy of a GUID NodeId makes
        // accidental collision negligible and deliberate collision computationally infeasible;
        // this format validation removes the remaining avenue (low-entropy / chosen NodeId).
        if (!NodeId.IsWellFormed(hello.NodeId))
        {
            logger.LogWarning("Hello rejected — malformed NodeId nodeId={NodeId}", hello.NodeId);
            await DenyAsync(responseStream, "Malformed NodeId", cancellationToken: cancellationToken);
            return false;
        }

        // Sec M1: a Bearer header was present but the token is invalid/expired/revoked.
        // Fail closed — a just-revoked CLI token must be rejected outright.
        if (conn.BearerPresentButInvalid)
        {
            logger.LogWarning("Hello rejected — Bearer token present but invalid/revoked nodeId={NodeId}", hello.NodeId);
            await DenyAsync(responseStream, "Invalid node auth token", cancellationToken: cancellationToken);
            return false;
        }

        if (conn.BearerUserId is not { } bearerUserId)
        {
            // No valid Bearer token — nodes must authenticate via Bearer (CLI token).
            logger.LogWarning("Hello rejected — no Bearer token nodeId={NodeId}", hello.NodeId);
            await DenyAsync(responseStream, "Invalid node auth token", cancellationToken: cancellationToken);
            return false;
        }

        // Bearer path: the call was authenticated via CLI token at the stream level.
        // Resolve SpaceId via ISpaceResolver (authoritative server-side) instead of
        // trusting hello.SpaceId (which may be stale or absent in CLI-issued calls).
        var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(new UserId(bearerUserId), cancellationToken);
        if (spaceId is null)
        {
            // Broken provisioning invariant — the user has no default Space.
            // Log already emitted by SpaceResolver; return auth failure to the node.
            logger.LogWarning(
                "Hello rejected via Bearer — no default Space for userId={UserId} nodeId={NodeId}",
                bearerUserId, hello.NodeId);
            await DenyAsync(responseStream, "No default Space for user", cancellationToken: cancellationToken);
            return false;
        }

        // SEC-CRITICAL-1 (Bearer path): the Bearer token authenticates the USER, not the
        // NODE. A node already registered under a different user's Space must not be
        // re-homed by simply presenting a valid Bearer token. Check that any previously
        // persisted SpaceId matches the one resolved for this bearer user; reject mismatches
        // so a node owned by user B cannot be hijacked into user A's Space.
        // Brand-new NodeIds (not yet in the repository) pass through and register
        // to the bearer user's Space via ConnectAsync + RegisterNodeAsync below.
        var existingBearerNode = await repository.GetNodeAsync(new NodeId(hello.NodeId), cancellationToken);
        if (existingBearerNode is not null && existingBearerNode.SpaceId != spaceId.Value)
        {
            logger.LogWarning(
                "Hello rejected via Bearer — NodeId already registered to a different Space nodeId={NodeId} registeredSpace={RegisteredSpace} resolvedSpace={ResolvedSpace}",
                hello.NodeId, existingBearerNode.SpaceId.Value, spaceId.Value);
            await DenyAsync(responseStream, "SpaceId does not match node registration", cancellationToken: cancellationToken);
            return false;
        }

        conn.NodeId = new NodeId(hello.NodeId);
        conn.SpaceId = spaceId.Value;
        conn.HelloReceived = true;

        // G2: use the stable gateway id instead of a per-call random one.
        var gatewayId = StableGatewayId;

        // 017: parse node_kind from Hello. "agent" → Agent; anything else (including empty) → Publisher
        // (back-compat: pre-017 nodes send no node_kind and are treated as Publisher).
        var nodeKind = string.Equals(hello.NodeKind, "agent", StringComparison.OrdinalIgnoreCase)
            ? Korat.Domain.NodeKind.Agent
            : Korat.Domain.NodeKind.Publisher;

        // 022: generate the per-stream ConnectionId ONCE here and store on ConnectionContext so
        // all subsequent handling (stream registration, RequestSession, teardown) uses the same id.
        // Echo it back in GatewayHello.connection_id (replacing the prior throwaway New() call,
        // LOCKED #3, 022). NodeKind is also stored for the branching in the teardown finally-block.
        conn.ConnectionId = ConnectionId.New();
        conn.NodeKind = nodeKind;
        conn.DisplayName = hello.DisplayName;

        var nodeGrain = clusterClient.GetGrain<INodeGrain>(hello.NodeId);
        // 029: forward the advertised capabilities from NodeHello so INodeGrain._capabilities
        // is populated before InferenceDispatcher checks HasCapabilityAsync("inference").
        IReadOnlyList<string>? capabilities = hello.Capabilities.Count > 0
            ? hello.Capabilities.ToList()
            : null;
        // Node host metadata (additive, node-visibility-doctor design 2026-07-02): proto default
        // for an unset string field is "" (legacy CLI, or a hello re-sent without the fields) —
        // normalize to null so the grain/DB distinguish "no metadata" from an empty string.
        // B3-review (low): the four fields are client-controlled and refreshed on every hello —
        // truncate to the varchar(256) DB cap so a hostile/buggy CLI can't persist multi-megabyte
        // strings (bounded otherwise only by the gRPC max message size).
        var node = await nodeGrain.ConnectAsync(
            conn.SpaceId, hello.DisplayName, gatewayId, nodeKind, capabilities,
            hostname: NormalizeHostMetadata(hello.Hostname),
            os: NormalizeHostMetadata(hello.Os),
            arch: NormalizeHostMetadata(hello.Arch),
            cliVersion: NormalizeHostMetadata(hello.CliVersion));
        // Use conn.SpaceId (authoritative) rather than hello.SpaceId: for Bearer callers
        // hello.SpaceId may be empty, and conn.SpaceId was resolved server-side above.
        var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(conn.SpaceId.Value);
        await spaceGrain.RegisterNodeAsync(node);

        // CLI version negotiation: log a warning if the connecting CLI is below the minimum
        // supported version. Do NOT refuse — deprecation-window policy (additive/backward-compat).
        var minSupported = configuration["Korat:Cloud:MinSupportedCliVersion"];
        if (!string.IsNullOrEmpty(hello.CliVersion) && !string.IsNullOrEmpty(minSupported)
            && CompareSemVer(hello.CliVersion, minSupported) < 0)
        {
            logger.LogWarning(
                "Connecting CLI below min-supported: node={NodeId} cli={CliVersion} min={Min}",
                hello.NodeId, hello.CliVersion, minSupported);
        }

        await responseStream.WriteAsync(new GatewayToNodeMessage
        {
            Hello = new GatewayHello
            {
                GatewayId = gatewayId.Value,
                // 022: echo the SAME ConnectionId we generated above (LOCKED #3).
                // Pre-022 this was a throwaway ConnectionId.New().Value; now it is the
                // stable per-stream id used for agent routing.
                ConnectionId = conn.ConnectionId.Value,
                CurrentCliVersion = configuration["Korat:Cloud:CurrentCliVersion"] ?? "",
                MinSupportedCliVersion = configuration["Korat:Cloud:MinSupportedCliVersion"] ?? "",
                // fix/default-space-placeholder: echo the server-authoritative SpaceId so
                // the CLI can replace any placeholder (e.g. legacy "default") with its real Space.
                // Server is already the authority (conn.SpaceId was resolved via ISpaceResolver
                // from the Bearer token above); this makes it observable to the client.
                ResolvedSpaceId = conn.SpaceId.Value,
            }
        }, cancellationToken);
        return true;
    }

    private async Task HandleHeartbeatAsync(
        Heartbeat heartbeat,
        ConnectionContext conn,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        CancellationToken cancellationToken)
    {
        // ARCH-HIGH-1: ALWAYS use the Hello-bound NodeId on this stream so a malicious
        // node cannot heartbeat for someone else's identity. The wire field is ignored
        // for attribution; we still echo it back as a transport-layer convenience.
        var attributedNodeId = conn.NodeId.Value;
        if (!string.IsNullOrEmpty(heartbeat.NodeId) && heartbeat.NodeId != attributedNodeId)
        {
            logger.LogWarning(
                "Heartbeat NodeId mismatch — wire={WireNodeId} stream={StreamNodeId}",
                heartbeat.NodeId, attributedNodeId);
        }

        // G2: use stable gateway id for heartbeat attribution as well.
        var nodeGrain = clusterClient.GetGrain<INodeGrain>(attributedNodeId);
        await nodeGrain.HeartbeatAsync(StableGatewayId);
        await responseStream.WriteAsync(new GatewayToNodeMessage
        {
            HeartbeatAck = new HeartbeatAck
            {
                NodeId = attributedNodeId,
                ReceivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 030 (push-to-wake): handle a RegisterPushToken message from a mobile node.
    /// Attribution mirrors Heartbeat: the Hello-bound stream NodeId is authoritative;
    /// the advisory wire field node_id is validated but not used for authz.
    /// The token is an idempotent upsert — the node may send it multiple times
    /// (on connect, on OS rotation) and old nodes never send it (safe default absent).
    /// An empty token/platform pair clears the stored token (called by C4 on APNs 410).
    /// </summary>
    private async Task HandleRegisterPushTokenAsync(
        RegisterPushToken message,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        // ARCH-HIGH-1 pattern: ALWAYS use the Hello-bound NodeId for attribution;
        // ignore whatever the wire node_id claims. Log a mismatch for diagnostics.
        var attributedNodeId = conn.NodeId.Value;
        if (!string.IsNullOrEmpty(message.NodeId) && message.NodeId != attributedNodeId)
        {
            logger.LogWarning(
                "RegisterPushToken NodeId mismatch — wire={WireNodeId} stream={StreamNodeId}",
                message.NodeId, attributedNodeId);
        }

        // Log a short token prefix (never the full token — per-device identifier, §4 design).
        var tokenPrefix = message.Token.Length >= 8 ? message.Token[..8] : message.Token;
        var isClearing = string.IsNullOrEmpty(message.Token);
        if (isClearing)
            logger.LogInformation(
                "RegisterPushToken clearing token node={NodeId}",
                attributedNodeId);
        else
            logger.LogInformation(
                "RegisterPushToken node={NodeId} platform={Platform} tokenPrefix={TokenPrefix}...",
                attributedNodeId, message.Platform, tokenPrefix);

        try
        {
            var nodeGrain = clusterClient.GetGrain<INodeGrain>(attributedNodeId);
            await nodeGrain.RegisterPushTokenAsync(message.Token, message.Platform);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a failed token registration leaves the node foreground-only
            // (same as if no token was ever sent). Log but do not terminate the stream.
            logger.LogWarning(
                "RegisterPushToken grain call failed node={NodeId} errorType={ErrorType}",
                attributedNodeId, ex.GetType().Name);
        }
    }

    private async Task HandlePublishAsync(
        PublishMcpServer publish,
        ConnectionContext conn,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        CancellationToken cancellationToken)
    {
        // G8: reject publish when the node is not in the repository (not just fall back to "default").
        var node = await repository.GetNodeAsync(conn.NodeId, cancellationToken);
        if (node is null)
        {
            logger.LogWarning("Publish rejected — node not found nodeId={NodeId}", conn.NodeId.Value);
            await DenyAsync(responseStream, KoratError.Message(KoratErrorCode.NotFound), publish.RequestId, cancellationToken);
            return;
        }

        var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(node.SpaceId.Value);
        try
        {
            var publishOutcome = await spaceGrain.PublishMcpServerWithOutcomeAsync(
                conn.NodeId,
                publish.DisplayName,
                publish.Command,
                string.Join(' ', publish.Args));
            var server = publishOutcome.Server;
            if (publishOutcome.Redefinition is not null)
                await HandleRedefinitionAsync(publishOutcome.Redefinition, cancellationToken);

            if (server is null) // Tombstoned (node,name): refuse re-publish — see delete-tombstone design.
            {
                logger.LogWarning(
                    "Publish refused by delete-tombstone name={DisplayName} node={NodeId}",
                    publish.DisplayName, conn.NodeId.Value);
                await DenyAsync(responseStream, KoratError.Message(KoratErrorCode.NotFound), publish.RequestId, cancellationToken);
                return;
            }

            logger.LogInformation(
                "Published MCP server {ServerId} name={DisplayName} node={NodeId}",
                server.Id,
                server.DisplayName,
                conn.NodeId.Value);

            await responseStream.WriteAsync(new GatewayToNodeMessage
            {
                PublishMcpServerAck = new PublishMcpServerAck
                {
                    RequestId = publish.RequestId,
                    McpServerId = server.Id.Value,
                    DisplayName = server.DisplayName
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // G6: unwrap any KoratDomainException from Orleans wrapper chain.
            var domain = GrainExceptionUnwrap.Find(ex);
            if (domain is not null)
            {
                if (domain.Code == KoratErrorCode.DuplicateServerName)
                    logger.LogWarning("Duplicate MCP server name {DisplayName}", publish.DisplayName);
                else
                    logger.LogWarning("Publish failed name={DisplayName} errorCode={Code}", publish.DisplayName, domain.Code);

                await DenyAsync(responseStream, KoratError.Message(domain.Code), publish.RequestId, cancellationToken);
                return;
            }

            // Non-domain exception — log error class only (FR-016), rethrow to let G3 handle it.
            logger.LogError("Publish grain call failed errorType={ErrorType}", ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// 021 (Layer 1): handle SyncMcpServers — the node's declarative server set reconcile.
    /// Resolves the node (same admission as HandlePublishAsync), maps each ServerDesc →
    /// McpServerSpec, calls SpaceGrain.SyncMcpServersAsync (upsert + soft-retire), and writes
    /// back one PublishMcpServerAck per server so the daemon can rebuild its routing map.
    /// RequestId is empty for sync acks — the daemon matches by DisplayName.
    /// </summary>
    /// <summary>
    /// Р26/Р27: finish what SpaceGrain started when a re-publish changed an approved server's
    /// launch definition.
    ///
    /// <para>The grain suspended the permissions (it owns that state) and handed back the live
    /// sessions plus the before/after pair. Two things remain, and both live here because grains
    /// cannot reach them: terminate those sessions, and write the audit record.</para>
    ///
    /// <para>The audit record carries the OLD and NEW command explicitly. Р27 is specific about
    /// this: a notification that says only "the definition changed" invites a reflexive approve,
    /// which is how Р26's protection gets bypassed through the human rather than through the code.
    /// The owner has to be able to see that <c>npx @modelcontextprotocol/server-filesystem ~/docs</c>
    /// became <c>bash -c …</c>.</para>
    ///
    /// <para>Best-effort by design: a publish that succeeded must not be rolled back because the
    /// audit sink or a session teardown failed. The suspension itself is already durable — it
    /// happened inside the grain, before this runs — so the security property holds even if
    /// everything here throws.</para>
    /// </summary>
    private async Task HandleRedefinitionAsync(
        Korat.Domain.Entities.McpServerRedefinition redefinition, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "MCP server redefined under an approved name — {Count} permission(s) suspended "
            + "serverId={ServerId} displayName={DisplayName} sessionsTerminated={Sessions}",
            redefinition.SuspendedGrantIds.Count,
            redefinition.ServerId.Value,
            redefinition.DisplayName,
            redefinition.SessionsToTerminate.Count);

        foreach (var sessionId in redefinition.SessionsToTerminate)
        {
            try
            {
                await sessionTerminator.TerminateSessionAsync(
                    sessionId, SessionCloseReason.Revoked, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Failed to terminate session after redefinition sessionId={SessionId} errorType={ErrorType}",
                    sessionId.Value, ex.GetType().Name);
            }
        }

        try
        {
            await auditLog.RecordAsync(new Korat.Cloud.Security.Audit.AuditEvent(
                Action: Korat.Cloud.Security.Audit.AuditActions.McpServerRedefine,
                TargetType: "mcp_server",
                TargetId: redefinition.ServerId.Value,
                DetailsJson: Korat.Cloud.Security.Audit.AuditDetails.Json(new
                {
                    displayName = redefinition.DisplayName,
                    previousCommand = redefinition.PreviousCommand,
                    previousArguments = redefinition.PreviousArguments,
                    newCommand = redefinition.NewCommand,
                    newArguments = redefinition.NewArguments,
                    previousDigest = redefinition.PreviousDigest,
                    newDigest = redefinition.NewDigest,
                    suspendedGrants = redefinition.SuspendedGrantIds.Count,
                    terminatedSessions = redefinition.SessionsToTerminate.Count,
                })),
                required: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Failed to audit MCP server redefinition serverId={ServerId} errorType={ErrorType}",
                redefinition.ServerId.Value, ex.GetType().Name);
        }
    }

    private async Task HandleSyncMcpServersAsync(
        SyncMcpServers sync,
        ConnectionContext conn,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        CancellationToken cancellationToken)
    {
        var node = await repository.GetNodeAsync(conn.NodeId, cancellationToken);
        if (node is null)
        {
            logger.LogWarning("SyncMcpServers rejected — node not found nodeId={NodeId}", conn.NodeId.Value);
            await DenyAsync(responseStream, KoratError.Message(KoratErrorCode.NotFound), cancellationToken: cancellationToken);
            return;
        }

        // Map each ServerDesc → McpServerSpec. Args are joined the same way as HandlePublishAsync
        // so the idempotency key (displayName + command + args) is consistent across both paths.
        var specs = sync.Servers
            .Select(s => new McpServerSpec(s.DisplayName, s.Command, string.Join(' ', s.Args)))
            .ToList();

        var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(node.SpaceId.Value);
        try
        {
            var syncOutcome = await spaceGrain.SyncMcpServersWithOutcomeAsync(conn.NodeId, specs);
            var servers = syncOutcome.Servers;
            foreach (var redefinition in syncOutcome.Redefinitions)
                await HandleRedefinitionAsync(redefinition, cancellationToken);

            logger.LogInformation(
                "SyncMcpServers: upserted {Count} servers node={NodeId}",
                servers.Count,
                conn.NodeId.Value);

            // Reply with one PublishMcpServerAck per server so the daemon rebuilds its routing map.
            // RequestId is empty — the daemon matches sync acks by DisplayName, not by RequestId.
            foreach (var server in servers)
            {
                await responseStream.WriteAsync(new GatewayToNodeMessage
                {
                    PublishMcpServerAck = new PublishMcpServerAck
                    {
                        RequestId = string.Empty,
                        McpServerId = server.Id.Value,
                        DisplayName = server.DisplayName
                    }
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            var domain = GrainExceptionUnwrap.Find(ex);
            if (domain is not null)
            {
                logger.LogWarning("SyncMcpServers failed node={NodeId} errorCode={Code}", conn.NodeId.Value, domain.Code);
                await DenyAsync(responseStream, KoratError.Message(domain.Code), cancellationToken: cancellationToken);
                return;
            }

            logger.LogError("SyncMcpServers grain call failed errorType={ErrorType}", ex.GetType().Name);
            throw;
        }
    }

    private async Task HandleUnpublishAsync(
        UnpublishMcpServer unpublish,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        // ARCH-SAFE: resolve the space via the node record so a malicious node cannot
        // unpublish servers belonging to a different space.
        var node = await repository.GetNodeAsync(conn.NodeId, cancellationToken);
        if (node is null)
        {
            logger.LogWarning("Unpublish rejected — node not found nodeId={NodeId}", conn.NodeId.Value);
            return;
        }

        var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(node.SpaceId.Value);
        await spaceGrain.UnpublishMcpServerAsync(conn.NodeId, new McpServerId(unpublish.McpServerId));

        logger.LogInformation(
            "Unpublished MCP server {McpServerId} node={NodeId}",
            unpublish.McpServerId,
            conn.NodeId.Value);
    }

    /// <summary>
    /// Thin adapter over <see cref="ISessionAdmission"/> (Space-MCP increment 1, Task 2,
    /// BLOCKER-3): builds a <see cref="ConsumerPrincipal"/> from this gRPC stream's
    /// Hello-authenticated <see cref="ConnectionContext"/> (always <see cref="ConsumerBindPolicy.NodeTofu"/>
    /// — a real bridge stream backs this call), delegates the entire admission gauntlet to
    /// <see cref="SessionAdmission.AdmitAsync"/>, and translates the result back into the exact
    /// same AccessDenied/AccessPending/SessionOpened stream writes this method used to construct
    /// directly. Every check that used to live here now lives in <c>SessionAdmission</c> —
    /// see tests/Korat.Auth.Tests/SpaceMcp/SessionAdmissionCharacterizationTests.cs for the proof
    /// that the NodeTofu path is unchanged, and <c>ConnectAccessRequestTests</c> for the
    /// end-to-end gRPC regression gate.
    /// </summary>
    private async Task HandleRequestSessionAsync(
        RequestSession request,
        ConnectionContext conn,
        IServerStreamWriter<GatewayToNodeMessage> responseStream,
        CancellationToken cancellationToken)
    {
        var mcpServerId = new McpServerId(request.McpServerId);
        var principal = new ConsumerPrincipal(
            new ConsumerId(request.AgentClientId),
            conn.SpaceId,
            conn.ConnectionId,
            conn.NodeId,
            conn.AgentId,
            ConsumerBindPolicy.NodeTofu,
            conn.DisplayName);

        var result = await admission.AdmitAsync(mcpServerId, principal, cancellationToken);

        switch (result)
        {
            case AdmissionResult.Denied denied:
                await DenyAsync(responseStream, denied.Reason, request.RequestId, cancellationToken);
                return;

            case AdmissionResult.Pending pending:
                await responseStream.WriteAsync(new GatewayToNodeMessage
                {
                    AccessPending = new AccessPending
                    {
                        RequestId = request.RequestId,
                        AccessRequestId = pending.AccessRequestId.Value
                    }
                }, cancellationToken);
                return;

            case AdmissionResult.Opened opened:
                await responseStream.WriteAsync(new GatewayToNodeMessage
                {
                    SessionOpened = new SessionOpened
                    {
                        RequestId = request.RequestId,
                        SessionId = opened.SessionId.Value,
                        HomeGatewayId = opened.HomeGatewayId.Value,
                        PayloadLimits = new PayloadLimitPolicy
                        {
                            PerMessageLimitBytes = Domain.Entities.PayloadLimitPolicy.DefaultPerMessageBytes,
                            SessionWarningBytes = Domain.Entities.PayloadLimitPolicy.DefaultSessionWarningBytes,
                            SessionHardLimitBytes = Domain.Entities.PayloadLimitPolicy.DefaultSessionHardLimitBytes
                        },
                        // 031: advisory — tells the agent whether to expect an e2e-v1 handshake.
                        // Not trusted for security decisions; the handshake outcome is authoritative.
                        PeerSupportsE2E = opened.PeerSupportsE2e
                    }
                }, cancellationToken);
                return;

            default:
                throw new InvalidOperationException($"Unhandled AdmissionResult type {result.GetType().Name}");
        }
    }

    // ── 029: Inference Point registration ─────────────────────────────────────────────────────────

    // ── 031: E2E key exchange routing ────────────────────────────────────────────────────────────

    /// <summary>
    /// 031: Agent → cloud → publisher. Validate sender ∈ session participants, stamp the
    /// mcp_server_id (publisher uses it to route the handshake to the right SessionContext),
    /// then forward to the publisher node. If the publisher does NOT support e2e-v1, send
    /// E2eNotSupported back to the agent so it can surface a downgrade warning.
    /// </summary>
    private async Task HandleE2eKeyOfferAsync(
        E2eKeyOffer offer,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        var sessionId = new SessionId(offer.SessionId);
        var route = await routingTable.GetRouteAsync(sessionId, cancellationToken);
        if (route is not { } r)
        {
            logger.LogDebug("E2eKeyOffer for unknown session={SessionId}", offer.SessionId);
            return;
        }

        // Validate sender is the agent participant of this session.
        if (r.Agent != conn.NodeId)
        {
            logger.LogWarning(
                "E2eKeyOffer from non-agent rejected session={SessionId} senderNode={NodeId}",
                offer.SessionId, conn.NodeId.Value);
            return;
        }

        // Finding 16, S8: an http_cloud session has no publisher NodeGrain to check a capability
        // against (r.Publisher is NodeId.Empty by design, and the cloud IS the terminus for
        // these sessions — e2e-v1 is structurally inapplicable, not merely unsupported, per the
        // spec's Trust model note). Short-circuit straight to E2eNotSupported instead of
        // activating a junk empty-key NodeGrain for an answer that could only ever be false.
        if (r.IsHttpCloud)
        {
            logger.LogDebug(
                "E2eKeyOffer: http_cloud session is cloud-terminated, e2e-v1 not applicable session={SessionId}",
                offer.SessionId);
            await routingTable.SendToConnectionAsync(
                r.AgentConnectionId,
                new GatewayToNodeMessage
                {
                    E2ENotSupported = new E2eNotSupported
                    {
                        SessionId = offer.SessionId,
                        Reason = "http_cloud sessions are cloud-terminated; e2e-v1 is not applicable"
                    }
                },
                cancellationToken);
            return;
        }

        // Check publisher capability BEFORE forwarding.
        var publisherSupportsE2e = false;
        try
        {
            publisherSupportsE2e = await clusterClient.GetGrain<INodeGrain>(r.Publisher.Value)
                .HasCapabilityAsync("e2e-v1");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("E2eKeyOffer: publisher capability check failed session={SessionId} errorType={ErrorType}",
                offer.SessionId, ex.GetType().Name);
        }
        if (!publisherSupportsE2e)
        {
            // Publisher does not support e2e-v1: send E2eNotSupported back to the agent.
            // The agent will fall back to plaintext (emit downgrade warning) or close the session
            // depending on its --e2e policy.
            logger.LogDebug(
                "E2eKeyOffer: publisher lacks e2e-v1, sending E2eNotSupported to agent session={SessionId}",
                offer.SessionId);
            await routingTable.SendToConnectionAsync(
                r.AgentConnectionId,
                new GatewayToNodeMessage
                {
                    E2ENotSupported = new E2eNotSupported
                    {
                        SessionId = offer.SessionId,
                        Reason = "publisher does not support e2e-v1"
                    }
                },
                cancellationToken);
            return;
        }

        // Stamp the mcp_server_id and agent_client_id so the publisher's SessionBridge can route
        // this to the right session and include the same agentClientId in the transcript hash.
        var forwardOffer = offer.Clone();
        forwardOffer.McpServerId = r.McpServerId.Value;

        // cloud-m1 fix: clear any agent-supplied ConsumerId BEFORE the grain lookup so the
        // failure path forwards an empty string, not the agent's wire value. The agent must not
        // be able to influence the ConsumerId that reaches the publisher's transcript hash by
        // supplying a forged value in the E2eKeyOffer message.
        forwardOffer.AgentClientId = "";

        // Resolve agentClientId from the session grain (needed for transcript hash on publisher side).
        // Best-effort — if the grain is unavailable we leave agent_client_id empty; both sides
        // will use empty string and compute the same (though weaker) transcript hash.
        try
        {
            var session = await clusterClient.GetGrain<ISessionGrain>(offer.SessionId).GetAsync();
            forwardOffer.AgentClientId = session.ConsumerId.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("E2eKeyOffer: failed to resolve agentClientId session={SessionId} errorType={ErrorType}",
                offer.SessionId, ex.GetType().Name);
        }

        var delivered = await routingTable.SendToNodeAsync(
            r.Publisher,
            new GatewayToNodeMessage { E2EKeyOffer = forwardOffer },
            cancellationToken);

        if (!delivered)
            logger.LogDebug("E2eKeyOffer: publisher unreachable session={SessionId}", offer.SessionId);
    }

    /// <summary>
    /// 031: Publisher → cloud → agent. Validate sender ∈ session participants, forward
    /// the answer (with publisher's ephemeral pub key + confirm tag) to the agent.
    /// </summary>
    private async Task HandleE2eKeyAnswerAsync(
        E2eKeyAnswer answer,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        var sessionId = new SessionId(answer.SessionId);
        var route = await routingTable.GetRouteAsync(sessionId, cancellationToken);
        if (route is not { } r)
        {
            logger.LogDebug("E2eKeyAnswer for unknown session={SessionId}", answer.SessionId);
            return;
        }

        // Validate sender is the publisher participant of this session.
        if (r.Publisher != conn.NodeId)
        {
            logger.LogWarning(
                "E2eKeyAnswer from non-publisher rejected session={SessionId} senderNode={NodeId}",
                answer.SessionId, conn.NodeId.Value);
            return;
        }

        // MAJOR-3 fix: stamp publisher_node_id so both the direct-connect agent and the
        // aggregator derive the same transcript hash without out-of-band identity knowledge.
        var forwardAnswer = answer.Clone();
        forwardAnswer.PublisherNodeId = r.Publisher.Value;

        var delivered = await routingTable.SendToConnectionAsync(
            r.AgentConnectionId,
            new GatewayToNodeMessage { E2EKeyAnswer = forwardAnswer },
            cancellationToken);

        if (!delivered)
            logger.LogDebug("E2eKeyAnswer: agent unreachable session={SessionId}", answer.SessionId);
    }

    /// <summary>
    /// 031: Agent → cloud → publisher. Close the handshake by forwarding the agent's confirm tag.
    /// Validate sender is the agent participant.
    /// </summary>
    private async Task HandleE2eKeyConfirmAsync(
        E2eKeyConfirm confirm,
        ConnectionContext conn,
        CancellationToken cancellationToken)
    {
        var sessionId = new SessionId(confirm.SessionId);
        var route = await routingTable.GetRouteAsync(sessionId, cancellationToken);
        if (route is not { } r)
        {
            logger.LogDebug("E2eKeyConfirm for unknown session={SessionId}", confirm.SessionId);
            return;
        }

        // Validate sender is the agent participant.
        if (r.Agent != conn.NodeId)
        {
            logger.LogWarning(
                "E2eKeyConfirm from non-agent rejected session={SessionId} senderNode={NodeId}",
                confirm.SessionId, conn.NodeId.Value);
            return;
        }

        var delivered = await routingTable.SendToNodeAsync(
            r.Publisher,
            new GatewayToNodeMessage { E2EKeyConfirm = confirm },
            cancellationToken);

        if (!delivered)
            logger.LogDebug("E2eKeyConfirm: publisher unreachable session={SessionId}", confirm.SessionId);
    }

    /// <summary>
    /// Returns true for exceptions that represent a normal (ungraceful) client disconnect:
    /// network blip, node restart, deploy rolling-restart, etc.
    /// These should be logged at Information, not Error, to avoid Sentry/GlitchTip noise.
    /// Internal so it can be unit-tested directly.
    /// </summary>
    internal static bool IsBenignDisconnect(Exception ex) =>
        ex is IOException
        || ex is OperationCanceledException
        || (ex is RpcException rpc && rpc.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable)
        || ex.GetType().Name is "ConnectionResetException" or "ConnectionAbortedException"
        || (ex.InnerException is not null && IsBenignDisconnect(ex.InnerException));

    /// <summary>
    /// Node host metadata normalization (B3-review, low): "" (proto3 default for an unset
    /// field, i.e. legacy CLI) → null; anything longer than the varchar(256) DB cap is
    /// truncated (not rejected — a buggy CLI still connects, it just can't persist garbage).
    /// </summary>
    internal const int MaxHostMetadataLength = 256;

    private static string? NormalizeHostMetadata(string value) =>
        string.IsNullOrEmpty(value)
            ? null
            : value.Length <= MaxHostMetadataLength ? value : value[..MaxHostMetadataLength];

    /// <summary>
    /// Minimal SemVer comparison for CLI version negotiation. Compares MAJOR.MINOR.PATCH
    /// numerically; a pre-release suffix (has '-') sorts before its release. Build metadata
    /// ('+...') and a leading 'v' are ignored. Unparseable inputs compare as equal (no action).
    /// Local copy — cloud must not take a dependency on the CLI assembly.
    /// </summary>
    private static int CompareSemVer(string a, string b)
    {
        if (!TryParseSemVer(a, out var va, out var preA) || !TryParseSemVer(b, out var vb, out var preB))
            return 0;
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        if (preA == preB) return 0;
        return preA ? -1 : 1;
    }

    private static bool TryParseSemVer(string s, out Version core, out bool isPre)
    {
        core = new Version(0, 0, 0); isPre = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = s.IndexOf('-');
        if (dash >= 0) { isPre = true; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min) || !int.TryParse(parts[2], out var pat))
            return false;
        core = new Version(maj, min, pat);
        return true;
    }
}
