using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 2 (spec §Pillar C RFC 9728): the path-scoped PRM document + the
/// 401 challenge that points MCP clients at it — the exact shape Korat's OWN
/// McpOAuthDiscoveryService parses client-side (resource_metadata="…", dogfood) — plus CORS
/// on the well-known documents (spec §Confidentiality).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class ProtectedResourceMetadataTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}
        """;

    [Fact]
    public async Task Prm_ReturnsResourceAndAuthorizationServer_ForAnySegment_NoEnumeration()
    {
        var client = fixture.Factory.CreateClient();
        // Deliberately an UNKNOWN segment: the PRM document is derived purely from the path
        // (no Space resolution) so anonymous probing can never enumerate real slugs.
        var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp/no-such-space");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("http://localhost/mcp/no-such-space", doc["resource"]!.GetValue<string>());
        var servers = doc["authorization_servers"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Single(servers);
        Assert.Equal("http://localhost", servers[0].TrimEnd('/'));
        var scopes = doc["scopes_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("korat:mcp", scopes);
    }

    [Fact]
    public async Task McpWithoutBearer_401_CarriesResourceMetadataChallenge()
    {
        var seeded = await fixture.SeedUserAsync(
            $"prm-challenge-{Guid.NewGuid():N}@example.com", "PRM Challenge");
        var client = fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{seeded.SpaceId}")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains(
            $"resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp/{seeded.SpaceId}\"",
            challenge);
        Assert.StartsWith("Bearer", challenge);
    }

    [Fact]
    public async Task McpWithGarbageBearer_401_CarriesChallenge_Too()
    {
        var seeded = await fixture.SeedUserAsync(
            $"prm-garbage-{Guid.NewGuid():N}@example.com", "PRM Garbage");
        var client = fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{seeded.SpaceId}")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task WellKnownDocuments_CarryPermissiveCors_AndAnswerPreflight()
    {
        var client = fixture.Factory.CreateClient();

        // Cross-origin GET on the AS metadata (served by OpenIddict inside UseAuthentication —
        // which is exactly why this is middleware, not an endpoint CORS policy).
        var get = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        get.Headers.Add("Origin", "https://claude.ai");
        var getResponse = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("*", getResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        // Preflight on the PRM document.
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/.well-known/oauth-protected-resource/mcp/any");
        preflight.Headers.Add("Origin", "https://claude.ai");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal("*", preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
