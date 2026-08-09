using System.Threading.Channels;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>Subset of <see cref="Korat.Cli.Gateway.NodeGatewayConnection"/> the aggregator needs, for testing.</summary>
internal interface IGatewayConnection
{
    ChannelReader<GatewayToNodeMessage> IncomingMessages { get; }
    Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken cancellationToken = default);
    Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong sequenceNumber, string direction, CancellationToken cancellationToken = default);
    Task SendHeartbeatAsync(CancellationToken cancellationToken = default);
    // 031: E2E support for the aggregator path (MAJOR-3).
    Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, FrameMetadata meta, CancellationToken cancellationToken = default);
    Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken cancellationToken = default);
    Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken cancellationToken = default);
    // cli-m8: publisher/aggregator-initiated session close notification to cloud (best-effort).
    Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default);
}
