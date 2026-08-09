using System.Net;
using System.Text;
using Korat.Cloud.Mcp.Oauth;
using Korat.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Korat.Cloud.ContractTests;

/// <summary>
/// Increment 2, Task 2: pure unit tests against a stub HttpMessageHandler — no
/// KoratIntegrationFixture needed (McpOAuthDiscoveryService is a plain class with one
/// constructor-injected seam, IOutboundHttpClientFactory). Covers the 401→PRM→AS-metadata happy
/// path, the canonical PRM-resource match (incl. trailing-slash — the driving target, Miro, is
/// literally "https://mcp.miro.com/"), a resource mismatch, and an SSRF-blocked discovery URL.
/// </summary>
public sealed class McpOAuthDiscoveryServiceTests
{
    private sealed class StubOutboundHttpClientFactory(HttpMessageHandler handler) : IOutboundHttpClientFactory
    {
        public HttpClient CreateClient(string purposeLabel) => new(handler, disposeHandler: false);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task DiscoverAsync_HappyPath_401ThenPrmThenAsMetadata()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/mcp" && req.Method == HttpMethod.Post)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
                return resp;
            }
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource")
                return Json(HttpStatusCode.OK,
                    """{"resource":"https://mcp.example.test/mcp","authorization_servers":["https://as.example.test"]}""");
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-authorization-server")
                return Json(HttpStatusCode.OK,
                    """{"issuer":"https://as.example.test","authorization_endpoint":"https://as.example.test/authorize","token_endpoint":"https://as.example.test/token","registration_endpoint":"https://as.example.test/register"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var metadata = await service.DiscoverAsync("https://mcp.example.test/mcp", default);

        Assert.Equal("https://as.example.test", metadata.Issuer);
        Assert.Equal("https://as.example.test/authorize", metadata.AuthorizationEndpoint);
        Assert.Equal("https://as.example.test/token", metadata.TokenEndpoint);
        Assert.Equal("https://as.example.test/register", metadata.RegistrationEndpoint);
    }

    [Theory]
    [InlineData("https://mcp.miro.com/", "https://mcp.miro.com", true)]     // trailing-slash normalization — the driving target
    [InlineData("https://mcp.miro.com", "https://mcp.miro.com/", true)]
    [InlineData("https://MCP.Miro.com/", "https://mcp.miro.com/", true)]    // host case-insensitivity
    [InlineData("https://mcp.miro.com:443/", "https://mcp.miro.com/", true)] // default-port normalization
    [InlineData("https://mcp.miro.com/", "https://evil.test/", false)]
    public void CanonicalUrlEquals_NormalizesSchemeHostPortTrailingSlash(string a, string b, bool expected)
    {
        Assert.Equal(expected, McpOAuthDiscoveryService.CanonicalUrlEquals(a, b));
    }

    [Fact]
    public async Task DiscoverAsync_PrmResourceMismatch_Throws()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/mcp")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
                return resp;
            }
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource")
                // resource does NOT match https://mcp.example.test/mcp
                return Json(HttpStatusCode.OK, """{"resource":"https://evil.test/mcp","authorization_servers":["https://as.example.test"]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => service.DiscoverAsync("https://mcp.example.test/mcp", default));
    }

    [Fact]
    public async Task DiscoverAsync_SsrfBlockedRemoteUrl_ThrowsWithoutDialingAnything()
    {
        var dialed = false;
        var handler = new RoutingHandler(_ => { dialed = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(
            () => service.DiscoverAsync("http://169.254.169.254/latest/meta-data/", default)); // http, not https — SsrfGuard rejects

        Assert.False(dialed);
    }

    [Fact]
    public async Task DiscoverAsync_NonUnauthorizedResponse_ThrowsSafeMessage()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var ex = await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => service.DiscoverAsync("https://mcp.example.test/mcp", default));
        Assert.DoesNotContain("System.", ex.Message); // never a raw .NET exception message
    }

    // --- Opus security gate (T2): wrong-type JSON fields must throw a clean
    // McpOAuthDiscoveryException, never an uncaught InvalidOperationException from
    // JsonNode.GetValue<string>()/AsArray() on an attacker-controlled response body. ---

