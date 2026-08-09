using Korat.Domain.Entities;

namespace Korat.Protocol;

public enum PayloadLimitViolation
{
    None,
    PerMessage,
    SessionWarning,
    SessionHardLimit
}

public sealed class PayloadLimitTracker
{
    private readonly PayloadLimitPolicy _policy;
    private long _totalBytes;

    public PayloadLimitTracker(PayloadLimitPolicy? policy = null)
    {
        _policy = policy ?? new PayloadLimitPolicy();
    }

    public PayloadLimitPolicy Policy => _policy;
    public long TotalBytes => _totalBytes;
    public bool WarningRaised { get; private set; }

    public PayloadLimitViolation RecordFrame(long frameBytes)
    {
        if (frameBytes > _policy.PerMessageLimitBytes)
            return PayloadLimitViolation.PerMessage;

        _totalBytes += frameBytes;

        if (_totalBytes > _policy.SessionHardLimitBytes)
            return PayloadLimitViolation.SessionHardLimit;

        if (!WarningRaised && _totalBytes > _policy.SessionWarningBytes)
            WarningRaised = true;

        return PayloadLimitViolation.None;
    }
}

public static class PayloadLimits
{
    public static PayloadLimitPolicy DefaultPolicy { get; } = new();
}
