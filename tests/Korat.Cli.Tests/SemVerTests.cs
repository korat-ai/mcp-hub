using Korat.Cli.Util;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="SemVer.IsNewer"/> covering numeric ordering,
/// pre-release ordering, build-metadata stripping, leading 'v', and garbage inputs.
/// </summary>
public class SemVerTests
{
    [Theory]
    [InlineData("0.3.0",      "0.2.8",     true)]   // newer patch series
    [InlineData("0.2.8",      "0.2.8",     false)]  // equal
    [InlineData("0.2.7",      "0.2.8",     false)]  // older
    [InlineData("1.0.0",      "0.9.9",     true)]   // major bump
    [InlineData("0.2.8",      "0.2.8-dev.1", true)] // release > pre-release
    [InlineData("0.2.8-dev.1","0.2.8",     false)]  // pre-release < release
    [InlineData("0.2.8+abc",  "0.2.8",     false)]  // build metadata ignored → equal
    [InlineData("x",          "0.2.8",     false)]  // garbage → equal ⇒ not newer
    [InlineData("v0.3.0",     "0.2.8",     true)]   // leading 'v' stripped
    public void IsNewer_returns_expected(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, SemVer.IsNewer(candidate, current));
    }
}
