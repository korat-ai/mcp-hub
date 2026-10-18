using System.Net;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

public sealed class TrustedProxyHeadersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0.0.0.0/96")]
    [InlineData("proxy.example.com")]
    [InlineData("10.42.0.0/99")]
    [InlineData("10.42.0.0/16,broken")]
    public void Enabled_WithMissingOrInvalidAllowlist_FailsBeforeServing(string? allowlist)
    {
        var config = Configuration(true, allowlist);
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKoratForwardedHeaders(config));
        Assert.Contains("TrustedProxies", error.Message);
    }

    [Fact]
    public void Disabled_DoesNotRequireProxyConfiguration()
    {
        Assert.False(new ServiceCollection().AddKoratForwardedHeaders(Configuration(false, null)));
    }

    [Theory]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED")]
    [InlineData("DOTNET_FORWARDEDHEADERS_ENABLED")]
    [InlineData("ForwardedHeaders_Enabled")]
    public void FrameworkWideTrustSwitch_IsRejected(string key)
    {
        var config = Configuration(true, "10.42.0.0/16");
        config[key] = "true";
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKoratForwardedHeaders(config));
    }

    [Fact]
    public void IndexedAndScalarAllowlists_AreBothSupported()
    {
        var indexed = Configuration(true, null);
        indexed["Korat:Cloud:TrustedProxies:0"] = "10.42.0.0/16";
        indexed["Korat:Cloud:TrustedProxies:1"] = "2001:db8::1";
        var scalar = Configuration(true, "10.42.0.0/16; 2001:db8::1");
        var first = OptionsFor(indexed);
        var second = OptionsFor(scalar);
        Assert.Equal(first.KnownIPNetworks, second.KnownIPNetworks);
        Assert.Equal(first.KnownProxies, second.KnownProxies);
        Assert.Equal(1, first.ForwardLimit);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, first.ForwardedHeaders);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, first.KnownProxies);
    }

    [Theory]
    [InlineData("10.42.0.0/16", "10.42.3.9")]
    [InlineData("10.42.0.0/16", "::ffff:10.42.3.9")]
    [InlineData("10.42.3.9", "10.42.3.9")]
    [InlineData("10.42.3.9", "::ffff:10.42.3.9")]
    [InlineData("2001:db8::/64", "2001:db8::9")]
    public async Task TrustedPeer_RestoresSchemeAndClientIp(string allowlist, string peer)
    {
        var ctx = Context(peer);
        await Process(ctx, allowlist);
        Assert.Equal("https", ctx.Request.Scheme);
        Assert.Equal("203.0.113.17", RateLimiterRegistration.ResolveClientIp(ctx, true));
        Assert.Equal("dcr-subnet4:203.0.113.0/24", RateLimiterRegistration.ResolveClientSubnet(ctx, true));
        // X-Forwarded-Host and legacy Fly headers cannot override validated properties.
        Assert.Equal("hub.example.com", ctx.Request.Host.Value);
    }

    [Theory]
    [InlineData("198.51.100.8")]
    [InlineData("172.16.0.8")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task UnknownPeer_CannotSpoofSchemeOrRateLimitIdentity(string peer)
    {
        var ctx = Context(peer);
        await Process(ctx, "10.42.0.0/16");
        Assert.Equal("http", ctx.Request.Scheme);
        Assert.Equal(peer, RateLimiterRegistration.ResolveClientIp(ctx, true));
        Assert.Equal("hub.example.com", ctx.Request.Host.Value);
    }

    [Fact]
    public async Task OneTrustedHop_DoesNotFollowAnAttackerSuppliedChain()
    {
        var ctx = Context("10.42.0.9");
        ctx.Request.Headers["X-Forwarded-For"] = "192.0.2.66, 203.0.113.17";
        ctx.Request.Headers["X-Forwarded-Proto"] = "http, https";
        await Process(ctx, "10.42.0.0/16");
        Assert.Equal("203.0.113.17", RateLimiterRegistration.ResolveClientIp(ctx, true));
        Assert.Equal("https", ctx.Request.Scheme);
    }

    private static DefaultHttpContext Context(string peer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("hub.example.com");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.17";
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";
        ctx.Request.Headers["X-Forwarded-Host"] = "attacker.example.com";
        ctx.Request.Headers["Fly-Client-IP"] = "192.0.2.66";
        return ctx;
    }

    private static Task Process(HttpContext context, string allowlist) =>
        new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            Options.Create(OptionsFor(Configuration(true, allowlist)))).Invoke(context);

    private static IConfigurationRoot Configuration(bool enabled, string? allowlist) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Korat:Cloud:TrustForwardedIp"] = enabled.ToString(),
            ["Korat:Cloud:TrustedProxies"] = allowlist,
        }).Build();

    private static ForwardedHeadersOptions OptionsFor(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        Assert.True(services.AddKoratForwardedHeaders(configuration));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }
}
