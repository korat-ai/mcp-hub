using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cloud.IntegrationTests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.ContractTests;

/// <summary>
/// Increment 2, Task 4: POST/PATCH/reconnect against a real in-process Kestrel stub AS+resource
/// server (mirrors Increment 1's HttpMcpProxyGrainTests.StartStubMcpServerAsync pattern — see the
/// increment-2 plan's Grounding Note 9). Covers: POST oauth returns a connect action (not 400,
/// the increment-1 behavior this supersedes); PATCH authMode→oauth returns a connect action;
/// reconnect on a NeedsReauth server returns a fresh connect action; a GET never returns token
/// material; PATCH RemoteUrl on an oauth server clears the token + flips to NeedsReauth.
/// </summary>
public sealed class WebMcpServerOAuthContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task CreateHttpMcpServer_OAuthMode_ReturnsConnectAction_NotBadRequest()
    {
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-oauth-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NeedsReauth", body.GetProperty("status").GetString());
        var connect = body.GetProperty("connect");
        Assert.False(connect.TryGetProperty("error", out _) && connect.GetProperty("error").ValueKind != JsonValueKind.Null);
        var authorizeUrl = connect.GetProperty("authorizeUrl").GetString();
        Assert.StartsWith($"{stub.Url}/authorize", authorizeUrl);
        Assert.Contains("code_challenge=", authorizeUrl);
        Assert.Contains("state=", authorizeUrl);
    }

    [Fact]
    public async Task CreateHttpMcpServer_OAuthMode_NeverReturnsAnyTokenOrClientSecret()
    {
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-oauth-leak-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });

        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("dcr-secret", raw); // the stub's DCR client_secret, see StartStubAuthorizationServerAsync
    }

    [Fact]
    public async Task PatchHttpMcpServer_AuthModeToOAuth_ReturnsConnectAction()
    {
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-oauth-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "none",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { authMode = "oauth" });

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NeedsReauth", patched.GetProperty("status").GetString());
        Assert.True(patched.GetProperty("connect").GetProperty("authorizeUrl").GetString()!.Length > 0);
    }

    [Fact]
    public async Task PatchHttpMcpServer_RemoteUrlOnOAuthServer_ClearsTokenAndReturnsToNeedsReauth()
    {
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-edit-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        // Simulate a completed connect (Task 5/7 exercise the real callback E2E — here we only
        // need SOME token to exist so we can prove PATCH clears it).
        await SeedConnectedOAuthTokenAsync(id!);

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { remoteUrl = $"{stub.Url}/mcp-v2" });

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NeedsReauth", patched.GetProperty("status").GetString());
        var repository = fixture.Services.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
        Assert.Null(await repository.GetMcpServerOAuthTokenCiphertextAsync(new Korat.Domain.McpServerId(id!), default));
    }

    [Fact]
    public async Task PatchHttpMcpServer_AuthModeAwayFromOAuthBeforeConsent_RecoversToPublished()
    {
        // Finding 1 (Task 4 review, SHOULD-FIX): a freshly-created oauth server starts
        // NeedsReauth (never consented). If the owner PATCHes authMode to "none"/"bearer"/
        // "header" BEFORE completing consent, the server must not be permanently stranded at
        // NeedsReauth — before this fix, UpdateHttpCloudConfigAsync updated AuthMode but left
        // Status untouched, and both HttpMcpProxyGrain's dispatch gate (Status!=Published) and
        // NodeGatewayService's session-open gate (ServerNeedsReauth) would reject it forever;
        // only a manual disable→enable round-trip recovered it, never a re-PATCH.
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-oauth-strand-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        Assert.Equal("NeedsReauth", created.GetProperty("status").GetString()); // sanity: never consented

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { authMode = "none" });

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", patched.GetProperty("authMode").GetString());
        Assert.Equal("Published", patched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReconnectOAuthServer_ReturnsFreshConnectAction()
    {
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-reconnect-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var reconnectResp = await client.PostAsync($"/api/mcp-servers/{id}/reconnect", null);

        Assert.Equal(HttpStatusCode.OK, reconnectResp.StatusCode);
        var connect = await reconnectResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(connect.GetProperty("authorizeUrl").GetString()!.Length > 0);
    }

    [Fact]
    public async Task ReconnectNonOAuthServer_Returns400()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-noreconnect-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var reconnectResp = await client.PostAsync($"/api/mcp-servers/{id}/reconnect", null);

        Assert.Equal(HttpStatusCode.BadRequest, reconnectResp.StatusCode);
    }

    [Fact]
    public async Task ReconnectOAuthServer_WithSuppliedCredentials_UsesThemInTheAuthorizeUrl()
    {
        // SHOULD-FIX 5 (fable plan-review): /reconnect previously had no body at all →
        // BuildAsync(ownerClientId: null) unconditionally — so a manual-cred server (no DCR)
        // whose token doc was cleared by a PATCH RemoteUrl had no stored client AND no way to
        // supply one. Fix: /reconnect accepts an OPTIONAL {clientId, clientSecret} body, threaded
        // into BuildAsync exactly like create/patch already do. Proven here by observing the
        // OWNER-SUPPLIED client_id (not the stub's DCR-issued "dcr-client-1") in the returned
        // authorizeUrl, after clearing whatever client the initial create stored.
        using var stub = await StartStubAuthorizationServerAsync();
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-reconnect-manual-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var repository = fixture.Services.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
        await repository.ClearMcpServerOAuthTokenAsync(new Korat.Domain.McpServerId(id!), default);

        var reconnectResp = await client.PostAsJsonAsync($"/api/mcp-servers/{id}/reconnect",
            new { clientId = "owner-supplied-client", clientSecret = "owner-supplied-secret" });

        Assert.Equal(HttpStatusCode.OK, reconnectResp.StatusCode);
        var connect = await reconnectResp.Content.ReadFromJsonAsync<JsonElement>();
        var authorizeUrl = connect.GetProperty("authorizeUrl").GetString();
        Assert.Contains("client_id=owner-supplied-client", authorizeUrl);
    }

    // ── stub AS + resource server (mirrors HttpMcpProxyGrainTests.StartStubMcpServerAsync,
    // extended to serve the PRM/AS-metadata/DCR routes too — see Grounding Note 9). Registers
    // itself with the OAuthFacadeHostRegistry (see the "Shared Test Harness" section before this
    // task) — `Url` returns a façade https:// URL, not the raw loopback address, so
    // SsrfGuard.ValidateUrl genuinely approves it. ──

    private async Task<StubAuthorizationServer> StartStubAuthorizationServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var app = builder.Build();
        string url = string.Empty; // the FAÇADE base — assigned after StartAsync, see below

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{url}/.well-known/oauth-protected-resource\"";
        });
        app.MapGet("/.well-known/oauth-protected-resource", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"resource":"{{url}}/mcp","authorization_servers":["{{url}}"]}""");
        });
        app.MapGet("/.well-known/oauth-authorization-server", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""
                {"issuer":"{{url}}","authorization_endpoint":"{{url}}/authorize","token_endpoint":"{{url}}/token","registration_endpoint":"{{url}}/register"}
                """);
        });
        app.MapPost("/register", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"client_id":"dcr-client-1","client_secret":"dcr-secret-1"}""");
        });

        await app.StartAsync();
        var realLoopbackUrl = app.Urls.First(); // e.g. http://127.0.0.1:54231 — never handed to code under test
        var facadeHost = Korat.Cloud.IntegrationTests.OAuthFacadeHostRegistry.Register(new Uri(realLoopbackUrl));
        url = $"https://{facadeHost}"; // NOW assign the closures' captured variable — safe: no
        // request reaches these routes until a caller dials `url` below, well after this method returns.
        return new StubAuthorizationServer(app, url, facadeHost);
    }

    private sealed class StubAuthorizationServer(WebApplication app, string url, string facadeHost) : IDisposable
    {
        public string Url => url;
        public void Dispose()
        {
            Korat.Cloud.IntegrationTests.OAuthFacadeHostRegistry.Unregister(facadeHost);
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private async Task SeedConnectedOAuthTokenAsync(string serverId)
    {
        // Reality-over-plan fix (flagged inline by the plan's own Step 14 note): the shared
        // fixture.Services/fixture.Factory has NO envelope KEK configured (fail-closed by design
        // — see IEnvelopeCrypto's doc comment), so a plain EncryptAsync here would throw
        // InvalidOperationException. Use the SAME KEK-aware WithWebHostBuilder pattern
        // WebMcpServerContractTests.CreateKekAwareAuthenticatedClientAsync and
        // HttpMcpProxyGrainTests.CreateServerAsync already established, so this ciphertext is
        // written with a KEK the web host (and the silo, which shares ThreadGrainTestKek) can
        // actually decrypt with.
        var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));
        using var scope = kekFactory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
        var envelopeCrypto = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
        var server = await repository.GetMcpServerAsync(new Korat.Domain.McpServerId(serverId), default);
        var doc = new Korat.Cloud.Mcp.Oauth.McpOAuthTokenDocument(
            "at-seed", "rt-seed", DateTimeOffset.UtcNow.AddHours(1), "https://as.example.test/token",
            "https://as.example.test", "client-seed", "secret-seed");
        var ciphertext = await envelopeCrypto.EncryptAsync(
            server!.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.OAuthAad(server.Id),
            Korat.Cloud.Mcp.Oauth.McpOAuthTokenDocument.Serialize(doc), default);
        await repository.SetMcpServerOAuthTokenAsync(server.Id, ciphertext, default);
        await fixture.ClusterClient.GetGrain<Korat.GrainInterfaces.IMcpServerGrain>(serverId).MarkOAuthConnectedAsync();
    }
}
