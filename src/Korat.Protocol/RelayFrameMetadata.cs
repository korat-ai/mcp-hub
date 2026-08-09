// DEFERRED: encrypted-relay milestone — not yet wired into the live gateway (which relays cleartext); see NodeGatewayService
namespace Korat.Protocol;

public sealed record RelayFrameMetadata(
    string SessionId,
    string SourceNodeId,
    string TargetNodeId,
    ulong SequenceNumber,
    string Direction,
    long CiphertextByteCount)
{
    public const string TestPayloadMarker = "__KORAT_TEST_PAYLOAD__";

    public LogSafeRelayFrameMetadata ToLogSafe() => new(
        SessionId,
        SourceNodeId,
        TargetNodeId,
        SequenceNumber,
        Direction,
        CiphertextByteCount);

    public static RelayFrameMetadata FromCiphertext(
        string sessionId,
        string sourceNodeId,
        string targetNodeId,
        ulong sequenceNumber,
        string direction,
        ReadOnlySpan<byte> ciphertext) =>
        new(sessionId, sourceNodeId, targetNodeId, sequenceNumber, direction, ciphertext.Length);
}

public sealed record LogSafeRelayFrameMetadata(
    string SessionId,
    string SourceNodeId,
    string TargetNodeId,
    ulong SequenceNumber,
    string Direction,
    long CiphertextByteCount)
{
    public bool ContainsPayloadMarker(string? value) =>
        value?.Contains(RelayFrameMetadata.TestPayloadMarker, StringComparison.Ordinal) == true;
}

public static class PayloadLoggingGuard
{
    public static bool IsSafeForLogging(object? value)
    {
        if (value is null) return true;
        var text = value.ToString();
        return text is null || !text.Contains(RelayFrameMetadata.TestPayloadMarker, StringComparison.Ordinal);
    }
}
