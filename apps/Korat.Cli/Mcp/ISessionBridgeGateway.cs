using Korat.Relay.V1;

namespace Korat.Cli.Mcp;

/// <summary>
/// Minimal send surface of the gateway connection needed by <see cref="SessionBridge"/>.
/// Extracted as an interface to enable test injection (and to decouple SessionBridge from
/// the concrete <see cref="Korat.Cli.Gateway.NodeGatewayConnection"/> transport type).
/// </summary>
internal interface ISessionBridgeGateway
{
    Task SendE2eKeyAnswerAsync(
        string sessionId,
        uint version,
        string curve,
        byte[] pubKey,
        byte[] confirmTag,
        CancellationToken cancellationToken = default);

    Task SendFrameAsync(
        string sessionId,
        ReadOnlyMemory<byte> ciphertext,
        ulong sequenceNumber,
        string direction,
        CancellationToken cancellationToken = default);

    Task SendE2eFrameAsync(
        string sessionId,
        ReadOnlyMemory<byte> wirePayload,
        ulong sequenceNumber,
        string direction,
        FrameMetadata meta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// cli-m8: notify the cloud to close the given session (best-effort — publisher-initiated).
    /// </summary>
    Task SendCloseSessionAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default);
}
