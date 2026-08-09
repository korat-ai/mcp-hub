namespace Korat.Domain.Tests;

public class KoratErrorTests
{
    [Theory]
    [InlineData(KoratErrorCode.PendingApproval)]
    [InlineData(KoratErrorCode.AccessDenied)]
    [InlineData(KoratErrorCode.GrantRevoked)]
    [InlineData(KoratErrorCode.ServerDisabled)]
    [InlineData(KoratErrorCode.ServerUnavailable)]
    [InlineData(KoratErrorCode.OfflineNode)]
    [InlineData(KoratErrorCode.PayloadLimitExceeded)]
    [InlineData(KoratErrorCode.CryptoFailure)]
    [InlineData(KoratErrorCode.DuplicateServerName)]
    public void Message_IsNonEmpty(KoratErrorCode code)
    {
        Assert.False(string.IsNullOrWhiteSpace(KoratError.Message(code)));
        Assert.False(string.IsNullOrWhiteSpace(KoratError.Code(code)));
    }
}
