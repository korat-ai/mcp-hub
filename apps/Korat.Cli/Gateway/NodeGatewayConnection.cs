using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Korat.Cli.Auth;
using Korat.Cli.Commands;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cli.Gateway;

/// <summary>Thrown when the gateway stream ends unexpectedly or returns an unexpected message.</summary>
public sealed class GatewayDisconnectedException : Exception
{
    public GatewayDisconnectedException(string message) : base(message) { }
    public GatewayDisconnectedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Long-lived gRPC stream to the cloud gateway with multiplexed read demux.
///
/// Owns a single background reader task that pulls messages off the response stream
/// and routes them to one of two channels:
///   - HeartbeatAcks → consumed by the heartbeat liveness check.
///   - Everything else (Frame, SessionOpened, AccessPending/Denied, …) → exposed
///     via <see cref="IncomingMessages"/> for the consumer (UpCommand / ConnectCommand).
///
/// Without demultiplexing, the heartbeat code path would race the frame-handling code
/// path on `MoveNext()` and one would steal the other's message.
/// </summary>
internal sealed class NodeGatewayConnection : IAsyncDisposable, Korat.Cli.Mcp.Aggregation.IGatewayConnection, Korat.Cli.Mcp.ISessionBridgeGateway
{
    private readonly GrpcChannel _channel;
    private readonly AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> _call;
    private readonly LocalIdentity _identity;
    private readonly string _displayName;

    private readonly Channel<GatewayToNodeMessage> _heartbeatAcks =
        Channel.CreateUnbounded<GatewayToNodeMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<GatewayToNodeMessage> _incoming =
        Channel.CreateUnbounded<GatewayToNodeMessage>(new UnboundedChannelOptions { SingleReader = true });

    // gRPC request streams are not safe for concurrent writes. Serialize Send* calls.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _readLoopCts = new();
    private readonly Task _readLoopTask;
    private volatile bool _disposed;

    private NodeGatewayConnection(
        GrpcChannel channel,
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call,
        LocalIdentity identity,
        string displayName,
        GatewayHello gatewayHello)
    {
        _channel = channel;
        _call = call;
        _identity = identity;
        _displayName = displayName;
        GatewayHello = gatewayHello;

        _readLoopTask = Task.Run(ReadLoopAsync);
    }

    public GatewayHello GatewayHello { get; }

    /// <summary>Stream of every non-HeartbeatAck message the cloud sends us.</summary>
    public ChannelReader<GatewayToNodeMessage> IncomingMessages => _incoming.Reader;

    public static async Task<NodeGatewayConnection> ConnectAsync(
        LocalIdentity identity,
        string displayName,
        CancellationToken cancellationToken = default,
        CliCredentials? cliCredentials = null,
        string? nodeIdOverride = null,
        string nodeKind = "publisher",
        string? agentIdHint = null)
    {
        // 006-cli-stdio-bridge: gRPC needs the HTTP/2-only port, which is separate
        // from the REST port in the dev cloud. Fall back to CloudUrl when GrpcUrl
        // is unset for backwards compatibility with pre-006 configs.
        var grpcUrl = string.IsNullOrWhiteSpace(identity.CloudGrpcUrl)
            ? identity.CloudUrl
            : identity.CloudGrpcUrl;
        var channel = GrpcChannel.ForAddress(grpcUrl);
        var client = new NodeGatewayService.NodeGatewayServiceClient(channel);

        // SP4: pass CliCredentials as call-level gRPC metadata (Authorization: Bearer)
        // so the server's Bearer branch in HandleHelloAsync authenticates the stream.
        // The per-node NodeAuthToken HMAC is not used from the CLI — the server skips
        // the HMAC check when BearerUserId is resolved. When no CliCredentials are
        // provided (e.g. UpCommand before first login), the HMAC is empty and the
        // server falls back to its own owner-token path if configured.
        CallOptions callOptions;
        if (cliCredentials is not null)
        {
            var headers = new Metadata
            {
                { "authorization", $"Bearer {cliCredentials.AccessToken}" }
            };
            callOptions = new CallOptions(headers: headers, cancellationToken: cancellationToken);
        }
        else
        {
            callOptions = new CallOptions(cancellationToken: cancellationToken);
        }

        // nodeAuthToken is always empty from the CLI — authentication goes through
        // Bearer (CliCredentials) and the server's own fallback if needed.
        var nodeAuthToken = string.Empty;

        // 017: agent connections override the NodeId so their stream is keyed
        // under a different routing-table entry than the publisher stream.
        // nodeKind is forwarded in NodeHello.NodeKind so the cloud can persist
        // the role (Publisher vs Agent) on the Node entity.
        var effectiveNodeId = nodeIdOverride ?? identity.NodeId;

        var call = client.Connect(callOptions);

        // 029: advertise "inference" capability when this node has at least one
        // inference point registered. The cloud checks HasCapabilityAsync("inference")
        // in InferenceDispatcher before dispatching any InferenceRequest.
        var hello = new NodeHello
        {
            SpaceId = identity.SpaceId,
            NodeId = effectiveNodeId,
            DisplayName = displayName,
            NodeAuthToken = nodeAuthToken,
            NodeKind = nodeKind,
            CliVersion = Korat.Cli.Util.CliVersion.Bare(),
            // Node host metadata (additive, node-visibility-doctor design 2026-07-02): lets the
            // cloud/console answer "where is this node running" — see HostMetadata for details.
            Hostname = Korat.Cli.Util.HostMetadata.Hostname,
            Os = Korat.Cli.Util.HostMetadata.Os,
            Arch = Korat.Cli.Util.HostMetadata.Arch,
            // PR-5 (agent-id-identity, additive): set ONLY by an "agent" node_kind
            // connection that knows its hosted agent's stable cloud AgentId (threaded from
            // ConnectCommand.ResolveOrCreateAgent's resolved/recorded identity). Empty for
            // publisher connections and for a mixed-version rollout (agentIdHint null) — the
            // cloud treats an empty agent_id as "cannot stamp attribution", never as an error.
        };
        if (identity.InferencePoints.Count > 0)
            hello.Capabilities.Add("inference");
        // 031: advertise E2E capability on all connections (publisher + agent).
        hello.Capabilities.Add("e2e-v1");

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage { Hello = hello });

