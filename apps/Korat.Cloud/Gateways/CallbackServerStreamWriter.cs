using Grpc.Core;
using Korat.Cloud.Mcp.Space;
using Korat.Relay.V1;

namespace Korat.Cloud.Gateways;

/// <summary>
/// B1 plan-review correction (2026-07-10 Space-MCP increment-1 plan, Task 3): the in-process
/// delivery-leg sink registered against <see cref="SessionRoutingTable.RegisterAgentStreamAsync"/>
/// for the Space-MCP aggregator grain's synthetic <c>ConnectionId</c>
/// (<see cref="SpaceMcpConsumerIdentity.SyntheticConnectionId"/>).
///
/// WHY THIS EXISTS (the bug this fixes): <see cref="SessionRoutingTable.WriteLocalToConnectionAsync"/>
/// (<c>SessionRoutingTable.cs:997-1019</c>) invokes the registered writer's <c>WriteAsync</c>
/// INLINE on the CALLER's thread — a gRPC publisher-stream read-loop thread,
/// <c>SessionTerminator</c>'s thread, or a NATS backplane subscription callback thread. NONE of
/// these is the aggregator grain's own Orleans scheduler thread. <c>[Reentrant]</c> on the grain
/// does NOT legalize a foreign thread mutating grain state directly — Orleans' single-threaded-
/// execution guarantee only holds for calls that go through a grain reference. Naively touching
/// grain fields from this writer would be a silent, hard-to-reproduce concurrency bug. Worse,
/// <c>WriteLocalToConnectionAsync</c> EVICTS the writer's <c>ConnectionId</c> slot on ANY
/// exception thrown out of <c>WriteAsync</c> (<c>:1012-1017</c>) — one unhandled throw here would
/// silently and permanently kill the delivery leg for the rest of the MCP session.
///
/// THE FIX: this writer does NO grain-state mutation itself — it is a pure thread-hop shim.
/// <see cref="WriteAsync"/> extracts only PRIMITIVES from the protobuf message (never passes the
/// <see cref="GatewayToNodeMessage"/> itself into the grain call — Orleans' grain-call serializer
/// only knows the app's own <c>[GenerateSerializer]</c> types, not Grpc/protobuf-generated ones)
/// and calls <see cref="ISpaceMcpAggregatorGrain.OnDeliveryAsync"/> via a grain reference obtained
/// from <see cref="IGrainFactory"/>. That call is a normal Orleans grain call — the runtime
/// marshals it onto the grain's own scheduler turn exactly like any other grain invocation,
/// regardless of which thread initiated it. ALL grain-state mutation (demuxing into the
/// per-backend-session table, catalog rebuild, list_changed cursor bump — Task 4/5/8) happens
/// inside <c>OnDeliveryAsync</c>, ON the scheduler — never here.
///
/// <see cref="WriteAsync"/> NEVER throws: any exception from the grain call (including the grain
/// being deactivated, unreachable, or the cluster being unavailable) is caught and logged here,
/// so <see cref="SessionRoutingTable"/> never evicts this delivery leg over a single failed or
/// racing delivery. The synthetic, in-process leg has no reconnect story of its own (unlike a
/// real gRPC bridge stream, which the CLI/node can just redial) — staying registered is strictly
/// better than being silently and permanently evicted after one transient grain-call failure.
/// </summary>
public sealed class CallbackServerStreamWriter(
    IGrainFactory grainFactory,
    string aggregatorGrainKey,
    ILogger logger) : IServerStreamWriter<GatewayToNodeMessage>
{
    public WriteOptions? WriteOptions { get; set; }

    /// <summary>
    /// MUST-FIX 3 (adversarial review, reworking the S4-era shared-code change): confirmed by
    /// reflection that <c>Grpc.Core.IAsyncStreamWriter&lt;T&gt;.WriteAsync(T, CancellationToken)</c>
    /// is a DEFAULT INTERFACE METHOD declared directly on the interface (not an extension method —
    /// the earlier <c>SessionRoutingTable</c> comment calling it
    /// <c>Grpc.Core.AsyncStreamExtensions</c> was wrong), and that its default body throws
    /// <c>NotSupportedException("Cancellation of stream writes is not supported by this gRPC
    /// implementation.")</c> whenever the token's <c>CanBeCanceled</c> is true — which a real gRPC
    /// <c>ServerCallContext.CancellationToken</c> always is. <see cref="SessionRoutingTable"/>'s
    /// shared <c>WriteLocalToConnectionAsync</c> path has been reverted back to calling the
    /// two-argument overload (behavior-preserving for every OTHER, gRPC-native writer, which
    /// overrides this DIM implicitly) — instead, THIS synthetic in-process writer overrides the
    /// DIM explicitly, so interface-typed dispatch (the call site's static type is
    /// <c>IAsyncStreamWriter&lt;GatewayToNodeMessage&gt;</c>) resolves to this override rather than
    /// the throwing default. The synthetic delivery leg has no per-write cancellation semantics of
    /// its own (there is no real HTTP/2 stream underneath it to cancel) — delegating straight to
    /// the existing, never-throwing single-argument <see cref="WriteAsync(GatewayToNodeMessage)"/>
    /// is the correct behavior, and keeps the fix scoped to exactly this writer instead of the
    /// shared node hot-path.
    /// </summary>
    Task IAsyncStreamWriter<GatewayToNodeMessage>.WriteAsync(GatewayToNodeMessage message, CancellationToken cancellationToken) =>
        WriteAsync(message);

    public async Task WriteAsync(GatewayToNodeMessage message)
    {
        try
        {
            var grain = grainFactory.GetGrain<ISpaceMcpAggregatorGrain>(aggregatorGrainKey);

            switch (message.PayloadCase)
            {
                case GatewayToNodeMessage.PayloadOneofCase.Frame:
                    var frame = message.Frame;
                    await grain.OnDeliveryAsync(frame.SessionId, frame.Ciphertext.ToByteArray(), frame.Enc, closeReason: null);
                    break;

                case GatewayToNodeMessage.PayloadOneofCase.CloseSession:
                    var close = message.CloseSession;
                    await grain.OnDeliveryAsync(close.SessionId, Array.Empty<byte>(), enc: 0, closeReason: close.Reason);
                    break;

                case GatewayToNodeMessage.PayloadOneofCase.PayloadLimitExceeded:
                    var limit = message.PayloadLimitExceeded;
                    await grain.OnDeliveryAsync(limit.SessionId, Array.Empty<byte>(), enc: 0, closeReason: limit.LimitName);
                    break;

                default:
                    // AccessPending/AccessDenied/SessionOpened/Hello/HeartbeatAck/etc are never
                    // sent by SessionRoutingTable to an ALREADY-OPEN backend session's
                    // ConnectionId — only Frame/CloseSession/PayloadLimitExceeded target an agent
                    // ConnectionId post-open. Ignore anything unexpected rather than throwing.
                    logger.LogDebug(
                        "CallbackServerStreamWriter ignored unexpected payload case={PayloadCase} grainKey={GrainKey}",
                        message.PayloadCase, aggregatorGrainKey);
                    break;
            }
        }
        catch (Exception ex)
        {
            // NEVER let this escape — SessionRoutingTable.WriteLocalToConnectionAsync evicts this
            // writer's ConnectionId slot on ANY exception (:1012-1017). Swallowing + logging here
            // trades "this one delivery might be lost on a transient grain-call failure" for "the
            // delivery leg stays registered for the rest of the MCP session" — the correct trade
            // for a synthetic leg with no reconnect path of its own.
            logger.LogWarning(ex,
                "CallbackServerStreamWriter: grain delivery failed (swallowed, leg not evicted) grainKey={GrainKey} payloadCase={PayloadCase}",
                aggregatorGrainKey, message.PayloadCase);
        }
    }
}
