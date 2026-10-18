using System.Net;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

public sealed class SpaAntiforgeryTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData("/app")]
    [InlineData("/app/")]
    [InlineData("/app/index.html")]
    [InlineData("/app/grants")]
    [InlineData("/app/signin")]
    public async Task ShellLoad_WithOnlySessionCookie_IssuesUsableXsrfPair(string path)
    {
        var root = Directory.CreateTempSubdirectory("korat-spa-csrf-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "app", "assets"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "app", "index.html"),
                "<!doctype html><title>Test shell</title>");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "app", "assets", "test.js"), "// test asset");
            await using var factory = fixture.Factory.WithWebHostBuilder(b => b.UseWebRoot(root.FullName));
            var user = await fixture.SeedUserAsync($"shell-csrf-{Guid.NewGuid():N}@test.local", "Owner");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var server = (await space.PublishMcpServerAsync(NodeId.New(), "Docs", "echo", ""))!;
            using var scope = factory.Services.CreateScope();
            var session = await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .CreateAsync(user.UserId, "browser-restart", "127.0.0.1", default);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false,
            });
            var sessionCookie = $"{CanonicalSigninHandler.SessionCookieName}={session.Id:N}";
            client.DefaultRequestHeaders.Add("Cookie", sessionCookie);

            using var shell = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Equal("text/html", shell.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Test shell", await shell.Content.ReadAsStringAsync());
            Assert.True(shell.Headers.TryGetValues("Set-Cookie", out var headers), "Shell must bootstrap CSRF cookies.");
            var cookies = headers!.Select(h => h.Split(';')[0]).ToArray();
            Assert.Contains(cookies, c => c.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal));
            var requestToken = Assert.Single(cookies, c => c.StartsWith("__Host-XSRF-TOKEN=", StringComparison.Ordinal))
                ["__Host-XSRF-TOKEN=".Length..];
            using var mutation = new HttpRequestMessage(HttpMethod.Post, $"/api/mcp-servers/{server.Id.Value}/disable");
            mutation.Headers.Add("Cookie", string.Join("; ", cookies.Prepend(sessionCookie)));
            mutation.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(requestToken));
            using var response = await client.SendAsync(mutation);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Assets stay public and cheap: no session/CSRF bootstrap on each JS request.
            using var asset = await client.GetAsync("/app/assets/test.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("// test asset", await asset.Content.ReadAsStringAsync());
            Assert.False(asset.Headers.Contains("Set-Cookie"));
        }
        finally { root.Delete(recursive: true); }
    }
}
