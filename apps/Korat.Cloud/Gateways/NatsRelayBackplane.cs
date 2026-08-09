using Google.Protobuf;
using Korat.Domain;
using Korat.Relay.V1;
using NATS.Client.Core;

namespace Korat.Cloud.Gateways;

/// <summary>
/// 009-nats-relay-backplane: Core NATS implementation of <see cref="IRelayBackplane"/>.
/// Ephemeral, at-most-once — same delivery model as the in-process relay (a lost frame
/// surfaces as a session error; nothing is persisted/replayed). One background subscribe
/// loop per locally-connected node.
///
/// 022: adds connection-keyed methods (SubscribeConnectionAsync / PublishToConnectionAsync)
/// for the AGENT data plane. Uses a distinct NATS subject prefix (korat.relay.conn.) so
/// connection subjects can never alias node subjects regardless of encoded id values.
/// </summary>
public sealed class NatsRelayBackplane(INatsConnection nats, ILogger<NatsRelayBackplane> logger) : IRelayBackplane
{
    public async Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await nats.PublishAsync(NatsSubjects.Frame(target), message.ToByteArray(), cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("NATS publish failed targetNode={NodeId} errorType={ErrorType}", target.Value, ex.GetType().Name);
            return false;
        }
    }

    public async Task<IAsyncDisposable> SubscribeNodeAsync(
        NodeId nodeId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        // Establish the subscription synchronously (SubscribeCoreAsync returns only after the
        // SUB is registered on the server) BEFORE returning — so a peer publishing immediately
        // after the node registers does not race ahead of the subscription. Core NATS is
        // at-most-once: a frame published to a not-yet-established subject is silently dropped.
        var sub = await nats.SubscribeCoreAsync<byte[]>(NatsSubjects.Frame(nodeId), cancellationToken: cancellationToken);
        var subscription = new NodeSubscription(sub, nodeId.Value, onMessage, logger);
        subscription.Start();
        return subscription;
    }

    // 022: connection-keyed methods — mirror the node-keyed ones exactly.
    // Subject prefix is korat.relay.conn. + base64url(connectionId.Value), distinct
    // from korat.relay.frame. so subjects can never collide (LOCKED #6).

    public async Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await nats.PublishAsync(NatsSubjects.Conn(target), message.ToByteArray(), cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("NATS publish failed targetConn={ConnectionId} errorType={ErrorType}", target.Value, ex.GetType().Name);
            return false;
        }
    }

    public async Task<IAsyncDisposable> SubscribeConnectionAsync(
        ConnectionId connectionId,
        Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        // Same at-most-once guarantee as SubscribeNodeAsync: SUB is registered on the server
        // before returning, so the inbox is live before the first RequestSession (LOCKED #6).
        var sub = await nats.SubscribeCoreAsync<byte[]>(NatsSubjects.Conn(connectionId), cancellationToken: cancellationToken);
        var subscription = new NodeSubscription(sub, connectionId.Value, onMessage, logger);
        subscription.Start();
        return subscription;
    }

    // ── 029: inference data plane ─────────────────────────────────────────────────────────────────

    // Wire encoding: 1-byte type tag (0x00 = Chunk, 0x01 = End) followed by the protobuf bytes
    // of InferenceChunk or InferenceEnd respectively. Simple, zero-copy-friendly.

    // Shared message-loop for both node and connection subscriptions — the only difference
    // is the NATS subject (already embedded in the INatsSub) and the log key string.
    private sealed class NodeSubscription : IAsyncDisposable
    {
        private readonly INatsSub<byte[]> _sub;
        private readonly string _key; // NodeId.Value or ConnectionId.Value (for logging)
        private readonly Func<GatewayToNodeMessage, CancellationToken, Task> _onMessage;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public NodeSubscription(
            INatsSub<byte[]> sub,
            string key,
            Func<GatewayToNodeMessage, CancellationToken, Task> onMessage,
            ILogger logger)
        {
            _sub = sub;
            _key = key;
            _onMessage = onMessage;
            _logger = logger;
        }

        public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var msg in _sub.Msgs.ReadAllAsync(cancellationToken))
                {
                    if (msg.Data is not { Length: > 0 } data)
                        continue;

                    GatewayToNodeMessage envelope;
                    try
                    {
                        envelope = GatewayToNodeMessage.Parser.ParseFrom(data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("NATS frame parse failed key={Key} errorType={ErrorType}", _key, ex.GetType().Name);
                        continue;
                    }

                    try
                    {
                        await _onMessage(envelope, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("NATS inbound delivery failed key={Key} errorType={ErrorType}", _key, ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal unsubscribe.
            }
            catch (Exception ex)
            {
                _logger.LogWarning("NATS subscribe loop ended key={Key} errorType={ErrorType}", _key, ex.GetType().Name);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_loop is not null)
            {
                try { await _loop; } catch { /* loop swallows its own errors */ }
            }
            try { await _sub.DisposeAsync(); } catch { /* best-effort unsubscribe */ }
            _cts.Dispose();
        }
    }
}
