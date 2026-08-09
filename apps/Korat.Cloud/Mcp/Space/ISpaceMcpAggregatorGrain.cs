using Korat.Domain;
using Korat.Domain.Auth;

namespace Korat.Cloud.Mcp.Space;

/// <summary>
/// Space-MCP (increment 1, Task 4): the per-session context <c>InitializeAsync</c> is called
/// with. <see cref="ConsumerIdentity"/> is the durable, cagg_-prefixed identity
/// (<see cref="SpaceMcpConsumerIdentity.Derive"/>) the aggregator presents to
/// <c>ISessionAdmission.AdmitAsync</c> for every backend it opens — the SAME identity every time
/// for this (CliToken, Space) pair, so grants survive client reconnects.
/// </summary>
public sealed record SpaceMcpSessionContext(ConsumerId ConsumerIdentity, SpaceId SpaceId, UserId Owner);

/// <summary>
/// Space-MCP (increment 1, Task 4): the durable binding a session was opened with, returned by
/// <see cref="ISpaceMcpAggregatorGrain.GetBindingAsync"/> so the HTTP responder (Task 7) can
/// re-validate — on EVERY request, not just at <c>initialize</c> — that the caller's own
/// Bearer-derived consumer identity and Space still match what this session was opened for
/// (Global Constraint "Session-id-is-not-a-credential", SF-5): a session whose bound identity/
/// Space differs from the current caller's ⇒ 404, never trusting the session-id alone.
/// Plain strings (not <see cref="ConsumerId"/>/<see cref="SpaceId"/>) so this record needs no
/// extra Orleans surface beyond the value types it already wraps.
/// </summary>
public sealed record SpaceMcpBinding(string ConsumerId, string SpaceId);

/// <summary>
/// The Space-MCP aggregator grain — one activation per Streamable-HTTP MCP session
/// (<see cref="IGrainWithStringKey"/> keyed by the server-generated <c>Mcp-Session-Id</c>,
/// NEVER client-supplied — Global Constraint "Session-id-is-not-a-credential").
///
/// Task 3 introduced this interface with ONLY <see cref="OnDeliveryAsync"/> — the delivery-leg
/// entry point the B1 plan-review correction requires (every publisher→consumer frame the
/// aggregator's backend sessions receive is marshaled back onto THIS grain's own Orleans
/// scheduler through this single method; see
/// <see cref="Korat.Cloud.Gateways.CallbackServerStreamWriter"/> for the full rationale of why
/// that marshaling is mandatory, not optional).
///
/// Task 4 fills in the rest of the session lifecycle: <see cref="InitializeAsync"/>,
/// <see cref="DispatchAsync"/> (tools/list only — Task 5 adds concurrency/ungranted stubs, Task 6
/// adds tools/call routing + request-access), <see cref="NextListChangedAsync"/> (Task 8 fills in
/// the real long-poll/bump semantics), <see cref="TerminateAsync"/>, and
/// <see cref="GetBindingAsync"/>.
/// </summary>
public interface ISpaceMcpAggregatorGrain : IGrainWithStringKey
{
    /// <summary>
    /// Initializes this aggregator session: registers the in-process delivery leg, discovers the
    /// Space's Published MCP servers, opens a backend relay session (via
    /// <c>ISessionAdmission.AdmitAsync</c> with <c>ConsumerBindPolicy.ServerMinted</c>) for each
    /// GRANTED one, and returns the aggregator's own MCP <c>initialize</c> result JSON (echoing
    /// the client's requested <c>protocolVersion</c> — N4 — with
    /// <c>serverInfo.name="korat-space"</c> and <c>capabilities.tools.listChanged=true</c>).
    /// Idempotent-by-construction: called at most once per grain activation (the HTTP responder,
    /// Task 7, only calls this on the client's own <c>initialize</c> request).
    /// </summary>
    /// <param name="ctx">The durable consumer identity + Space + owner this session belongs to.</param>
    /// <param name="clientInitializeJson">The external client's raw <c>initialize</c> JSON-RPC
    /// request body — only its requested <c>protocolVersion</c> is read (N4 echo).</param>
    /// <returns>The aggregator's <c>initialize</c> JSON-RPC result JSON (not the full envelope —
    /// callers wrap it as needed).</returns>
    // MUST-FIX 1 (adversarial review, third pass, BLOCKER): Orleans' default grain-call response
    // timeout is 30s (MessagingOptions.ResponseTimeout, never overridden anywhere in this
    // codebase) — but this method's own fan-out (SpaceMcpAggregatorGrain.InitializeCoreAsync) can
    // legitimately take up to `ceil(N_granted / MaxConcurrentBackendOpens) * PerBackendTimeout` =
    // ceil(N/8) * 40s in the worst case. MUST exceed that budget comfortably; 3 minutes is
    // generous for any Space size this increment is scoped for (a very large granted-server count
    // is an O2-family concern, not this fix's scope — see SpaceMcpDispatcher's own O2 doc comment).
    [global::Orleans.ResponseTimeout("00:03:00")]
    Task<string> InitializeAsync(SpaceMcpSessionContext ctx, string clientInitializeJson);

