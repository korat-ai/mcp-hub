using Korat.Domain;
using Xunit;

namespace Korat.Domain.Tests;

public class McpServerHttpCloudConfigTests
{
    [Fact]
    public void IsHttpCloud_recognizes_only_the_new_literal()
    {
        Assert.True(McpServerTransports.IsHttpCloud("http_cloud"));
        Assert.False(McpServerTransports.IsHttpCloud("Stdio"));      // legacy default
        Assert.False(McpServerTransports.IsHttpCloud("stdio_node")); // not a stored literal — "Stdio" is
        Assert.False(McpServerTransports.IsHttpCloud(""));
    }

    // Increment 2: oauth is now a valid, accepted auth mode (Increment 1 rejected it — see the
    // Grounding Notes in the increment-2 plan for why this INlineData flips from false to true).
    [Theory]
    [InlineData("none", true)]
    [InlineData("bearer", true)]
    [InlineData("header", true)]
    [InlineData("oauth", true)]
    [InlineData("bogus", false)]
    public void AuthModes_IsValid_matches_scope(string mode, bool expected)
    {
        Assert.Equal(expected, McpServerAuthModes.IsValid(mode));
    }

    [Theory]
    [InlineData("oauth", true)]
    [InlineData("none", false)]
    [InlineData("bearer", false)]
    [InlineData("header", false)]
    [InlineData(null, false)]
    public void IsOAuth_matches_only_the_oauth_literal(string? mode, bool expected)
    {
        Assert.Equal(expected, McpServerAuthModes.IsOAuth(mode));
    }
}
