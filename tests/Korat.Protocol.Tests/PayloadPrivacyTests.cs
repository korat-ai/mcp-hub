using Korat.Protocol;

namespace Korat.Protocol.Tests;

public class PayloadPrivacyTests
{
    [Fact]
    public void LogSafeMetadata_ExcludesPayloadMarker()
    {
        var metadata = RelayFrameMetadata.FromCiphertext(
            "session-1", "node-a", "node-b", 1, "client_to_server", [1, 2, 3]);

        var logSafe = metadata.ToLogSafe();
        Assert.False(logSafe.ContainsPayloadMarker(logSafe.SessionId));
        Assert.True(PayloadLoggingGuard.IsSafeForLogging(logSafe));
        Assert.False(PayloadLoggingGuard.IsSafeForLogging(RelayFrameMetadata.TestPayloadMarker));
    }
}