    /// <summary>
    /// Dispatches one external-client JSON-RPC message (request or notification) against this
    /// session's catalog/backends. Returns the JSON-RPC response envelope for a request
    /// (<c>id</c> present), or <c>null</c> for a notification/response (<c>id</c> absent — the
    /// HTTP responder maps <c>null</c> to <c>202 Accepted</c> with no body).
    /// </summary>
    // MUST-FIX 1 (adversarial review, third pass, BLOCKER): a `tools/call` routed through this
    // method (HandleToolRouteAsync) can legitimately wait up to
    // SpaceBackendSession.ToolCallTimeout (300s) for a slow backend tool (build/test/shell — the
    // product's headline use case). Left at Orleans' 30s default, a call that legitimately runs
    // 45s would throw TimeoutException at the GRAIN-CALL boundary (SpaceMcpDispatcher.cs's
    // `await existingGrain.DispatchAsync(...)`) — a 500 to the client — while
    // HandleToolRouteAsync keeps running to 300s and silently discards the result. MUST exceed
    // A lazy mobile call can first spend up to 40s reopening/waking and then legitimately use the
    // full 300s tool timeout. Six minutes leaves transport margin around that combined budget.
    [global::Orleans.ResponseTimeout("00:06:00")]
    Task<string?> DispatchAsync(string jsonRpc);

    /// <summary>
    /// Task 8 (GET-SSE <c>list_changed</c> watch, SF-6): long-polls for the next
    /// <c>notifications/tools/list_changed</c>-worthy change. Returns immediately if this
    /// session's cursor has already moved past <paramref name="knownCursor"/>; otherwise blocks
    /// until either a bump lands (a synchronous revoke via <see cref="OnDeliveryAsync"/>, or
    /// the backstop reconcile timer picking up a newly-approved grant) or a bounded heartbeat
    /// elapses — bounded well under Orleans' 30s grain-call response timeout (plan-review
    /// correction N2) so this call itself never times out. A heartbeat elapsing is not an error:
    /// it returns the cursor UNCHANGED, letting the dispatcher's GET-SSE loop keep the connection
    /// alive without emitting a notification. Never throws.
    /// </summary>
    Task<long> NextListChangedAsync(long knownCursor);

