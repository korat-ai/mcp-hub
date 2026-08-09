using Korat.Domain;
using Xunit;

namespace Korat.Domain.Tests;

public class SessionReaperRulesTests
{
    private static readonly TimeSpan Grace = SessionReaperRules.ReapGrace;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static DateTimeOffset Fresh => Now - TimeSpan.FromSeconds(10);
    private static DateTimeOffset Stale => Now - Grace - TimeSpan.FromMinutes(1);

    [Fact]
    public void Active_with_both_nodes_fresh_is_not_reapable()
        => Assert.False(SessionReaperRules.IsReapable(SessionStatus.Active, Fresh, Fresh, Now, Grace));

    [Fact]
    public void Active_with_client_stale_is_reapable()
        => Assert.True(SessionReaperRules.IsReapable(SessionStatus.Active, Stale, Fresh, Now, Grace));

    [Fact]
    public void Active_with_publisher_stale_is_reapable()
        => Assert.True(SessionReaperRules.IsReapable(SessionStatus.Active, Fresh, Stale, Now, Grace));

    [Fact]
    public void Opening_with_both_stale_is_reapable()
        => Assert.True(SessionReaperRules.IsReapable(SessionStatus.Opening, Stale, Stale, Now, Grace));

    [Fact]
    public void Active_with_null_lastseen_is_reapable()
        => Assert.True(SessionReaperRules.IsReapable(SessionStatus.Active, null, Fresh, Now, Grace));

    [Fact]
    public void Within_grace_stale_is_not_reapable()
    {
        var withinGrace = Now - Grace + TimeSpan.FromSeconds(30); // older than 90s but inside grace
        Assert.False(SessionReaperRules.IsReapable(SessionStatus.Active, withinGrace, Fresh, Now, Grace));
    }

    [Theory]
    [InlineData(SessionStatus.Closed)]
    [InlineData(SessionStatus.Failed)]
    public void Terminal_status_is_never_reapable(SessionStatus status)
        => Assert.False(SessionReaperRules.IsReapable(status, null, null, Now, Grace));

    [Fact]
    public void Grace_exceeds_presence_stale_threshold()
        => Assert.True(SessionReaperRules.ReapGrace > NodePresenceRules.StaleThreshold);
}
