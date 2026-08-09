using Korat.Domain.Entities;
using Korat.Protocol;

namespace Korat.Protocol.Tests;

public class PayloadLimitTests
{
    [Fact]
    public void DefaultPolicy_MatchesProductDefaults()
    {
        var policy = PayloadLimits.DefaultPolicy;
        Assert.Equal(PayloadLimitPolicy.DefaultPerMessageBytes, policy.PerMessageLimitBytes);
        Assert.Equal(PayloadLimitPolicy.DefaultSessionWarningBytes, policy.SessionWarningBytes);
        Assert.Equal(PayloadLimitPolicy.DefaultSessionHardLimitBytes, policy.SessionHardLimitBytes);
    }

    [Fact]
    public void RecordFrame_ExceedsPerMessage_ReturnsViolation()
    {
        var tracker = new PayloadLimitTracker();
        var result = tracker.RecordFrame(PayloadLimitPolicy.DefaultPerMessageBytes + 1);
        Assert.Equal(PayloadLimitViolation.PerMessage, result);
    }

    [Fact]
    public void RecordFrame_CrossesWarning_SetsFlag()
    {
        var tracker = new PayloadLimitTracker();
        var chunk = PayloadLimitPolicy.DefaultPerMessageBytes;
        tracker.RecordFrame(chunk);
        tracker.RecordFrame(chunk);
        tracker.RecordFrame(chunk);
        tracker.RecordFrame(chunk);
        Assert.True(tracker.WarningRaised);
    }
}