        if (!await call.ResponseStream.MoveNext(cancellationToken))
            throw new InvalidOperationException("Cloud closed stream before GatewayHello.");

        if (call.ResponseStream.Current.PayloadCase != GatewayToNodeMessage.PayloadOneofCase.Hello)
            throw new InvalidOperationException($"Expected GatewayHello, got {call.ResponseStream.Current.PayloadCase}.");

        return new NodeGatewayConnection(
            channel,
            call,
            identity,
            displayName,
            call.ResponseStream.Current.Hello);
    }

    /// <summary>
    /// Sends a heartbeat and waits up to <paramref name="timeout"/> for the matching ack.
    /// Throws <see cref="GatewayDisconnectedException"/> if the stream dies first.
    /// </summary>
    public async Task SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(new NodeToGatewayMessage
        {
            Heartbeat = new Heartbeat
            {
                NodeId = _identity.NodeId,
                SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        }, cancellationToken);

        try
        {
            await _heartbeatAcks.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException ex)
        {
            throw new GatewayDisconnectedException("Gateway closed the stream during heartbeat.", ex);
        }
    }

    /// <summary>
    /// Sends a RelayFrame on the gateway stream. The cloud will route it to the peer
    /// node of the named session (see <c>SessionRoutingTable.ForwardFrameAsync</c>).
    /// </summary>
    public async Task SendFrameAsync(
        string sessionId,
        ReadOnlyMemory<byte> ciphertext,
        ulong sequenceNumber,
        string direction,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = sequenceNumber,
                Direction = direction,
                Ciphertext = ByteString.CopyFrom(ciphertext.Span)
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 031: Sends an E2E-encrypted RelayFrame (enc=1) with cleartext metadata header.
    /// The cloud forwards frame opaque; the metadata is used only for telemetry.
    /// </summary>
    public async Task SendE2eFrameAsync(
        string sessionId,
        ReadOnlyMemory<byte> wirePayload,
        ulong sequenceNumber,
        string direction,
        FrameMetadata meta,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = sequenceNumber,
                Direction = direction,
                Ciphertext = ByteString.CopyFrom(wirePayload.Span),
                Enc = 1,
                Meta = meta,
            }
        }, cancellationToken);
    }

    /// <summary>Sends a RequestSession message (used by ConnectCommand).</summary>
    public async Task SendRequestSessionAsync(
        string requestId,
        string agentClientId,
        string mcpServerId,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = requestId,
                AgentClientId = agentClientId,
                McpServerId = mcpServerId
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Sends a <see cref="PublishMcpServer"/> message and returns the request ID so the
    /// caller can correlate the <see cref="PublishMcpServerAck"/> from
    /// <see cref="IncomingMessages"/>.
    /// </summary>
    public async Task<string> SendPublishMcpServerAsync(
        string nodeId,
        string displayName,
        string command,
        IEnumerable<string> args,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var msg = new PublishMcpServer
        {
            RequestId = requestId,
            NodeId = nodeId,
            DisplayName = displayName,
            Command = command,
        };
        msg.Args.AddRange(args);

        await WriteAsync(new NodeToGatewayMessage { PublishMcpServer = msg }, cancellationToken);
        return requestId;
    }

    /// <summary>
    /// 021 (Layer 1): sends a <see cref="SyncMcpServers"/> message carrying the daemon's
    /// complete current server set. The cloud reconciles its state to match (upsert + soft-retire)
    /// and replies with one <see cref="PublishMcpServerAck"/> per server so the daemon can
    /// rebuild its routing map. The acks arrive through the normal inbound dispatch loop and
    /// are matched by DisplayName (RequestId is empty on sync acks).
    /// </summary>
    public async Task SendSyncMcpServersAsync(
        string nodeId,
        IEnumerable<ServerDesc> servers,
        CancellationToken cancellationToken = default)
    {
        var msg = new SyncMcpServers { NodeId = nodeId };
        msg.Servers.AddRange(servers);
        await WriteAsync(new NodeToGatewayMessage { SyncMcpServers = msg }, cancellationToken);
    }

    /// <summary>
    /// Sends an <see cref="UnpublishMcpServer"/> message to take a server offline.
    /// </summary>
    public async Task SendUnpublishMcpServerAsync(
        string nodeId,
        string mcpServerId,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(new NodeToGatewayMessage
        {
            UnpublishMcpServer = new UnpublishMcpServer
            {
                RequestId = Guid.NewGuid().ToString("N"),
                NodeId = nodeId,
                McpServerId = mcpServerId,
            }
        }, cancellationToken);
    }

    // ── 031: E2E key exchange (agent / publisher → cloud) ────────────────────────────────────────

    /// <summary>
    /// 031: Agent → cloud: send an E2eKeyOffer for the given session.
    /// </summary>
    public Task SendE2eKeyOfferAsync(
        string sessionId,
        uint version,
        string curve,
        byte[] pubKey,
        byte[] salt,
        CancellationToken cancellationToken = default)
        => WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyOffer = new E2eKeyOffer
            {
                SessionId = sessionId,
                Version = version,
                Curve = curve,
                PubKey = ByteString.CopyFrom(pubKey),
                Salt = ByteString.CopyFrom(salt),
            }
        }, cancellationToken);

    /// <summary>
    /// 031: Publisher → cloud: send an E2eKeyAnswer for the given session.
    /// </summary>
    public Task SendE2eKeyAnswerAsync(
        string sessionId,
        uint version,
        string curve,
        byte[] pubKey,
        byte[] confirmTag,
        CancellationToken cancellationToken = default)
        => WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyAnswer = new E2eKeyAnswer
            {
                SessionId = sessionId,
                Version = version,
                Curve = curve,
                PubKey = ByteString.CopyFrom(pubKey),
                ConfirmTag = ByteString.CopyFrom(confirmTag),
            }
        }, cancellationToken);

    /// <summary>
    /// 031: Agent → cloud: send E2eKeyConfirm to close the handshake.
    /// </summary>
    public Task SendE2eKeyConfirmAsync(
        string sessionId,
        byte[] confirmTag,
        CancellationToken cancellationToken = default)
        => WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyConfirm = new E2eKeyConfirm
            {
                SessionId = sessionId,
                ConfirmTag = ByteString.CopyFrom(confirmTag),
            }
        }, cancellationToken);

    /// <summary>
    /// cli-m8: publisher/aggregator → cloud: notify the cloud to close the given session.
    /// Best-effort — callers should catch and swallow exceptions.
    /// </summary>
    public Task SendCloseSessionAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
        => WriteAsync(new NodeToGatewayMessage
        {
            CloseSession = new CloseSession
            {
                SessionId = sessionId,
                Reason = reason,
            }
        }, cancellationToken);

    // ── 029: Inference streaming (node → cloud) ───────────────────────────────────────────────────

    private async Task WriteAsync(NodeToGatewayMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _call.RequestStream.WriteAsync(message, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _call.ResponseStream.MoveNext(_readLoopCts.Token))
            {
                var msg = _call.ResponseStream.Current;
                if (msg.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.HeartbeatAck)
                {
                    await _heartbeatAcks.Writer.WriteAsync(msg, _readLoopCts.Token);
                }
                else
                {
                    await _incoming.Writer.WriteAsync(msg, _readLoopCts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Stream died for any other reason — surface as ChannelClosedException
            // to anyone waiting on a read.
            _heartbeatAcks.Writer.TryComplete(ex);
            _incoming.Writer.TryComplete(ex);
            return;
        }

        _heartbeatAcks.Writer.TryComplete();
        _incoming.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _readLoopCts.Cancel(); } catch { /* best-effort */ }

        try
        {
            await _call.RequestStream.CompleteAsync();
        }
        catch
        {
            // Best-effort cleanup — the stream may already be torn down.
        }

        try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }

        _call.Dispose();
        _channel.Dispose();
        _readLoopCts.Dispose();
        _writeLock.Dispose();
    }
}
