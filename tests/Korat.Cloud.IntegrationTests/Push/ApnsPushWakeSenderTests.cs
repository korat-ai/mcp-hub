using System.Net;
using System.Security.Cryptography;
using Korat.Cloud.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.IntegrationTests.Push;

/// <summary>
/// Unit tests for <see cref="ApnsPushWakeSender"/> using a stubbed <see cref="HttpMessageHandler"/>.
/// The real APNs endpoint is never contacted.
///
/// 031 (mobile-push increment 2): ApnsPushWakeSender's constructor changed from
/// (IOptions&lt;ApnsOptions&gt;, IHttpClientFactory, ILogger) to (ApnsTransport, ILogger) — the
/// ES256/JWT/HTTP plumbing moved into the shared ApnsTransport (Task 1). Every test below builds
/// an ApnsTransport first, then the sender. ALL BEHAVIORAL ASSERTIONS ARE UNCHANGED from before
/// the refactor — this is the proof that the wake path is byte-identical.
/// </summary>
public sealed class ApnsPushWakeSenderTests : IDisposable
{
    // Generate a fresh ES256 key for every test run so we don't need a committed .p8 fixture.
    private static readonly string TestPrivateKeyPem = GenerateTestPrivateKeyPem();

    private static string GenerateTestPrivateKeyPem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = ecdsa.ExportPkcs8PrivateKey();
        var b64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{b64}\n-----END PRIVATE KEY-----";
    }

    private static ApnsOptions DefaultOptions(string? keyId = "TESTKEYID1") => new()
    {
        KeyId = keyId,
        TeamId = "ABCDE12345",
        BundleId = "dev.korat.node",
        PrivateKeyPem = TestPrivateKeyPem,
        WakeWaitSeconds = 12,
        WakeDedupSeconds = 10,
    };

    private HttpMessageHandler? _handler;
    private ApnsTransport? _transport;

    /// <summary>
    /// Stub IHttpClientFactory that always returns an HttpClient backed by <paramref name="handler"/>.
    /// </summary>
    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }

    private ApnsPushWakeSender CreateSender(HttpStatusCode status, string? responseBody = null)
    {
        var stub = new StubHandler(status, responseBody);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        return new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);
    }

    public void Dispose()
    {
        _handler?.Dispose();
        _transport?.Dispose();
    }

    [Fact]
    public async Task Send_Returns_Sent_On_200()
    {
        var sender = CreateSender(HttpStatusCode.OK);
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Sent, result);
    }

    [Fact]
    public async Task Send_Returns_TokenInvalid_On_410()
    {
        var sender = CreateSender(HttpStatusCode.Gone);
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.TokenInvalid, result);
    }

    [Fact]
    public async Task Send_Returns_TokenInvalid_On_400_BadDeviceToken()
    {
        var sender = CreateSender(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}""");
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.TokenInvalid, result);
    }

    [Fact]
    public async Task Send_Returns_Failed_On_400_Other()
    {
        var sender = CreateSender(HttpStatusCode.BadRequest, """{"reason":"MissingTopic"}""");
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Failed, result);
    }

    [Fact]
    public async Task Send_Returns_Failed_On_429()
    {
        var sender = CreateSender(HttpStatusCode.TooManyRequests);
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Failed, result);
    }

    [Fact]
    public async Task Send_Returns_Failed_On_500()
    {
        var sender = CreateSender(HttpStatusCode.InternalServerError);
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Failed, result);
    }

    [Fact]
    public async Task Send_Returns_Failed_On_403_NonTokenReason()
    {
        var sender = CreateSender(HttpStatusCode.Forbidden, """{"reason":"DeviceTokenNotForTopic"}""");
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Failed, result);
    }

    [Fact]
    public async Task Send_Returns_Sent_On_403_ExpiredProviderToken_Then_200_On_Retry()
    {
        // Stub returns 403 on first call, 200 on second (simulates JWT cache refresh + retry).
        var stub = new SequentialStubHandler(new[]
        {
            (HttpStatusCode.Forbidden, (string?)"""{"reason":"ExpiredProviderToken"}"""),
            (HttpStatusCode.OK,        (string?)null),
        });
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);

        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);

        Assert.Equal(PushWakeResult.Sent, result);
        Assert.Equal(2, stub.CallCount); // two HTTP calls: 403 + retry 200
    }

    [Fact]
    public async Task Send_Uses_Sandbox_Host_For_ApnsSandbox_Platform()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);
        await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns_sandbox", CancellationToken.None);
        Assert.NotNull(stub.LastRequest);
        Assert.Contains("sandbox", stub.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task Send_Uses_Production_Host_For_Apns_Platform()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);
        await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.NotNull(stub.LastRequest);
        Assert.DoesNotContain("sandbox", stub.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task Send_Sets_Required_Headers()
    {
        var stub = new StubHandler(HttpStatusCode.OK);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);
        await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);

        var req = stub.LastRequest!;
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("bearer", req.Headers.Authorization.Scheme, ignoreCase: true);
        Assert.Contains("apns-push-type", req.Headers.Select(h => h.Key));
        Assert.Contains("apns-priority", req.Headers.Select(h => h.Key));
        Assert.Contains("apns-topic", req.Headers.Select(h => h.Key));
        Assert.Contains("apns-expiration", req.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Send_Does_Not_Throw_On_HttpException()
    {
        var throwing = new ThrowingHandler();
        _handler = throwing;
        var factory = new StubHttpClientFactory(throwing);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsPushWakeSender(_transport, NullLogger<ApnsPushWakeSender>.Instance);
        // Must not propagate — returns Failed
        var result = await sender.SendWakeAsync("aabbccdd" + new string('0', 56), "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.Failed, result);
    }

    [Fact]
    public async Task NullPushWakeSender_Returns_NotConfigured()
    {
        var sender = new NullPushWakeSender();
        var result = await sender.SendWakeAsync("any", "apns", CancellationToken.None);
        Assert.Equal(PushWakeResult.NotConfigured, result);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class StubHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class SequentialStubHandler(IList<(HttpStatusCode Status, string? Body)> responses)
        : HttpMessageHandler
    {
        private int _idx;
        public int CallCount => _idx;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = responses[_idx++];
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated network failure");
    }
}
