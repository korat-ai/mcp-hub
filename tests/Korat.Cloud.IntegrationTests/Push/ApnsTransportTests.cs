using System.Net;
using System.Security.Cryptography;
using Korat.Cloud.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.IntegrationTests.Push;

/// <summary>
/// Unit tests for the shared <see cref="ApnsTransport"/> — specifically the behavior that is NEW
/// as of the 031 (mobile-push increment 2) refactor: a shared JWT cache across multiple senders,
/// and the raw status/body passthrough contract each sender maps independently. Per-status-code
/// mapping is already covered end-to-end by ApnsPushWakeSenderTests (proving the wake path is
/// byte-identical after the extraction) and ApnsAlertSenderTests (Task 2).
/// </summary>
public sealed class ApnsTransportTests : IDisposable
{
    private static readonly string TestPrivateKeyPem = GenerateTestPrivateKeyPem();

    private static string GenerateTestPrivateKeyPem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = ecdsa.ExportPkcs8PrivateKey();
        var b64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{b64}\n-----END PRIVATE KEY-----";
    }

    private static ApnsOptions DefaultOptions() => new()
    {
        KeyId = "TESTKEYID1",
        TeamId = "ABCDE12345",
        BundleId = "dev.korat.node",
        PrivateKeyPem = TestPrivateKeyPem,
        WakeWaitSeconds = 12,
        WakeDedupSeconds = 10,
    };

    private HttpMessageHandler? _handler;
    private ApnsTransport? _transport;

    public void Dispose()
    {
        _handler?.Dispose();
        _transport?.Dispose();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>Records the Authorization header of every request it sees.</summary>
    private sealed class JwtCapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> SeenBearerTokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenBearerTokens.Add(request.Headers.Authorization!.Parameter!);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class RawStubHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SendAsync_Reuses_Cached_Jwt_Across_Two_Calls()
    {
        var handler = new JwtCapturingHandler(HttpStatusCode.OK);
        _handler = handler;
        var factory = new StubHttpClientFactory(handler);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);

        var headers = new Dictionary<string, string> { ["apns-push-type"] = "background" };
        await _transport.SendAsync("aabbccdd" + new string('0', 56), "apns", headers, "{}"u8.ToArray(), CancellationToken.None);
        await _transport.SendAsync("aabbccdd" + new string('0', 56), "apns", headers, "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(2, handler.SeenBearerTokens.Count);
        Assert.Equal(handler.SeenBearerTokens[0], handler.SeenBearerTokens[1]); // same JWT — cache hit
    }

    [Fact]
    public async Task SendAsync_Returns_Raw_Status_And_Body_On_Non200()
    {
        var stub = new RawStubHandler(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}""");
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);

        var (status, body) = await _transport.SendAsync(
            "aabbccdd" + new string('0', 56), "apns",
            new Dictionary<string, string>(), "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(400, status);
        Assert.Contains("BadDeviceToken", body);
    }

    [Fact]
    public async Task SendAsync_Returns_Null_Body_On_200()
    {
        var stub = new RawStubHandler(HttpStatusCode.OK, null);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);

        var (status, body) = await _transport.SendAsync(
            "aabbccdd" + new string('0', 56), "apns",
            new Dictionary<string, string>(), "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(200, status);
        Assert.Null(body);
    }
}