    [Fact]
    public async Task DiscoverAsync_PrmResourceWrongType_ThrowsCleanExceptionNotInvalidOperationException()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/mcp")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
                return resp;
            }
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource")
                // "resource" is a number, not a string — attacker-controlled wrong-type field.
                return Json(HttpStatusCode.OK, """{"resource":123,"authorization_servers":["https://as.example.test"]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => service.DiscoverAsync("https://mcp.example.test/mcp", default));
    }

    [Fact]
    public async Task DiscoverAsync_PrmAuthorizationServersWrongType_ThrowsCleanExceptionNotInvalidOperationException()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/mcp")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
                return resp;
            }
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource")
                // "authorization_servers" is a string, not an array — attacker-controlled wrong-type field.
                return Json(HttpStatusCode.OK,
                    """{"resource":"https://mcp.example.test/mcp","authorization_servers":"not-an-array"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => service.DiscoverAsync("https://mcp.example.test/mcp", default));
    }

    [Fact]
    public async Task DiscoverAsync_AsMetadataIssuerWrongType_ThrowsCleanExceptionNotInvalidOperationException()
    {
        var handler = new RoutingHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/mcp")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
                return resp;
            }
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-protected-resource")
                return Json(HttpStatusCode.OK,
                    """{"resource":"https://mcp.example.test/mcp","authorization_servers":["https://as.example.test"]}""");
            if (req.RequestUri!.AbsolutePath == "/.well-known/oauth-authorization-server")
                // "issuer" is a number, not a string — attacker-controlled wrong-type field.
                return Json(HttpStatusCode.OK,
                    """{"issuer":123,"authorization_endpoint":"https://as.example.test/authorize","token_endpoint":"https://as.example.test/token"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => service.DiscoverAsync("https://mcp.example.test/mcp", default));
    }

    // --- "auto-detect auth mode" feature: DetectAuthModeAsync's 3-way classifier. A pure unit
    // test against a stubbed handler (same StubOutboundHttpClientFactory/RoutingHandler as above)
    // — no OAuthFacadeHostRegistry https-façade needed since these never go through the shared
    // KoratIntegrationFixture Kestrel-stub harness, just the classifier method directly. ---

    [Fact]
    public async Task DetectAuthModeAsync_Returns200_ClassifiesNone()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{}}""", Encoding.UTF8, "application/json"),
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.None, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_401WithResourceMetadataChallenge_ClassifiesOAuth()
    {
        var handler = new RoutingHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            resp.Headers.Add("WWW-Authenticate",
                "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource\"");
            return resp;
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.OAuth, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_403WithNoChallenge_ClassifiesUnknown()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.Unknown, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_401WithNoWwwAuthenticateHeader_ClassifiesUnknown()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.Unknown, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_401WithBearerChallengeButNoResourceMetadata_ClassifiesUnknown()
    {
        // A plain Bearer challenge (no RFC 9728 resource_metadata param) is NOT enough signal —
        // this is the "401/403 with no such challenge" case the plan calls out as Unknown, not OAuth.
        var handler = new RoutingHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            resp.Headers.Add("WWW-Authenticate", "Bearer realm=\"example\"");
            return resp;
        });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.Unknown, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_5xxResponse_ClassifiesUnknown()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.Unknown, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_NetworkError_ClassifiesUnknown_NeverThrows()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("connection refused"));
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("https://mcp.example.test/mcp", default);

        Assert.Equal(McpAuthMode.Unknown, mode);
    }

    [Fact]
    public async Task DetectAuthModeAsync_SsrfBlockedRemoteUrl_ClassifiesUnknownWithoutDialingAnything()
    {
        var dialed = false;
        var handler = new RoutingHandler(_ => { dialed = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        var service = new McpOAuthDiscoveryService(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthDiscoveryService>.Instance);

        var mode = await service.DetectAuthModeAsync("http://169.254.169.254/latest/meta-data/", default); // http, not https — SsrfGuard rejects

        Assert.Equal(McpAuthMode.Unknown, mode);
        Assert.False(dialed);
    }

    [Theory]
    [InlineData(McpAuthMode.None, "none")]
    [InlineData(McpAuthMode.OAuth, "oauth")]
    [InlineData(McpAuthMode.Unknown, "unknown")]
    public void McpAuthModeStrings_ToWireString_MapsEachModeToItsLowercaseWireValue(McpAuthMode mode, string expected)
    {
        Assert.Equal(expected, McpAuthModeStrings.ToWireString(mode));
    }
}
