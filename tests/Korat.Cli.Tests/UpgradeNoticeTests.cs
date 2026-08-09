using Korat.Cli.Util;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="UpgradeNotice.Format"/> and <see cref="UpgradeNotice.MaybeWarn"/>.
/// </summary>
public class UpgradeNoticeTests
{
    // ── Format ──────────────────────────────────────────────────────────────

    [Fact]
    public void Format_returns_message_containing_both_versions_and_upgrade_hint()
    {
        var msg = UpgradeNotice.Format("0.3.0", "0.2.8");
        Assert.NotNull(msg);
        Assert.Contains("0.3.0", msg);
        Assert.Contains("0.2.8", msg);
        Assert.Contains("korat upgrade", msg);
    }

    [Fact]
    public void Format_returns_null_when_current_is_empty()
    {
        Assert.Null(UpgradeNotice.Format("", "0.2.8"));
    }

    [Fact]
    public void Format_returns_null_when_current_equals_running()
    {
        Assert.Null(UpgradeNotice.Format("0.2.8", "0.2.8"));
    }

    [Fact]
    public void Format_returns_null_when_current_is_older_than_running()
    {
        Assert.Null(UpgradeNotice.Format("0.2.7", "0.2.8"));
    }

    // ── MaybeWarn ───────────────────────────────────────────────────────────

    [Fact]
    public void MaybeWarn_writes_once_when_upgrade_available()
    {
        UpgradeNotice.ResetForTests();
        var writer = new StringWriter();

        UpgradeNotice.MaybeWarn("0.3.0", "0.2.8", writer);

        var output = writer.ToString();
        Assert.Contains("0.3.0", output);
        Assert.Contains("korat upgrade", output);
    }

    [Fact]
    public void MaybeWarn_writes_at_most_once_per_process()
    {
        UpgradeNotice.ResetForTests();
        var writer = new StringWriter();

        UpgradeNotice.MaybeWarn("0.3.0", "0.2.8", writer);
        UpgradeNotice.MaybeWarn("0.3.0", "0.2.8", writer);
        UpgradeNotice.MaybeWarn("0.3.0", "0.2.8", writer);

        // Only one line should have been written
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public void MaybeWarn_writes_nothing_when_no_upgrade_available()
    {
        UpgradeNotice.ResetForTests();
        var writer = new StringWriter();

        UpgradeNotice.MaybeWarn("0.2.8", "0.2.8", writer);

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void MaybeWarn_writes_nothing_when_current_is_empty()
    {
        UpgradeNotice.ResetForTests();
        var writer = new StringWriter();

        UpgradeNotice.MaybeWarn("", "0.2.8", writer);

        Assert.Equal("", writer.ToString());
    }
}
