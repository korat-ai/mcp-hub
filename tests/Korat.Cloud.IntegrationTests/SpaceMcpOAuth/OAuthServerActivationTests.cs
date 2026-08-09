using System.Net;
using System.Text.Json.Nodes;
using Korat.Cloud.Web.Oauth;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 1: the OpenIddict AS is ACTIVE — RFC 8414 metadata is served at
/// BOTH well-known paths (OpenIddict 7.5 default — verified grounding #1), advertises the
/// auth-code + refresh grants, S256 PKCE, and the dedicated korat:mcp scope (SF-7); and the
/// ONE pre-registered public/PKCE MCP client upserts idempotently.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class OAuthServerActivationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task AsMetadata_ServedAtOauthAuthorizationServerPath_AdvertisesMcpEssentials()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.EndsWith("/connect/authorize", doc["authorization_endpoint"]!.GetValue<string>());
        Assert.EndsWith("/connect/token", doc["token_endpoint"]!.GetValue<string>());
        var grants = doc["grant_types_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("authorization_code", grants);
        Assert.Contains("refresh_token", grants);
        var pkce = doc["code_challenge_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("S256", pkce);
        var scopes = doc["scopes_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(KoratOAuthConstants.McpScope, scopes);
    }

    [Fact]
    public async Task AsMetadata_AlsoServedAtOpenIdConfigurationPath()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.EndsWith("/connect/token", doc["token_endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreRegisteredClient_UpsertsIdempotently_PublicPkceMcpScopeOnly()
    {
        await fixture.EnsureOAuthClientAsync("http://127.0.0.1:45123/callback");

        string firstId;
        using (var scope = fixture.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var app = await manager.FindByClientIdAsync(KoratOAuthConstants.DefaultClientId);
            Assert.NotNull(app);
            firstId = (await manager.GetIdAsync(app!))!;
        }

        // Second upsert (changed redirect URI) converges the SAME row — never a duplicate.
        await fixture.EnsureOAuthClientAsync("http://127.0.0.1:45999/other-callback");
        using (var scope = fixture.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var app = await manager.FindByClientIdAsync(KoratOAuthConstants.DefaultClientId);
            Assert.NotNull(app);
            Assert.Equal(firstId, await manager.GetIdAsync(app!));
        }
    }

    [Fact]
    public async Task Seeder_WithNoRedirectUrisConfigured_SkipsWithoutThrowing()
    {
        // Default options carry NO redirect URIs (the operator must configure them for a real
        // deploy) — the seeder must skip cleanly, never create an unusable/invalid client.
        using var scope = fixture.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var options = new Korat.Cloud.Web.Oauth.SpaceMcpOAuthOptions { ClientId = $"skip-{Guid.NewGuid():N}" };
        await SpaceMcpOAuthClientSeeder.UpsertAsync(manager, options, CancellationToken.None);
        Assert.Null(await manager.FindByClientIdAsync(options.ClientId));
    }
}
