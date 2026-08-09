using Korat.Domain.Auth;
using Xunit;

namespace Korat.Auth.Tests;

public class CliTokenEntityTests
{
    [Fact]
    public void CliToken_constructs_with_full_scope_and_no_revocation()
    {
        var now = DateTimeOffset.UtcNow;
        var t = new CliToken
        {
            Id = Guid.NewGuid(),
            UserId = UserId.New(),
            TokenHash = "abc",
            Scope = "full",
            IssuedAt = now,
            ExpiresAt = now.AddDays(90),
            LastUsedAt = now,
        };
        Assert.Equal("full", t.Scope);
        Assert.Null(t.RevokedAt);
    }
}
