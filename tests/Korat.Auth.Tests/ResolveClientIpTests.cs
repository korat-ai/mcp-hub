using System.Net;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Tests for RateLimiterRegistration.ResolveClientIp:
///   (a) flag off → Fly-Client-IP header ignored even if present
///   (b) flag on  → Fly-Client-IP header used when present
///   (c) flag on, header absent → falls back to RemoteIpAddress
/// </summary>
public class ResolveClientIpTests
{
    private static HttpContext BuildContext(string? flyHeader, string? remoteIp)
    {
        var ctx = new DefaultHttpContext();
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (flyHeader is not null)
            ctx.Request.Headers["Fly-Client-IP"] = flyHeader;
        return ctx;
    }

    [Fact]
    public void FlagOff_HeaderPresent_UsesRemoteIpAddress()
    {
        // When TrustForwardedIp=false the Fly-Client-IP header must be ignored
        // even if a spoofed value is present — preventing rate-limit bypass.
        var ctx = BuildContext(flyHeader: "1.2.3.4", remoteIp: "10.0.0.1");
        var result = RateLimiterRegistration.ResolveClientIp(ctx, trustForwardedIp: false);
        Assert.Equal("10.0.0.1", result);
    }

    [Fact]
    public void FlagOn_HeaderPresent_UsesHeaderValue()
    {
        // When TrustForwardedIp=true the Fly-Client-IP header wins over RemoteIpAddress
        // (RemoteIpAddress would be the Fly edge proxy address, not the real client).
        var ctx = BuildContext(flyHeader: "5.6.7.8", remoteIp: "10.0.0.1");
        var result = RateLimiterRegistration.ResolveClientIp(ctx, trustForwardedIp: true);
        Assert.Equal("5.6.7.8", result);
    }

    [Fact]
    public void FlagOn_HeaderAbsent_FallsBackToRemoteIpAddress()
    {
        // When TrustForwardedIp=true but no Fly-Client-IP header is present,
        // RemoteIpAddress is used as the fallback.
        var ctx = BuildContext(flyHeader: null, remoteIp: "10.0.0.2");
        var result = RateLimiterRegistration.ResolveClientIp(ctx, trustForwardedIp: true);
        Assert.Equal("10.0.0.2", result);
    }

    [Fact]
    public void FlagOff_NoRemoteIp_ReturnsAnon()
    {
        // Defensive: when neither is available (unusual in practice) return "anon"
        // so a rate-limit partition key is always set.
        var ctx = BuildContext(flyHeader: null, remoteIp: null);
        var result = RateLimiterRegistration.ResolveClientIp(ctx, trustForwardedIp: false);
        Assert.Equal("anon", result);
    }
}
