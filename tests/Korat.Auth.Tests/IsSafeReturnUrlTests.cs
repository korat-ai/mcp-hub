using Korat.Cloud.Web.Auth.Security;

namespace Korat.Auth.Tests;

public class IsSafeReturnUrlTests
{
    [Theory]
    [InlineData("/app/")]
    [InlineData("/app/grants")]
    public void Check_AcceptsKnownPrefixes(string url) => Assert.True(IsSafeReturnUrl.Check(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("/%2f%2fevil.com")]
    [InlineData("/app/../../external")]
    [InlineData("/app/\r\nLocation: https://evil.com")]
    [InlineData("/app/\tinjected")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    [InlineData("/not-in-prefix-set")]
    // web-M4 minor: /api/* and /signin/* removed from allowed prefixes — a returnUrl that
    // resolves to a JSON endpoint or the sign-in flow itself is not a valid post-auth page.
    [InlineData("/api/space")]
    [InlineData("/signin/github")]
    public void Check_RejectsUnsafeInputs(string? url) => Assert.False(IsSafeReturnUrl.Check(url));

    [Theory]
    [InlineData("/app/\u0085injected")]
    [InlineData("/app/\u2028injected")]
    [InlineData("/app/\u2029injected")]
    public void Check_RejectsUnicodeLineSeparators(string url) => Assert.False(IsSafeReturnUrl.Check(url));

    [Fact]
    public void Check_RejectsOversizeInput()
    {
        var huge = "/app/" + new string('a', 4000);
        Assert.False(IsSafeReturnUrl.Check(huge));
    }

    [Fact]
    public void Check_AcceptsAt2048CharBoundary()
    {
        const string prefix = "/app/";
        var url = prefix + new string('a', 2048 - prefix.Length);
        Assert.Equal(2048, url.Length);
        Assert.True(IsSafeReturnUrl.Check(url));
    }

    [Fact]
    public void Check_RejectsAt2049Chars()
    {
        const string prefix = "/app/";
        var url = prefix + new string('a', 2049 - prefix.Length);
        Assert.Equal(2049, url.Length);
        Assert.False(IsSafeReturnUrl.Check(url));
    }
}
