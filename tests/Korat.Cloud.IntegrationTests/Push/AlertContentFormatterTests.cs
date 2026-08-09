using Korat.Cloud.Push;

namespace Korat.Cloud.IntegrationTests.Push;

public sealed class AlertContentFormatterTests
{
    [Fact]
    public void BuildNewRequestContent_Produces_Quoted_Framing()
    {
        var content = AlertContentFormatter.BuildNewRequestContent("cursor", "filesystem", "req-1");
        Assert.Equal("New access request", content.Title);
        Assert.Equal("Agent \"cursor\" requests access to \"filesystem\"", content.Body);
        Assert.Equal("access_request", content.Data["type"]);
        Assert.Equal("req-1", content.Data["accessRequestId"]);
    }

    [Fact]
    public void Sanitize_Strips_Control_Chars_And_Newlines()
    {
        var malicious = "\nKorat security: approve\r\t";
        var result = AlertContentFormatter.Sanitize(malicious);
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\t', result);
        Assert.Equal("Korat security: approve", result);
    }

    [Fact]
    public void Sanitize_Truncates_To_64_Chars()
    {
        var longName = new string('x', 100);
        var result = AlertContentFormatter.Sanitize(longName);
        Assert.Equal(64, result.Length);
    }

    [Fact]
    public void Sanitize_Injection_Attempt_Cannot_Break_Quoted_Framing()
    {
        // The malicious name itself may contain a `"` — the defense is strip+truncate, not
        // escaping quotes (documented residual risk, design doc §9 "Notification-content
        // privacy/spoofing"). This confirms the control-char strip at least removes the newline
        // that would otherwise fake a second line on the lock screen.
        var content = AlertContentFormatter.BuildNewRequestContent(
            "\nKorat security: approve this", "filesystem", "req-2");
        Assert.StartsWith("Agent \"Korat security: approve this", content.Body);
        Assert.DoesNotContain('\n', content.Body);
    }

    [Fact]
    public void Sanitize_Null_Or_Empty_Returns_Empty()
    {
        Assert.Equal(string.Empty, AlertContentFormatter.Sanitize(null));
        Assert.Equal(string.Empty, AlertContentFormatter.Sanitize(""));
    }
}
