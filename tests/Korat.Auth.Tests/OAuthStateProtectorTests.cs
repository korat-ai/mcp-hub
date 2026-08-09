using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Korat.Auth.Tests;

public class OAuthStateProtectorTests
{
    private static OAuthStateProtector Build(TimeProvider? time = null) =>
        new(new EphemeralDataProtectionProvider(), time ?? TimeProvider.System);

    private static OAuthStatePayload NewPayload(TimeProvider? time = null) =>
        new("/app/grants", Guid.NewGuid(), (time ?? TimeProvider.System).GetUtcNow());

    [Fact]
    public void Protect_RoundTrips_PreservingNonceAndIssuedAt()
    {
        var p = Build();
        var original = NewPayload();
        var s = p.Protect(original);
        var unprotected = p.TryUnprotect(s);
        Assert.NotNull(unprotected);
        Assert.Equal(original.Nonce, unprotected!.Nonce);
        Assert.Equal(original.IssuedAt, unprotected.IssuedAt);
        Assert.Equal(original.ReturnUrl, unprotected.ReturnUrl);
    }

    [Fact]
    public void TryUnprotect_ReturnsPayload_WhenWithinTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var p = Build(clock);
        var payload = NewPayload(clock);
        var s = p.Protect(payload);

        clock.Advance(TimeSpan.FromMinutes(5));  // well within 10-minute TTL

        var result = p.TryUnprotect(s);
        Assert.NotNull(result);
        Assert.Equal(payload.Nonce, result!.Nonce);
    }

    [Fact]
    public void TryUnprotect_ReturnsNull_WhenIssuedAtBeyondTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var p = Build(clock);
        var s = p.Protect(NewPayload(clock));

        clock.Advance(TimeSpan.FromMinutes(11));  // beyond StateMaxAge (10 min)

        Assert.Null(p.TryUnprotect(s));
    }

    [Fact]
    public void TryUnprotect_ReturnsNull_ForTamperedValue()
    {
        var p = Build();
        var s = p.Protect(new OAuthStatePayload("/app/", Guid.NewGuid(), TimeProvider.System.GetUtcNow()));
        var tampered = s[..^4] + "AAAA";
        Assert.Null(p.TryUnprotect(tampered));
    }

    [Fact]
    public void TryUnprotect_ReturnsNull_ForGarbageInput()
    {
        var p = Build();
        Assert.Null(p.TryUnprotect("not-base64-data!"));
    }

    [Fact]
    public void TryUnprotect_ReturnsNull_ForForeignProtectorOutput()
    {
        var p1 = new OAuthStateProtector(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var p2 = new OAuthStateProtector(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var s = p1.Protect(new OAuthStatePayload("/app/", Guid.NewGuid(), TimeProvider.System.GetUtcNow()));
        Assert.Null(p2.TryUnprotect(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryUnprotect_ReturnsNull_ForEmptyOrNull(string? input)
    {
        var p = Build();
        Assert.Null(p.TryUnprotect(input!));
    }

    [Fact]
    public void TryUnprotect_ReturnsNull_ForOversizeInput()
    {
        var p = Build();
        var huge = new string('A', 5000);
        Assert.Null(p.TryUnprotect(huge));
    }
}
