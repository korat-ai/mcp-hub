using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Web.Oauth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2b, Tasks 2+4: the open, bounded RFC 7591 Dynamic Client Registration endpoint
/// (POST /connect/register) and its discovery advertisement. Open DCR is the single riskiest new
/// exposure in the whole feature — these tests pin the RFC 7591 shape (no client_secret), the
/// least-privilege confinement (korat:mcp only, never identity scopes), the redirect-URI policy
/// that is the primary anti-open-redirect defense, and the plan-review corrections applied
/// alongside Task 4: MF-1 (bounded body/redirect-count/client-name reads — a naive
/// Request.ContentLength check is bypassable under chunked transfer-encoding), SF-1 (CORS on
/// the endpoint), and SF-3 (an "unverified" consent badge for DCR-registered clients).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class DcrRegistrationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly JsonObject BaseRequest = new()
    {
        ["client_name"] = "Test MCP Client",
        ["redirect_uris"] = new JsonArray { "http://127.0.0.1:45123/callback" },
        ["token_endpoint_auth_method"] = "none",
        ["grant_types"] = new JsonArray { "authorization_code", "refresh_token" },
        ["response_types"] = new JsonArray { "code" },
        ["scope"] = "korat:mcp",
    };

    private static HttpContent Body(JsonObject overrides)
    {
        var obj = JsonNode.Parse(BaseRequest.ToJsonString())!.AsObject();
        foreach (var kv in overrides) obj[kv.Key] = kv.Value is null ? null : JsonNode.Parse(kv.Value.ToJsonString());
        return new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Metadata_AdvertisesRegistrationEndpoint()
    {
        var client = fixture.Factory.CreateClient();
        var doc = JsonNode.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"))!;
        var registrationEndpoint = doc["registration_endpoint"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(registrationEndpoint), "registration_endpoint missing from AS metadata");
        Assert.EndsWith("/connect/register", registrationEndpoint);
    }

    [Fact]
    public async Task Register_ReturnsClientId_NoSecret_ScopeMcpOnly()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync("/connect/register", Body(new JsonObject()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        var clientId = doc["client_id"]!.GetValue<string>();
        Assert.StartsWith("dcr_", clientId);
        Assert.NotNull(doc["client_id_issued_at"]);
        Assert.Equal("korat:mcp", doc["scope"]!.GetValue<string>());
        Assert.False(doc.ContainsKey("client_secret"), "a PUBLIC DCR client must never receive a client_secret");
        Assert.Equal("none", doc["token_endpoint_auth_method"]!.GetValue<string>());

        // The persisted client is the SAME least-privilege shape as the pre-registered one:
        // public, korat:mcp-only, PKCE-required — never an identity scope.
        using var scope = fixture.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(clientId);
        Assert.NotNull(app);
        var permissions = await manager.GetPermissionsAsync(app!);
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.Scope + "korat:mcp", permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Prefixes.Scope + "openid", permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Prefixes.Scope + "email", permissions);
        var requirements = await manager.GetRequirementsAsync(app!);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, requirements);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, await manager.GetClientTypeAsync(app!));

        // Task 4 (marker stamped by DcrEndpoints, consumed by SF-3's consent badge + Task 6's
        // TTL sweep): the DCR marker + registered-at Properties are present on the persisted row.
        var properties = await manager.GetPropertiesAsync(app!);
        Assert.True(properties.ContainsKey(KoratOAuthConstants.DcrMarkerProperty));
        Assert.True(properties.ContainsKey(KoratOAuthConstants.DcrRegisteredAtProperty));
    }

    [Fact]
    public async Task Register_RequestingIdentityScope_StillRegistersMcpOnly()
    {
        // SF-7: even if the client ASKS for openid, the registered client is korat:mcp-only —
        // it cannot self-grant more. The response reflects the granted scope, not the requested.
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync("/connect/register",
            Body(new JsonObject { ["scope"] = "openid email profile korat:mcp" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("korat:mcp", doc["scope"]!.GetValue<string>());

        using var scope = fixture.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(doc["client_id"]!.GetValue<string>());
        var permissions = await manager.GetPermissionsAsync(app!);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Prefixes.Scope + "openid", permissions);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/cb", HttpStatusCode.Created)]
    [InlineData("http://[::1]:5000/cb", HttpStatusCode.Created)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", HttpStatusCode.Created)]
    [InlineData("http://evil.com/cb", HttpStatusCode.BadRequest)]
    [InlineData("http://localhost:5000/cb", HttpStatusCode.Created)]     // real MCP clients (Claude Code/Cursor) register localhost loopback
    [InlineData("http://localhost.evil.com/cb", HttpStatusCode.BadRequest)] // but the "localhost" match is EXACT — suffix bait still rejected
    [InlineData("https://*.evil.com/cb", HttpStatusCode.BadRequest)]
    [InlineData("myapp://callback", HttpStatusCode.BadRequest)]
    public async Task Register_RedirectUriPolicy_EnforcedEndToEnd(string redirectUri, HttpStatusCode expected)
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync("/connect/register",
            Body(new JsonObject { ["redirect_uris"] = new JsonArray { redirectUri } }));
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("invalid_redirect_uri", doc["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Register_MissingRedirectUris_Rejected()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync("/connect/register",
            Body(new JsonObject { ["redirect_uris"] = new JsonArray() }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("invalid_redirect_uri", doc["error"]!.GetValue<string>());
    }

    // ── MF-1: bounded body / redirect-count / client-name reads ─────────────────────────────

    /// <summary>A non-seekable <see cref="HttpContent"/> whose length can never be computed in
    /// advance — <see cref="TryComputeLength"/> always returns false, so <see cref="HttpClient"/>
    /// sends it WITHOUT a Content-Length header (the wire/TestServer shape of
    /// Transfer-Encoding: chunked). This is what makes the plan's original
    /// <c>if (ctx.Request.ContentLength is > 0 …)</c> guard a no-op — the fix under test must
    /// reject an oversized body from the STREAM itself, never consulting ContentLength.</summary>
    private sealed class ChunkedNoLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(payload, cancellationToken).AsTask();
    }

    [Fact]
    public async Task Register_ChunkedOversizedBody_Rejected_NotUnboundedRead()
    {
        // MF-1: on an isolated host with a tiny MaxRequestBytes, a body sent WITHOUT a
        // Content-Length header (simulating Transfer-Encoding: chunked) must still be rejected —
        // proving the cap is enforced by reading the stream itself, not by inspecting a
        // (here, absent) declared length.
        var cappedFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Korat:Cloud:SpaceMcpDcr:MaxRequestBytes"] = "64",
            })));
        var client = cappedFactory.CreateClient();

        // Padding lives in an UNMAPPED JSON property (not client_name/redirect_uris) so this
        // test proves the raw BYTE cap specifically — System.Text.Json still has to read (and
        // skip) every byte of an unknown property while parsing, so the bounded stream sees the
        // full oversized payload regardless of which field the client dressed it up as.
        var padding = new string('a', 4000);
        var payloadJson =
            $$"""{"client_name":"Test MCP Client","redirect_uris":["http://127.0.0.1:45123/callback"],"padding":"{{padding}}"}""";
        var content = new ChunkedNoLengthContent(Encoding.UTF8.GetBytes(payloadJson));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/connect/register", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("invalid_client_metadata", doc["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_TooManyRedirectUris_Rejected()
    {
        // MF-1 (second half): even a body well under MaxRequestBytes must not be allowed to
        // pack an unbounded redirect_uris array — every entry is individually a VALID loopback
        // URI (so the redirect-URI policy alone would accept each one) but the COUNT is bounded
        // by SpaceMcpDcrOptions.MaxRedirectUris (default 5).
        var client = fixture.Factory.CreateClient();
        var uris = new JsonArray();
        for (var i = 0; i < 6; i++)
            uris.Add($"http://127.0.0.1:{40000 + i}/cb");

        var response = await client.PostAsync("/connect/register",
            Body(new JsonObject { ["redirect_uris"] = uris }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("invalid_client_metadata", doc["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_ClientNameTooLong_Rejected()
    {
        // MF-1 (second half): client_name is stored verbatim as the persisted DisplayName and
        // rendered on every future consent page — bounded by MaxClientNameLength (default 256).
        var client = fixture.Factory.CreateClient();
        var longName = new string('n', 300);

        var response = await client.PostAsync("/connect/register",
            Body(new JsonObject { ["client_name"] = longName }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("invalid_client_metadata", doc["error"]!.GetValue<string>());
    }

    // ── SF-1: CORS on the DCR endpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task Register_CarriesPermissiveCors_AndAnswersPreflight()
    {
        // SF-1: /connect/register is a mapped minimal API OUTSIDE the OpenIddict discovery
        // pipeline the pre-existing /.well-known CORS middleware targeted — without this fix a
        // browser-context DCR client (e.g. web claude.ai) would be blocked by the same-origin
        // policy before the request ever reaches the handler.
        var client = fixture.Factory.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/connect/register");
        preflight.Headers.Add("Origin", "https://claude.ai");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal("*", preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(
            "content-type",
            preflightResponse.Headers.GetValues("Access-Control-Allow-Headers").Single(),
            StringComparison.OrdinalIgnoreCase);

        var post = new HttpRequestMessage(HttpMethod.Post, "/connect/register") { Content = Body(new JsonObject()) };
        post.Headers.Add("Origin", "https://claude.ai");
        var postResponse = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        Assert.Equal("*", postResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    // ── Kill switch ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_KillSwitchOff_Returns404()
    {
        // NOTE: this only proves the endpoint's own kill switch (DcrEndpoints reads
        // SpaceMcpDcrOptions via a DI factory registered in Program.cs, so it observes this
        // isolated host's overridden config). It deliberately does NOT also assert that AS
        // metadata omits registration_endpoint when disabled this way: that omission (Task 2,
        // Program.cs's HandleConfigurationRequestContext handler) reads `builder.Configuration`
        // directly inside the OpenIddict *builder* lambda, which — unlike a request-time
        // handler — runs eagerly during AddServer(...), BEFORE WithWebHostBuilder's
        // ConfigureAppConfiguration override is merged in; it is therefore not observable this
        // way in an isolated test host (production is unaffected — there is only one real
        // build). Out of scope for Task 4/MF-1/SF-1/SF-3 to touch Task 2's already-committed
        // metadata handler; left as a residual finding.
        var disabledFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Korat:Cloud:SpaceMcpDcr:Enabled"] = "false",
            })));
        var client = disabledFactory.CreateClient();

        var response = await client.PostAsync("/connect/register", Body(new JsonObject()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── SF-3: consent-page "unverified / auto-registered" badge for DCR clients ─────────────────

    [Fact]
    public async Task Authorize_ForDcrRegisteredClient_ShowsUnverifiedBadge()
    {
        // SF-3: client_name is attacker-controlled ("Test MCP Client" here, but nothing stops a
        // real DCR client from claiming to be "Korat Official"). The consent page must warn the
        // owner this client walked in through open registration — driven purely by the
        // server-stamped korat:dcr marker, never by anything the client supplied.
        var registerClient = fixture.Factory.CreateClient();
        var redirectUri = "http://127.0.0.1:46001/cb";
        var registerResponse = await registerClient.PostAsync("/connect/register",
            Body(new JsonObject { ["redirect_uris"] = new JsonArray { redirectUri } }));
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        var registered = JsonNode.Parse(await registerResponse.Content.ReadAsStringAsync())!.AsObject();
        var dcrClientId = registered["client_id"]!.GetValue<string>();

        var seeded = await fixture.SeedUserAsync($"dcr-badge-{Guid.NewGuid():N}@example.com", "DCR Badge Owner");
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();
        var url = "/connect/authorize?response_type=code" +
            $"&client_id={dcrClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=korat:mcp" +
            $"&resource={Uri.EscapeDataString(resource)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            $"&state=st-{Guid.NewGuid():N}";

        var response = await browser.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("nverified", html, StringComparison.Ordinal); // "Unverified" / "unverified"
    }

    [Fact]
    public async Task Authorize_ForPreRegisteredClient_ShowsNoUnverifiedBadge()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"nodcr-badge-{Guid.NewGuid():N}@example.com", "No DCR Badge Owner");
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();

        var response = await browser.GetAsync(OAuthFlowHelper.AuthorizeUrl(resource, challenge));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("nverified", html, StringComparison.Ordinal);
    }
}
