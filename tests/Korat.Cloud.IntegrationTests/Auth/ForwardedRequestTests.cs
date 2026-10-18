using System.Net;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Korat.Cloud.IntegrationTests.Auth;

public sealed class ForwardedRequestTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData("10.42.0.9", true, "https", "203.0.113.17")]
    [InlineData("::ffff:10.42.0.9", true, "https", "203.0.113.17")]
    [InlineData("198.51.100.9", true, "http", "198.51.100.9")]
    [InlineData("172.16.0.9", true, "http", "172.16.0.9")]
    [InlineData("10.42.0.9", false, "http", "10.42.0.9")]
    public async Task OAuthChallenge_UsesOnlyTrustedForwardedSchemeAndIp(
        string peer, bool enabled, string expectedScheme, string expectedIp)
    {
        using var host = ForwardedTestHost.Create(fixture.Factory, enabled);
        // No provider request is made: this exercises the application's real challenge
        // pipeline through the point where it constructs the callback URL and cookies.
        var context = await host.Server.SendAsync(ctx =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            ctx.Request.Method = "GET";
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("localhost");
            ctx.Request.Path = "/signin/github";
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.17";
            ctx.Request.Headers["X-Forwarded-Host"] = "attacker.example.com";
            ctx.Request.Headers["Fly-Client-IP"] = "192.0.2.66";
        });

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        var destination = new Uri(context.Response.Headers.Location.ToString());
        Assert.Equal("github.com", destination.Host);
        var callback = new Uri(QueryHelpers.ParseQuery(destination.Query)["redirect_uri"].ToString());
        Assert.Equal(expectedScheme, callback.Scheme);
        Assert.Equal("localhost", callback.Host);
        Assert.Equal("/signin/github/callback", callback.AbsolutePath);
        Assert.Equal(expectedIp, RateLimiterRegistration.ResolveClientIp(context, enabled));
    }
}