    /// <summary>
    /// Tears down every backend relay session this aggregator opened and unregisters the
    /// in-process delivery leg (plan-review correction S4). Called by the HTTP responder's
    /// DELETE handler (Task 7); also mirrored by <see cref="Grain.OnDeactivateAsync"/> so an
    /// abandoned session (client never DELETEs) does not leak backend sessions/routing entries
    /// past this activation's own deactivation.
    /// </summary>
    // MUST-FIX 1 (adversarial review, third pass, BLOCKER): terminates N backends at roughly
    // ~2s each (SessionTerminator.TerminateSessionAsync's own repository read + close-frame
    // round trip) sequentially — a Space with many granted backends can cross Orleans' 30s
    // default well before this method returns. 2 minutes covers a generous backend count with
    // margin.
    [global::Orleans.ResponseTimeout("00:02:00")]
    Task TerminateAsync();

    /// <summary>
    /// Returns this session's durable binding (identity + Space it was <see cref="InitializeAsync"/>-ed
    /// with), or <c>null</c> if this activation was never initialized (a fresh/recycled
    /// activation the caller's session-id no longer really points at). The HTTP responder
    /// re-validates this on EVERY non-initialize request (SF-5) — never trusting the
    /// session-id alone.
    /// </summary>
    Task<SpaceMcpBinding?> GetBindingAsync();

    /// <summary>
    /// Delivers one publisher→consumer event belonging to a backend relay session this grain
    /// opened (Task 4, via <c>ISessionAdmission.AdmitAsync</c> with
    /// <c>ConsumerBindPolicy.ServerMinted</c>).
    ///
    /// Called ONLY from <see cref="Korat.Cloud.Gateways.CallbackServerStreamWriter"/> — the
    /// registered writer for this grain's synthetic <c>ConnectionId</c>
    /// (<see cref="SpaceMcpConsumerIdentity.SyntheticConnectionId"/>). That writer is the ONLY
    /// caller allowed to reach this grain off the HTTP request path: it is the thread-hop shim
    /// that marshals a delivery originating on a gRPC-publisher-stream thread, a
    /// <c>SessionTerminator</c> thread, or a NATS-backplane-callback thread back onto this
    /// grain's own scheduler turn (B1 plan-review correction —
    /// <c>SessionRoutingTable.WriteLocalToConnectionAsync</c> invokes the registered writer
    /// INLINE on the caller's thread; <c>[Reentrant]</c> does not legalize a foreign thread
    /// mutating grain state directly, and a throw out of that writer would evict the delivery
    /// leg permanently — see the writer's own doc comment).
    ///
    /// All grain-state mutation this event triggers (demuxing into the per-backend-session
    /// table, catalog rebuild, list_changed cursor bump — Task 4/5/8) happens INSIDE this method,
    /// on the scheduler, exactly like any other grain call — never in the writer itself.
    /// </summary>
    /// <param name="backendSessionId">
    /// The relay <c>SessionId</c> (<c>RelayFrame.SessionId</c> / <c>CloseSession.SessionId</c> /
    /// <c>PayloadLimitExceeded.SessionId</c>) identifying which of this grain's (possibly many)
    /// open backend sessions the event belongs to.
    /// </param>
    /// <param name="payload">
    /// The raw plaintext MCP bytes for a data frame (<c>RelayFrame.Ciphertext</c> — "ciphertext"
    /// only by legacy field name; Space-MCP backend sessions are always plaintext, forced
    /// <c>enc=0</c> — Global Constraint "Forced peer_supports_e2e=false"). Empty for a
    /// <paramref name="closeReason"/> event.
    /// </param>
    /// <param name="enc">
    /// The <c>RelayFrame</c> encryption-scheme indicator. Task 4's backend-session port fails
    /// closed on any nonzero value rather than silently misinterpreting ciphertext as plaintext
    /// (plan-review N3).
    /// </param>
    /// <param name="closeReason">
    /// Non-null when this call represents a <c>CloseSession</c> or <c>PayloadLimitExceeded</c>
    /// event instead of a data frame — <paramref name="backendSessionId"/> still identifies
    /// which backend faulted; <paramref name="payload"/> is empty in that case.
    /// </param>
    Task OnDeliveryAsync(string backendSessionId, byte[] payload, uint enc, string? closeReason);
}
