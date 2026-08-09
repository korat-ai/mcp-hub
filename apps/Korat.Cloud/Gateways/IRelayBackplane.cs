using Korat.Relay.V1;
using Korat.Domain;

namespace Korat.Cloud.Gateways;

/// <summary>
/// 009-nats-relay-backplane: cross-machine transport for relay messages. Lets a frame
/// reach a node connected to a DIFFERENT machine. The in-process fast path
/// (<see cref="SessionRoutingTable"/>) handles same-machine peers; this backplane handles
/// the rest. Carries the full <see cref="GatewayToNodeMessage"/> envelope (frames AND
/// control messages such as CloseSession) so teardown works cross-machine too.
///
/// Orleans remains the control plane (session topology); this is purely byte transport.
/// </summary>
public interface IRelayBackplane
{
    /// <summary>
    /// Publish a message to whichever machine currently holds <paramref name="target"/>'s
    /// live stream. Returns false if publish failed (treated as undeliverable).
    /// </summary>
    Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribe this machine to inbound messages destined for <paramref name="nodeId"/>
    /// (because that node's live stream is connected here). The handler writes each message
    /// to the local stream. Dispose the returned handle to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeNodeAsync(
        NodeId nodeId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken);

    // 022: connection-keyed transport for the AGENT data plane.
    // Agent streams are addressed by ConnectionId (one per gRPC stream), not NodeId
    // (one per identity), because an agent fans out to N concurrent bridge processes.
    // Publisher streams continue to use the node-keyed methods above.

    /// <summary>
    /// Publish a message to whichever machine currently holds the agent stream identified
    /// by <paramref name="target"/> (a per-stream ConnectionId). Returns false if publish failed.
    /// </summary>
    Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribe this machine to inbound messages destined for the agent stream identified
    /// by <paramref name="connectionId"/>. Dispose the returned handle to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeConnectionAsync(
        ConnectionId connectionId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken);

    // 029: inference data plane — cross-silo routing for InferenceChunk / InferenceEnd.
    // The serving silo (HTTP request) subscribes before dispatching InferenceRequest; the
    // node's gRPC silo publishes each chunk/end. NATS delivers cross-silo; single-silo
    // falls back to the direct OnChunk path in InferenceResponseBroker.

}

/// <summary>
/// No-op backplane used when NATS_URL is absent (single-machine fallback — kept for ≥6
/// months per the 009 decision). Publish always reports undeliverable, which is exactly
/// today's behaviour when a peer is not connected to this machine.
/// </summary>
public sealed class NullRelayBackplane : IRelayBackplane
{
    public Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<IAsyncDisposable> SubscribeNodeAsync(
        NodeId nodeId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
        => Task.FromResult<IAsyncDisposable>(NoopSubscription.Instance);

    // 022: connection-keyed no-ops — same semantics as the node-keyed ones above.
    // NullBackplane is single-machine; connection-keyed routing is handled entirely
    // in-process via _agentStreamsByConnection, so these are never actually called.
    public Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<IAsyncDisposable> SubscribeConnectionAsync(
        ConnectionId connectionId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
        => Task.FromResult<IAsyncDisposable>(NoopSubscription.Instance);

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public static readonly NoopSubscription Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
