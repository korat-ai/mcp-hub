using System.Net;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Deferred-fix (SECURITY MINOR): the cookie path of rate-limit partitioning must mirror the
/// bearer fix — partition on the session cookie ONLY when the value has the server-issued
/// shape (Guid "N", 32 hex chars, exactly what CanonicalSigninHandler issues). A malformed or
/// absent cookie partitions by client IP, so crafted cookies can neither mint unbounded
/// partitions nor evade the per-IP bucket.
/// </summary>
public class RateLimitCookiePartitionTests
{
    private const string ValidCookie = "0123456789abcdef0123456789abcdef"; // Guid "N" shape

    private static HttpContext BuildContext(string? cookie, string remoteIp = "10.0.0.9")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (cookie is not null)
            ctx.Request.Headers.Cookie = $"{CanonicalSigninHandler.SessionCookieName}={cookie}";
        return ctx;
    }

    // ── ResolvePrincipalKey (owner-management / admin-ops policies) ─────────────────────────

    [Fact]
    public void PrincipalKey_ValidShapedCookie_PartitionsBySession()
    {
        var key = RateLimiterRegistration.ResolvePrincipalKey(BuildContext(ValidCookie), trustForwardedIp: false);
        Assert.Equal("s:" + ValidCookie, key);
    }

    [Theory]
    [InlineData("attacker-crafted-junk")]                       // wrong shape entirely
    [InlineData("0123456789abcdef0123456789abcde")]             // 31 chars — too short
    [InlineData("0123456789abcdef0123456789abcdef0")]           // 33 chars — too long
    [InlineData("zzzz456789abcdef0123456789abcdef")]            // 32 chars but non-hex
    public void PrincipalKey_MalformedCookie_PartitionsByIp_NotByCookie(string cookie)
    {
        var key = RateLimiterRegistration.ResolvePrincipalKey(BuildContext(cookie), trustForwardedIp: false);
        Assert.Equal("ip:10.0.0.9", key);
        Assert.DoesNotContain(cookie, key); // attacker-controlled value never becomes the key
    }

    [Fact]
    public void PrincipalKey_AbsentCookie_NoAuthHeader_PartitionsByIp()
    {
        var key = RateLimiterRegistration.ResolvePrincipalKey(BuildContext(cookie: null), trustForwardedIp: false);
        Assert.Equal("ip:10.0.0.9", key);
    }

    // ── ResolveSessionKey (per-session policies: signout / auth-me / approve / …) ───────────

    [Fact]
    public void SessionKey_ValidShapedCookie_PartitionsBySession()
    {
        var key = RateLimiterRegistration.ResolveSessionKey(BuildContext(ValidCookie), trustForwardedIp: false);
        Assert.Equal(ValidCookie, key);
    }

    [Theory]
    [InlineData("not-a-session-id")]
    [InlineData("")]
    public void SessionKey_MalformedOrEmptyCookie_PartitionsByIp(string cookie)
    {
        var key = RateLimiterRegistration.ResolveSessionKey(BuildContext(cookie), trustForwardedIp: false);
        Assert.Equal("10.0.0.9", key);
    }

    [Fact]
    public void SessionKey_AbsentCookie_PartitionsByIp()
    {
        var key = RateLimiterRegistration.ResolveSessionKey(BuildContext(cookie: null), trustForwardedIp: false);
        Assert.Equal("10.0.0.9", key);
    }

    // ── ResolveTelegramBindingKey (PR-2 Task 9 review fix: per-BINDING webhook limit) ────────

    [Fact]
    public void TelegramKey_ValidShapedBindingId_PartitionsByBinding_NotByIp()
    {
        var bindingId = Guid.NewGuid().ToString("N"); // ChannelBindingId.New() shape
        var ctx = BuildContext(cookie: null);
        ctx.Request.RouteValues["bindingId"] = bindingId;

        var key = RateLimiterRegistration.ResolveTelegramBindingKey(ctx, trustForwardedIp: false);
        Assert.Equal("tb:" + bindingId, key);

        // Two bindings hit from the same (Telegram) IP land in DIFFERENT partitions — a spammed
        // chat can no longer starve every other binding on the platform.
        var ctx2 = BuildContext(cookie: null);
        ctx2.Request.RouteValues["bindingId"] = Guid.NewGuid().ToString("N");
        Assert.NotEqual(key, RateLimiterRegistration.ResolveTelegramBindingKey(ctx2, trustForwardedIp: false));
    }

    [Theory]
    [InlineData("attacker-crafted-junk")]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")]   // 31 chars
    [InlineData("zzzz456789abcdef0123456789abcdef")]  // 32 chars, non-hex
    public void TelegramKey_MalformedBindingId_PartitionsByIp_NotByRouteValue(string bindingId)
    {
        var ctx = BuildContext(cookie: null);
        ctx.Request.RouteValues["bindingId"] = bindingId;

        var key = RateLimiterRegistration.ResolveTelegramBindingKey(ctx, trustForwardedIp: false);
        Assert.Equal("tb-ip:10.0.0.9", key);
        if (bindingId.Length > 0)
            Assert.DoesNotContain(bindingId, key); // attacker value never becomes the key
    }

    [Fact]
    public void TelegramKey_AbsentRouteValue_PartitionsByIp()
    {
        var key = RateLimiterRegistration.ResolveTelegramBindingKey(
            BuildContext(cookie: null), trustForwardedIp: false);
        Assert.Equal("tb-ip:10.0.0.9", key);
    }

    // ── Shape check itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_AcceptsExactlyWhatCanonicalSigninHandlerIssues()
    {
        // CanonicalSigninHandler appends session.Id.ToString("N") — every such value must pass.
        Assert.True(RateLimiterRegistration.IsValidSessionCookieShape(Guid.NewGuid().ToString("N")));
        Assert.False(RateLimiterRegistration.IsValidSessionCookieShape(null));
        Assert.False(RateLimiterRegistration.IsValidSessionCookieShape(""));
        Assert.False(RateLimiterRegistration.IsValidSessionCookieShape(Guid.NewGuid().ToString("D"))); // dashed
    }
}
