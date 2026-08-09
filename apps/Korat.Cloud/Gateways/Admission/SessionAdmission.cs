using Korat.Cloud.Gateways;
using Korat.Cloud.Push;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;

namespace Korat.Cloud.Gateways.Admission;

/// <summary>
/// Extraction of <see cref="NodeGatewayService.HandleRequestSessionAsync"/>'s body (Space-MCP
/// increment 1, Task 2, BLOCKER-3). Every check below is copied verbatim from that method with
/// only the mechanical <c>conn.X</c> → <c>principal.X</c> / <c>request.X</c> → parameter
/// substitutions the plan specifies — same order, same early-returns, same comments (updated to
/// reference the new parameter names where the prose talks about them).
///
/// The two NEW behaviours this increment adds are both confined to the TOFU-bind block and the
/// final E2E-capability check, and are gated on <see cref="ConsumerPrincipal.BindPolicy"/>:
///   1. A <see cref="ConsumerBindPolicy.NodeTofu"/> caller may never present a <c>cagg_</c>-
///      prefixed ConsumerId (reserved for the aggregator) — rejected before any grain lookup.
///   2. A <see cref="ConsumerBindPolicy.ServerMinted"/> caller binds against
///      <see cref="AggregatorSentinelNodeId"/> instead of its own NodeId, never stamps the
///      hosted-agent attribution link, and never queries/trusts publisher E2E capability
///      (forced <c>PeerSupportsE2e=false</c> — SF-8, the cloud is always the plaintext terminus).
/// </summary>
public sealed class SessionAdmission(
    IClusterClient clusterClient,
    IMetadataRepository repository,
    SessionRoutingTable routingTable,
    NodeWakeCoordinator wakeCoordinator,
    AccessRequestNotifier notifier,
    IConfiguration configuration,
    ILogger<SessionAdmission> logger) : ISessionAdmission
{
    /// <summary>
    /// Space-MCP aggregator (increment 1): the sentinel NodeId every <see cref="ConsumerBindPolicy.ServerMinted"/>
    /// consumer identity is bound to instead of a real bridge NodeId. There is no gRPC stream
    /// behind an aggregator-opened backend session — only the in-process delivery leg (Task 3) —
    /// so a stable, reserved sentinel stands in for "the node this identity is bound to".
    ///
    /// MUST-FIX 2 (adversarial review): the literal value now lives in
    /// <see cref="Korat.Domain.WellKnownNodeIds.AggregatorSentinelNodeId"/> — the single source of
    /// truth <c>Korat.Persistence</c>'s <c>EfMetadataRepository.ListReapableSessionsAsync</c> also
    /// references (it cannot see this Korat.Cloud type). Kept as a <c>NodeId</c>-typed field here
    /// (unchanged name/type) so every existing caller in this assembly is unaffected.
    /// </summary>
    public static readonly NodeId AggregatorSentinelNodeId = new(Korat.Domain.WellKnownNodeIds.AggregatorSentinelNodeId);
    private static readonly TimeSpan ServerMintedWakeWait = TimeSpan.FromSeconds(30);

    // G2: stable gateway id for this silo instance derived from configuration or machine name.
    // Mirrors NodeGatewayService.StableGatewayId exactly (same config key, same fallback) so
    // session-home assignment is byte-for-byte unaffected by the extraction.
    private GatewayId StableGatewayId =>
        new(configuration["Korat:Cloud:GatewayId"] ?? Environment.MachineName);

    public async Task<AdmissionResult> AdmitAsync(McpServerId serverId, ConsumerPrincipal principal, CancellationToken cancellationToken)
    {
        var server = await repository.GetMcpServerAsync(serverId, cancellationToken);
        if (server is null)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.NotFound));
        }

        // F45: cross-space isolation. server is loaded by GUID only (no space scoping), so a
        // consumer whose own space (principal.ConsumerSpaceId, authoritatively resolved server-side)
        // differs from the server's space must NOT be able to open a session OR inject a pending
        // access-request into another tenant's approval queue (confused-deputy / spam). Reject as
        // NotFound — mirroring the cross-space-as-404 convention used by the REST endpoints and the
        // SpaceGrain cross-space guards — so the server GUID is not confirmed to an outsider. This
        // runs BEFORE the agent-client TOFU bind and the grant/access-request branch, so no state
        // is created in the foreign space. There is no legitimate cross-space session flow.
        if (principal.ConsumerSpaceId != server.SpaceId)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.NotFound));
        }

        // Server state is checked only after the cross-space guard above. A caller from another
        // Space must receive the same NotFound response for active, disabled, and reauth-required
        // servers; otherwise the server id becomes a state-probing side channel.
        if (server.Status == McpServerStatus.Disabled)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.ServerDisabled));
        }

        // Increment 2 (HTTP MCP OAuth): a server awaiting owner re-authorization must not open
        // new sessions. Denied at SESSION-OPEN ONLY, not at access-request time — a pending-
        // consent server may legitimately accumulate access requests while the owner finishes
        // consenting (mirrors the 021 availability philosophy: admission ≠ catalog visibility).
        if (server.Status == McpServerStatus.NeedsReauth)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.ServerNeedsReauth));
        }

        // G9: use the targeted repository query instead of listing all grants and scanning.
        // GetActiveGrantAsync is already on IMetadataRepository (see SpaceGrain usage).
        var agentClientId = principal.ConsumerId;

        // Space-MCP (increment 1, plan correction S6/Task 2): the cagg_ namespace is reserved for
        // ServerMinted (aggregator-minted) identities only. A NodeTofu (gRPC) caller presenting a
        // cagg_-prefixed ConsumerId is rejected outright, BEFORE any grain lookup — otherwise a
        // malicious node could present the aggregator's own reserved identity and either hijack its
        // AggregatorSentinelNodeId bind or read/consume grants that belong to the aggregator.
        if (principal.BindPolicy == ConsumerBindPolicy.NodeTofu
            && agentClientId.Value.StartsWith("cagg_", StringComparison.Ordinal))
        {
            return new AdmissionResult.Denied("reserved_agent_client_namespace");
        }

        // Space-MCP (Task 2, fable should-fix SF2): the MIRROR of the guard above — a
        // ServerMinted (aggregator-minted) caller presenting a NON-cagg_ ConsumerId is denied
        // here too, BEFORE any grain lookup/bind. Without this, a ServerMinted admission could bind
        // an arbitrary CLI-namespace identity to AggregatorSentinelNodeId, and because
        // ConsumerGrain.RegisterAsync overwrites the whole state unconditionally, a race with a
        // NodeTofu caller's first-use TOFU bind could hijack the real node's agent-client identity.
        // NodeTofu may never present cagg_; ServerMinted may never present anything BUT cagg_.
        if (principal.BindPolicy == ConsumerBindPolicy.ServerMinted
            && !agentClientId.Value.StartsWith("cagg_", StringComparison.Ordinal))
        {
            return new AdmissionResult.Denied("reserved_agent_client_namespace");
        }

        // 023 / ARCH-CRITICAL-2: the agent-client this request acts for must be bound to the node
        // that owns it, so a malicious node A cannot spoof an agent-client owned by node B.
        // ConsumerGrain is durable (rehydrates from Postgres on activate, persists on Register),
        // so a bound NodeId survives silo restart. The production connect path never pre-registers
        // the agent-client (the CLI generates ConsumerId locally and only sends it here), so we
        // bind it TRUST-ON-FIRST-USE: the ConsumerId is a 122-bit unguessable value, so the first
        // authenticated caller to present it is taken to be its legitimate owner.
        //
        // What TOFU here does and does not buy (docs/security/threat-model.md, "Not protected" §1):
        // it stops node A from naming a ConsumerId owned by node B — that is the ARCH-CRITICAL-2
        // guarantee above, and it holds. It does NOT make the ConsumerId a secret between agents
        // on one machine: the CLI persists it in ~/.korat/config.json with owner-only permissions,
        // so every process running as that OS user can read it and present it. "Unguessable" is
        // therefore the accurate word; "unshared" would not be — a previous revision of this
        // comment said unshared, and that claim is what the threat model now contradicts.
        //   - bound & matches the target node → proceed (happy path)
        //   - bound & different node           → AccessDenied (spoof/hijack blocked)
        //   - unbound (never seen)             → bind it to the target node now (durable), then proceed
        // Bind runs BEFORE the grant check so a first, still-ungranted RequestSession also binds
        // (it then returns Pending below) — closing the TOFU window on the very first session.
        //
        // Space-MCP (ServerMinted fork): the "target node" is AggregatorSentinelNodeId instead of
        // principal.RequestingNodeId — same GetAsync-then-Register shape, never a blind overwrite
        // (ConsumerGrain.RegisterAsync replaces the whole state unconditionally), so a
        // ServerMinted identity already bound to a DIFFERENT, non-sentinel node (a hijack attempt,
        // or a stale bind from before this identity existed) is rejected rather than silently
        // re-pointed.
        var boundTargetNodeId = principal.BindPolicy == ConsumerBindPolicy.ServerMinted
            ? AggregatorSentinelNodeId
            : principal.RequestingNodeId;
        try
        {
            var agentClientState = await clusterClient.GetGrain<IConsumerGrain>(agentClientId.Value).GetAsync();
            var hasRegisteredNodeId = !string.IsNullOrEmpty(agentClientState.NodeId.Value);
            if (hasRegisteredNodeId && agentClientState.NodeId != boundTargetNodeId)
            {
                logger.LogWarning(
                    "RequestSession rejected — agent-client NodeId mismatch agentClientId={ConsumerId} expectedNode={ExpectedNode} senderNode={NodeId}",
                    agentClientId.Value, agentClientState.NodeId.Value, boundTargetNodeId.Value);
                return new AdmissionResult.Denied("agent_client_node_mismatch");
            }
            if (!hasRegisteredNodeId)
            {
                // First use — bind the agent-client to the target node (TOFU). Use
                // principal.ConsumerSpaceId (the consumer's own space), NOT server.SpaceId.
                // DisplayName is informational only. Prefer the caller's bounded friendly name;
                // server-minted consumers use a stable generic label rather than exposing cagg_ ids.
                await clusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
                    .RegisterAsync(
                        principal.ConsumerSpaceId,
                        boundTargetNodeId,
                        ConsumerDisplayName(principal));
                // Audit: a security-state transition (identity permanently pinned to a node).
                logger.LogInformation(
                    "Agent-client bound (first use) agentClientId={ConsumerId} nodeId={NodeId} spaceId={SpaceId}",
                    agentClientId.Value, boundTargetNodeId.Value, principal.ConsumerSpaceId.Value);
            }
            else if (DisplayNameRules.IsValid(principal.DisplayName ?? string.Empty, allowControlChars: false)
                && !string.Equals(agentClientState.DisplayName, principal.DisplayName, StringComparison.Ordinal))
            {
                // The security bind was already verified above. Refresh only informational
                // metadata so existing identities acquire the CLI's real consumer name.
                await clusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
                    .RegisterAsync(principal.ConsumerSpaceId, boundTargetNodeId, principal.DisplayName!);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Grain lookup failed — log and reject to fail closed.
            logger.LogError("Agent-client grain lookup failed agentClientId={ConsumerId} errorType={ErrorType}",
                agentClientId.Value, ex.GetType().Name);
            return new AdmissionResult.Denied("agent_client_lookup_failed");
        }

        var activeGrant = await repository.GetActiveGrantAsync(server.SpaceId, agentClientId, serverId, cancellationToken);

        // Р26: an active grant is not enough — it must be a grant for THIS definition of the
        // server. SpaceGrain already suspends permissions at the moment a re-publish changes the
        // launch definition, so in the normal flow this never fires. It is here because that is
        // not the only way a definition can move:
        //   • PATCH /api/mcp-servers/{id} changes RemoteUrl/AuthMode for HTTP servers;
        //   • a grant approved before Р26 carries no digest at all;
        //   • any future write path someone adds without remembering to suspend.
        // Treating "no digest" as a mismatch is deliberate. The alternative — pass when unknown —
        // would make every pre-Р26 grant permanently exempt from the check we just added, which is
        // the failure mode this whole decision exists to remove. The cost is one re-approval per
        // existing permission, once.
        if (activeGrant is not null)
        {
            var currentDigest = McpServerDefinition.Digest(server);
            if (!string.Equals(activeGrant.ApprovedDefinitionDigest, currentDigest, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Grant does not match the server's current definition — treating as ungranted "
                    + "grantId={GrantId} serverId={ServerId} approvedDigest={ApprovedDigest} currentDigest={CurrentDigest}",
                    activeGrant.Id.Value,
                    serverId.Value,
                    string.IsNullOrEmpty(activeGrant.ApprovedDefinitionDigest) ? "(none)" : activeGrant.ApprovedDefinitionDigest,
                    currentDigest);
                activeGrant = null;
            }
        }

        if (activeGrant is null)
        {
            // G1: use principal.RequestingNodeId as the requesting-node attribution, not the
            // agent client identity.
            // 031 (mobile-push increment 2, relocated into the admission seam that survives the
            // Space-MCP extraction): CreateAccessRequestWithStatusAsync (not the plain
            // CreateAccessRequestAsync wrapper) so we can see Created — the trigger for the
            // owner push notify below MUST fire only on a fresh insert, never on the idempotent
            // "already pending" replay.
            var createResult = await clusterClient.GetGrain<ISpaceGrain>(server.SpaceId.Value)
                .CreateAccessRequestWithStatusAsync(
                    agentClientId,
                    serverId,
                    principal.RequestingNodeId); // requesting node = the node that sent Hello on this stream

            if (createResult.Created)
            {
                // Detached, best-effort, NEVER awaited: notify the Space owner's push-enabled
                // devices of the new pending request. DetachedNotifyRunner passes its OWN
                // timeout-bound CancellationToken (NOT `cancellationToken`) — the caller typically
                // disconnects immediately after receiving Pending, which would cancel
                // `cancellationToken` before the notify's HTTP sends complete. Its internal token
                // also means a timeout actually CANCELS the fan-out (not just abandons the await),
                // and it swallows/logs every exception so a notify failure can never surface into
                // or slow this admission call.
                var notifySpaceId = server.SpaceId;
                _ = Task.Run(() => DetachedNotifyRunner.RunAsync(
                    ct => notifier.NotifyOwnerOfNewRequestAsync(notifySpaceId, createResult.Request, ct),
                    TimeSpan.FromSeconds(15),
                    logger,
                    notifySpaceId.Value));
            }

            return new AdmissionResult.Pending(createResult.Request.Id);
        }

        // 021 (Layer 2, admission): a grant exists and we are about to OPEN a real session — gate
        // on server availability HERE (not earlier), so the access-request/Pending path above
        // is never blocked by a momentarily-offline publisher (a durable access request can be
        // approved and consumed later). These two checks turn the #13 silent failure ("session
        // ready" then empty stdout, frames dropped) into a fast, explicit denial.
        //
        // (a) Not asserted: the server was omitted from the publisher's last SyncMcpServers — the
        // daemon explicitly stopped serving it even if the node is still online. Soft-retire closes
        // the "node online but server removed on reconnect" ghost that pure node-presence misses.
        if (!server.IsAsserted)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.ServerUnavailable));
        }

        var isHttpCloud = McpServerTransports.IsHttpCloud(server.Transport);

        if (!isHttpCloud)
        {
            // (b) Publisher node offline: use the EXPLICIT NodeGrain.Status (Online/Offline) — set
            // authoritatively by ConnectAsync/MarkOfflineAsync — NOT the stale-timestamp heuristic
            // (display-only per 019). This avoids the 019 false-negative: a publisher whose heartbeat
            // is momentarily late but whose gRPC stream is alive still has Status=Online and is admitted.
            // DELIBERATE divergence from the spec's "active-connection registry" wording: the gateway's
            // SessionRoutingTable._streamsByNode is SILO-LOCAL, so on a multi-silo Fly deploy a publisher
            // connected to silo B would false-negative this check on silo A. NodeGrain.Status is
            // cluster-global. Do NOT "fix" this back to the local registry.
            //
            // 030 (push-to-wake): if the node is Offline and has a push token, attempt a silent APNs
            // push and wait up to WakeWaitSeconds (default 12 s) for the node to come Online.
            // NodeWakeCoordinator.TryWakeAsync returns false immediately (zero added latency) for
            // non-wakeable nodes (CLI/Android/old iOS — no PushToken or APNs unconfigured).
            var ownerNode = await clusterClient.GetGrain<INodeGrain>(server.PublisherNodeId.Value).GetAsync();
            if (ownerNode.Status == NodeStatus.Offline)
            {
                // 030: attempt wake — returns true only if the node came Online within the window.
                // TryWakeAsync returns false immediately (zero added latency) for non-wakeable nodes
                // (CLI/Android/old iOS — no PushToken or APNs sender not configured).
                TimeSpan? wakeWaitOverride = principal.BindPolicy == ConsumerBindPolicy.ServerMinted
                    ? ServerMintedWakeWait
                    : null;
                var woke = await wakeCoordinator.TryWakeAsync(
                    ownerNode, cancellationToken, wakeWaitOverride);

                if (!woke)
                {
                    // Not wake-capable, or wake timed out.
                    // Distinguish the timeout case: if APNs is configured AND the node has a push token
                    // we attempted a wake (sent + waited), so surface the actionable node_waking code
                    // so the agent knows to retry in ~30 s. Otherwise standard server_unavailable.
                    var wakeWasAttempted = wakeCoordinator.IsConfigured
                        && !string.IsNullOrEmpty(ownerNode.PushToken);
                    return new AdmissionResult.Denied(
                        wakeWasAttempted
                            ? KoratError.Message(KoratErrorCode.NodeWaking)
                            : KoratError.Message(KoratErrorCode.ServerUnavailable));
                }

                // Node came Online within the wake window — proceed normally.
                // Re-fetch the authoritative node status so the grant-open path sees Online.
                ownerNode = await clusterClient.GetGrain<INodeGrain>(server.PublisherNodeId.Value).GetAsync();

                // FIX: re-fetch the server and re-validate IsAsserted/Status post-wake.
                // The pre-wake snapshot (above) was taken before the 12 s wait; the server could
                // have been disabled or un-asserted while we were waiting. Using the stale snapshot
                // opens a session against a server that is no longer available.
                var serverAfterWake = await repository.GetMcpServerAsync(serverId, cancellationToken);
                if (serverAfterWake is null || serverAfterWake.Status == McpServerStatus.Disabled
                    || !serverAfterWake.IsAsserted)
                {
                    return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.ServerUnavailable));
                }
                // Use the post-wake snapshot going forward.
                server = serverAfterWake;
            }
        }
        // Increment 1: an http_cloud server has no publisher node to be offline/wake — there is
        // nothing to check here. Availability for http_cloud is Published && IsAsserted only
        // (already checked above at the top of this method), which is always true for these rows
        // (Crux Finding: http_cloud has no SyncMcpServers soft-retire path).

        // Step-A defense-in-depth: re-validate the grant is STILL Active immediately before
        // opening — tightens the GetActiveGrantAsync→OpenSession window so a revoke landing
        // mid-open cannot produce a live session against a revoked grant. GetActiveGrantAsync
        // filters Status == Active, so a revoked grant returns null.
        var recheckGrant = await repository.GetActiveGrantAsync(server.SpaceId, agentClientId, serverId, cancellationToken);
        // Р26: the same digest condition as the first check. A re-publish landing inside this
        // window suspends the grant, but a suspension that races the read would otherwise let a
        // session open against a definition nobody approved — the exact window this recheck exists
        // to close, now widened to cover definition changes as well as revokes.
        if (recheckGrant is not null
            && !string.Equals(recheckGrant.ApprovedDefinitionDigest, McpServerDefinition.Digest(server), StringComparison.Ordinal))
        {
            recheckGrant = null;
        }
        if (recheckGrant is null)
        {
            return new AdmissionResult.Denied(KoratError.Message(KoratErrorCode.AccessDenied));
        }
        activeGrant = recheckGrant; // use the freshest grant snapshot for OpenAsync

        // G2: use the stable gateway grain for session-home assignment.
        var gatewayGrain = clusterClient.GetGrain<IGatewayGrain>(StableGatewayId.Value);
        var homeGatewayId = await gatewayGrain.AssignSessionHomeAsync();
        var sessionId = SessionId.New();
        var sessionGrain = clusterClient.GetGrain<ISessionGrain>(sessionId.Value);

        // Increment 1: an http_cloud session's "publisher" is NodeId.Empty — there is no relay
        // node (mirrors InferencePointGrain.PublishOutboundAsync's "outbound: no relay node").
        var effectivePublisherNodeId = isHttpCloud ? new NodeId(string.Empty) : server.PublisherNodeId;

        // G4: pass the actual SpaceId from the server record instead of hardcoding "default".
        // 022: also pass principal.AgentConnectionId (the per-stream id of THIS agent bridge, or
        // the aggregator's synthetic ConnectionId) so the SessionGrain persists it (DB column).
        // The route resolver will include it in SessionRouteInfo so publisher→agent frames reach
        // the correct bridge stream.
        await sessionGrain.OpenAsync(
            activeGrant.Id,
            activeGrant.ConsumerId,
            activeGrant.McpServerId,
            principal.RequestingNodeId,  // G1: client node = the node that sent Hello on this stream
            effectivePublisherNodeId,
            homeGatewayId,
            server.SpaceId,              // G4: real SpaceId from the resolved server
            principal.AgentConnectionId); // 022: per-bridge ConnectionId for publisher→agent routing

        // 005-mvp-relay-minimal: record the session-to-node routing BEFORE responding so the
        // peer stream can begin sending frames as soon as it receives SessionOpened without
        // racing the routing table insertion.
        // 022: pass principal.AgentConnectionId so ForwardFrameAsync routes publisher→agent frames
        // to the exact bridge stream (not to the shared NodeId slot).
        // F1: pass the same PayloadLimitPolicy advertised in SessionOpened so the routing table
        // enforces the same limits it tells the node about.
        var advertizedPolicy = new PayloadLimitPolicy();
        routingTable.OpenSession(sessionId, principal.RequestingNodeId, effectivePublisherNodeId, serverId, server.SpaceId,
            principal.AgentConnectionId, advertizedPolicy, isHttpCloud: isHttpCloud);

        // 031-relay-confidentiality: stamp advisory peer_supports_e2e flag on SessionOpened so the
        // agent can decide whether to attempt a handshake immediately. This is ADVISORY — the agent
        // MUST still validate the handshake outcome rather than trusting this flag blindly. Old cloud
        // omits the field (proto3 default false), which is the correct safe fallback.
        // Increment 1 (Crux Finding 9): E2E is semantically inapplicable to an http_cloud session
        // — the cloud IS the terminus (it must decrypt to make the outbound HTTP call), and there
        // is no publisher node to query a capability from. Force false without attempting the check.
        //
        // Space-MCP (SF-8): E2E is likewise inapplicable to a ServerMinted (aggregator-opened)
        // session — the cloud is again the plaintext terminus (it must decrypt to serve the HTTP
        // MCP responder), so force false and skip the capability query entirely, same as http_cloud.
        var publisherSupportsE2e = false;
        if (!isHttpCloud && principal.BindPolicy != ConsumerBindPolicy.ServerMinted)
        {
            try
            {
                publisherSupportsE2e = await clusterClient.GetGrain<INodeGrain>(server.PublisherNodeId.Value)
                    .HasCapabilityAsync("e2e-v1");
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort — advisory flag only; failure means false */ }
        }

        return new AdmissionResult.Opened(sessionId, homeGatewayId, publisherSupportsE2e);
    }

    private static string ConsumerDisplayName(ConsumerPrincipal principal)
    {
        if (DisplayNameRules.IsValid(principal.DisplayName ?? string.Empty, allowControlChars: false))
            return principal.DisplayName!;

        if (principal.BindPolicy == ConsumerBindPolicy.ServerMinted)
            return "Connected MCP client";

        var value = principal.ConsumerId.Value;
        var shortId = value.Length >= 8 ? value[..8] : value;
        return $"consumer-{shortId}";
    }
}
