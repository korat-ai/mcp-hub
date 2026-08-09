using System.Net;
using Korat.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Shared-harness self-test (fable plan-review, Blocker 1): proves the façade seam does exactly
/// three things and nothing more — (1) a registered façade https:// URL is BOTH accepted by the
/// real SsrfGuard.ValidateUrl AND actually reaches the real loopback stub, from both the web host
/// and the silo container; (2) an UNREGISTERED plain http:// URL is still rejected exactly as
/// today (proves ValidateUrl itself was not weakened); (3) a non-façade https:// call is untouched
/// (the decorator is a pure passthrough for anything not registered).
/// </summary>
public sealed class OAuthLoopbackFacadeTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public void ValidateUrl_StillRejectsAnUnregisteredPlainHttpUrl()
    {
        // Regression proof: the façade seam must not weaken SsrfGuard.ValidateUrl itself.
        var error = Korat.Cloud.Web.Spaces.SsrfGuard.ValidateUrl("http://127.0.0.1:9/probe");
        Assert.NotNull(error);
        Assert.Contains("HTTPS", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisteredFacadeHost_PassesValidateUrl_AndReachesTheRealStub_FromTheWebHost()
    {
        using var stub = await StartTinyLoopbackStubAsync();

        var validateError = Korat.Cloud.Web.Spaces.SsrfGuard.ValidateUrl($"{stub.FacadeUrl}/ping");
        Assert.Null(validateError); // a well-formed https:// DNS-name URL on the default port

        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IOutboundHttpClientFactory>();
        using var client = factory.CreateClient("harness-self-test");
        var response = await client.GetAsync($"{stub.FacadeUrl}/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegisteredFacadeHost_ReachesTheRealStub_FromTheSiloContainer()
    {
        // HttpMcpProxyGrain's own outbound refresh calls resolve IOutboundHttpClientFactory from
        // the SILO's DI, a separate container from the web host (KoratTestHost.cs) — the façade
        // must work identically there, or Task 5's grain refresh tests cannot pass.
        using var stub = await StartTinyLoopbackStubAsync();

        var siloServices = fixture.Cluster.GetSiloServiceProvider();
        var factory = siloServices.GetRequiredService<IOutboundHttpClientFactory>();
        using var client = factory.CreateClient("harness-self-test-silo");
        var response = await client.GetAsync($"{stub.FacadeUrl}/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonFacadeHttpsHost_IsUntouchedPassthrough()
    {
        // Proves the decorator does not intercept traffic that was never registered — every
        // pre-existing SSRF/outbound test (BYOK/inference, etc.) is unaffected by this harness.
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IOutboundHttpClientFactory>();
        using var client = factory.CreateClient("harness-self-test-passthrough");

        // A non-façade, non-routable TEST-NET-1 https host: expect a real connection failure
        // (proves the request left the decorator untouched and hit the network), not a rewritten
        // loopback response.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://192.0.2.1/unused"));
    }

    private static async Task<TinyStub> StartTinyLoopbackStubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        var facadeHost = OAuthFacadeHostRegistry.Register(new Uri(app.Urls.First()));
        return new TinyStub(app, facadeHost);
    }

    private sealed class TinyStub(WebApplication app, string facadeHost) : IDisposable
    {
        public string FacadeUrl => $"https://{facadeHost}";
        public void Dispose()
        {
            OAuthFacadeHostRegistry.Unregister(facadeHost);
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
