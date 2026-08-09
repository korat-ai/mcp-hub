using Korat.Domain.Auth;

namespace Korat.Domain.Tests.Auth;

public class UserIdTests
{
    [Fact]
    public void New_ReturnsDistinctIds()
    {
        var a = UserId.New();
        var b = UserId.New();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Parse_RoundTripsViaToString()
    {
        var original = UserId.New();
        var parsed = UserId.Parse(original.ToString());
        Assert.Equal(original, parsed);
    }
}
