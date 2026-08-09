using System.Net;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="RateLimiterRegistration.ResolveClientSubnet"/> — registration-flood
/// -DoS hardening item 3: the per-SUBNET partition key for the open DCR endpoint
/// (<c>POST /connect/register</c>). Defeats an attacker who rotates source IPs WITHIN one
/// subnet (a /24 = 256 IPs) to defeat the existing per-IP <c>DcrRegisterPolicy</c>.
///
/// Covers: IPv4 masked to /24, IPv6 masked to /48, IPv4-mapped-IPv6 normalized to plain IPv4
/// BEFORE masking (so an attacker can't dodge the /24 bucket by presenting v4 traffic as
/// <c>::ffff:a.b.c.d</c>), and the unparseable-IP fallback (deterministic, never throws, never
/// mints an unbounded partition).
/// </summary>
public class ResolveClientSubnetTests
{
    private static HttpContext BuildContext(string? remoteIp)
    {
        var ctx = new DefaultHttpContext();
        if (remoteIp is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return ctx;
    }

    [Fact]
    public void Ipv4_SameSlash24_SameKey()
    {
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("1.2.3.4"), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("1.2.3.240"), trustForwardedIp: false);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Ipv4_DifferentSlash24_DifferentKeys()
    {
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("1.2.3.4"), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("1.2.4.4"), trustForwardedIp: false);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Ipv6_SameSlash48_SameKey()
    {
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("2001:db8:1:aaaa::1"), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("2001:db8:1:ffff::9"), trustForwardedIp: false);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Ipv6_DifferentSlash48_DifferentKeys()
    {
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("2001:db8:1::1"), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("2001:db8:2::1"), trustForwardedIp: false);
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Ipv4MappedIpv6_NormalizesToPlainIpv4_SameKeyAsPlainIpv4()
    {
        // Guards the exact evasion the design targets: an attacker sending v4 traffic dressed
        // as ::ffff:a.b.c.d must NOT dodge the /24 bucket by landing in a much larger /48 v6
        // bucket instead.
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("::ffff:1.2.3.4"), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext("1.2.3.4"), trustForwardedIp: false);
        Assert.Equal(key1, key2);
        Assert.StartsWith("dcr-subnet4:", key1);
    }

    [Fact]
    public void UnparseableIp_FallsBackDeterministically_NoThrow()
    {
        // No RemoteIpAddress and trustForwardedIp=false -> ResolveClientIp returns "anon",
        // which does not parse as an IPAddress. Must still bucket deterministically, never throw.
        var key1 = RateLimiterRegistration.ResolveClientSubnet(BuildContext(remoteIp: null), trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolveClientSubnet(BuildContext(remoteIp: null), trustForwardedIp: false);
        Assert.Equal("dcr-subnet-ip:anon", key1);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void FlyClientIpHeader_TrustedFlagOn_UsesHeaderForSubnetKey()
    {
        var ctx = BuildContext(remoteIp: "10.0.0.1");
        ctx.Request.Headers["Fly-Client-IP"] = "9.9.9.5";
        var key = RateLimiterRegistration.ResolveClientSubnet(ctx, trustForwardedIp: true);
        Assert.Equal("dcr-subnet4:9.9.9.0/24", key);
    }

    [Fact]
    public void FlyClientIpHeader_TrustedFlagOff_HeaderIgnored_UsesRemoteIpAddress()
    {
        // Mirrors ResolveClientIpTests.FlagOff_HeaderPresent_UsesRemoteIpAddress: the subnet
        // key must not be spoofable via the forwarded header when the flag is off.
        var ctx = BuildContext(remoteIp: "10.0.0.1");
        ctx.Request.Headers["Fly-Client-IP"] = "9.9.9.5";
        var key = RateLimiterRegistration.ResolveClientSubnet(ctx, trustForwardedIp: false);
        Assert.Equal("dcr-subnet4:10.0.0.0/24", key);
    }
}
