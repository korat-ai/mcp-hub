using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cloud.Mcp.Oauth;
using Korat.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Korat.Cloud.ContractTests;

public sealed class McpOAuthClientRegistrarTests
{
    private sealed class StubOutboundHttpClientFactory(HttpMessageHandler handler) : IOutboundHttpClientFactory
    {
        public HttpClient CreateClient(string purposeLabel) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> route) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return route(request, LastRequestBody);
        }
    }

    [Fact]
    public async Task RegisterAsync_HappyPath_ReturnsClientIdAndSecret_UsesPerServerRedirectUri()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"client_id":"dcr-client-123","client_secret":"dcr-secret-456"}""", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        var result = await registrar.RegisterAsync(
            "https://as.example.test/register", "https://cloud.korat.test/api/mcp/oauth/callback/srv-abc", default);

        Assert.Equal("dcr-client-123", result.ClientId);
        Assert.Equal("dcr-secret-456", result.ClientSecret);
        var sentBody = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("https://cloud.korat.test/api/mcp/oauth/callback/srv-abc",
            sentBody.RootElement.GetProperty("redirect_uris")[0].GetString());
    }

    [Fact]
    public async Task RegisterAsync_NoClientSecret_ReturnsNullSecret()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"client_id":"public-client-only"}""", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        var result = await registrar.RegisterAsync("https://as.example.test/register", "https://cloud.korat.test/cb", default);

        Assert.Equal("public-client-only", result.ClientId);
        Assert.Null(result.ClientSecret);
    }

    [Fact]
    public async Task RegisterAsync_AsRejects_ThrowsSafeMessage()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_client_metadata"}""", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        var ex = await Assert.ThrowsAsync<McpOAuthDiscoveryException>(
            () => registrar.RegisterAsync("https://as.example.test/register", "https://cloud.korat.test/cb", default));
        Assert.DoesNotContain("invalid_client_metadata", ex.Message); // upstream body never surfaced verbatim
    }

    [Fact]
    public async Task RegisterAsync_SsrfBlockedRegistrationEndpoint_ThrowsWithoutDialing()
    {
        var dialed = false;
        var handler = new RecordingHandler((req, body) => { dialed = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(
            () => registrar.RegisterAsync("http://169.254.169.254/register", "https://cloud.korat.test/cb", default));

        Assert.False(dialed);
    }

    [Fact]
    public async Task RegisterAsync_MalformedJson_ThrowsSafeMessage()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(
            () => registrar.RegisterAsync("https://as.example.test/register", "https://cloud.korat.test/cb", default));
    }

    // --- Opus security gate (T3): wrong-type JSON fields must throw a clean
    // McpOAuthDiscoveryException, never an uncaught InvalidOperationException from
    // JsonNode.GetValue<string>() on an attacker-controlled response body — same class of bug the
    // T2 gate already fixed in McpOAuthDiscoveryService. ---

    [Fact]
    public async Task RegisterAsync_ClientIdWrongType_ThrowsCleanExceptionNotInvalidOperationException()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "client_id" is a number, not a string — attacker-controlled wrong-type field.
            Content = new StringContent("""{"client_id":123}""", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        await Assert.ThrowsAsync<McpOAuthDiscoveryException>(
            () => registrar.RegisterAsync("https://as.example.test/register", "https://cloud.korat.test/cb", default));
    }

    [Fact]
    public async Task RegisterAsync_ClientSecretWrongType_TreatedAsAbsentNotThrown()
    {
        var handler = new RecordingHandler((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // "client_secret" is a number, not a string — optional field, wrong-type -> null, not a throw.
            Content = new StringContent("""{"client_id":"public-client-only","client_secret":456}""", Encoding.UTF8, "application/json"),
        });
        var registrar = new McpOAuthClientRegistrar(new StubOutboundHttpClientFactory(handler), NullLogger<McpOAuthClientRegistrar>.Instance);

        var result = await registrar.RegisterAsync("https://as.example.test/register", "https://cloud.korat.test/cb", default);

        Assert.Equal("public-client-only", result.ClientId);
        Assert.Null(result.ClientSecret);
    }
}
