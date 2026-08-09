using System.Net;
using System.Text;
using System.Web;
using Korat.Cloud.Mcp.Oauth;
using Korat.Domain;
using Xunit;

namespace Korat.Cloud.ContractTests;

public sealed class McpOAuthTokenExchangeTests
{
    private sealed class StubOutboundHttpClientFactory(HttpMessageHandler handler) : IOutboundHttpClientFactory
    {
        public HttpClient CreateClient(string purposeLabel) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return route(LastRequestBody);
        }
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_HappyPath_ReturnsTokensAndSendsFormEncodedGrant()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""", Encoding.UTF8, "application/json"),
        });
        var factory = new StubOutboundHttpClientFactory(handler);

        var result = await McpOAuthTokenExchange.ExchangeAuthorizationCodeAsync(
            factory, "https://as.example.test/token", "auth-code-1", "verifier-1",
            "https://cloud.korat.test/cb", "client-1", "secret-1", "https://mcp.example.test/", default);

        Assert.Equal("at-1", result.AccessToken);
        Assert.Equal("rt-1", result.RefreshToken);
        Assert.True(result.AccessExpiry > DateTimeOffset.UtcNow.AddMinutes(58));

        var sentForm = HttpUtility.ParseQueryString(handler.LastRequestBody!);
        Assert.Equal("authorization_code", sentForm["grant_type"]);
        Assert.Equal("auth-code-1", sentForm["code"]);
        Assert.Equal("verifier-1", sentForm["code_verifier"]);
        Assert.Equal("client-1", sentForm["client_id"]);
        Assert.Equal("secret-1", sentForm["client_secret"]);
        Assert.Equal("https://mcp.example.test/", sentForm["resource"]);
    }

    [Fact]
    public async Task RefreshAsync_HappyPath_SendsRefreshGrant()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"at-2","refresh_token":"rt-2","expires_in":3600}""", Encoding.UTF8, "application/json"),
        });
        var factory = new StubOutboundHttpClientFactory(handler);

        var result = await McpOAuthTokenExchange.RefreshAsync(
            factory, "https://as.example.test/token", "rt-old", "client-1", null, "https://mcp.example.test/", default);

        Assert.Equal("at-2", result.AccessToken);
        var sentForm = HttpUtility.ParseQueryString(handler.LastRequestBody!);
        Assert.Equal("refresh_token", sentForm["grant_type"]);
        Assert.Equal("rt-old", sentForm["refresh_token"]);
        Assert.Null(sentForm["client_secret"]); // public client — no secret sent
    }

    [Fact]
    public async Task RefreshAsync_NoNewRefreshTokenIssued_ResultHasNullRefreshToken()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"at-3","expires_in":3600}""", Encoding.UTF8, "application/json"),
        });
        var result = await McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-old", "client-1", null,
            "https://mcp.example.test/", default);

        Assert.Null(result.RefreshToken); // caller (HttpMcpProxyGrain, Task 5) must keep the OLD refresh token
    }

    [Fact]
    public async Task PostAsync_InvalidGrantError_ThrowsMcpOAuthInvalidGrantException()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant","error_description":"refresh token expired"}""", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<McpOAuthInvalidGrantException>(() => McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-dead", "client-1", null,
            "https://mcp.example.test/", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task PostAsync_5xxResponse_ThrowsMcpOAuthTransientTokenException_NotInvalidGrant(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<McpOAuthTransientTokenException>(() => McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default));
    }

    [Fact]
    public async Task PostAsync_NetworkFailure_ThrowsMcpOAuthTransientTokenException()
    {
        var handler = new ThrowingHandler();

        await Assert.ThrowsAsync<McpOAuthTransientTokenException>(() => McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("simulated network failure");
    }

    [Fact]
    public async Task PostAsync_SsrfBlockedTokenEndpoint_ThrowsWithoutDialing()
    {
        var dialed = false;
        var handler = new RecordingHandler(_ => { dialed = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(() => McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "http://169.254.169.254/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default));
        Assert.False(dialed);
    }

    // --- De-staled per T2/T3 opus gates (McpOAuthDiscoveryService, McpOAuthClientRegistrar): a
    // wrong-type attacker-controlled JSON field must throw a classified exception, never an
    // uncaught InvalidOperationException from JsonNode.GetValue<string>(). ---

    [Fact]
    public async Task PostAsync_AccessTokenWrongType_ThrowsTransientNotInvalidOperationException()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "access_token" is a number, not a string — attacker/misbehaving-AS-controlled wrong-type field.
            Content = new StringContent("""{"access_token":123,"expires_in":3600}""", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<McpOAuthTransientTokenException>(() => McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default));
    }

    [Fact]
    public async Task PostAsync_RefreshTokenWrongType_TreatedAsAbsentNotThrown()
    {
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "refresh_token" is a number, not a string — optional field, wrong-type -> null, not a throw.
            Content = new StringContent("""{"access_token":"at-1","refresh_token":789,"expires_in":3600}""", Encoding.UTF8, "application/json"),
        });

        var result = await McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default);

        Assert.Equal("at-1", result.AccessToken);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_ExpiresInAsJsonString_IsToleratedNotThrown()
    {
        // MINOR #9 (fable plan-review): some ASes return "expires_in":"3600" (a string), not a
        // number — must not 500 the callback.
        var handler = new RecordingHandler(body => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"at-str","expires_in":"1800"}""", Encoding.UTF8, "application/json"),
        });

        var result = await McpOAuthTokenExchange.RefreshAsync(
            new StubOutboundHttpClientFactory(handler), "https://as.example.test/token", "rt-1", "client-1", null,
            "https://mcp.example.test/", default);

        Assert.Equal("at-str", result.AccessToken);
        Assert.True(result.AccessExpiry > DateTimeOffset.UtcNow.AddMinutes(29));
        Assert.True(result.AccessExpiry < DateTimeOffset.UtcNow.AddMinutes(31));
    }
}
