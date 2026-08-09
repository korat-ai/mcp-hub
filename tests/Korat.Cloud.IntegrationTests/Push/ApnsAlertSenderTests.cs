using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Korat.Cloud.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.IntegrationTests.Push;

public sealed class ApnsAlertSenderTests : IDisposable
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

    private sealed class StubHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsByteArrayAsync(ct);
            var response = new HttpResponseMessage(status);
            if (body is not null)
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated network failure");
    }

    private ApnsAlertSender CreateSender(HttpStatusCode status, string? responseBody, out StubHandler stub)
    {
        stub = new StubHandler(status, responseBody);
        _handler = stub;
        var factory = new StubHttpClientFactory(stub);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        return new ApnsAlertSender(_transport, NullLogger<ApnsAlertSender>.Instance);
    }

    private static AlertContent SampleContent() => new(
        "New access request",
        "Agent \"cursor\" requests access to \"filesystem\"",
        new Dictionary<string, string> { ["type"] = "access_request", ["accessRequestId"] = "req-123" });

    [Fact]
    public async Task SendAlertAsync_Returns_Delivered_On_200()
    {
        var sender = CreateSender(HttpStatusCode.OK, null, out _);
        var result = await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.Delivered, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TokenInvalid_On_410()
    {
        var sender = CreateSender(HttpStatusCode.Gone, null, out _);
        var result = await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.TokenInvalid, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TokenInvalid_On_400_BadDeviceToken()
    {
        var sender = CreateSender(HttpStatusCode.BadRequest, """{"reason":"BadDeviceToken"}""", out _);
        var result = await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.TokenInvalid, result);
    }

    [Fact]
    public async Task SendAlertAsync_Returns_TransientFailure_On_500()
    {
        var sender = CreateSender(HttpStatusCode.InternalServerError, null, out _);
        var result = await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.TransientFailure, result);
    }

    [Fact]
    public async Task SendAlertAsync_Sets_Alert_Specific_Headers()
    {
        var sender = CreateSender(HttpStatusCode.OK, null, out var stub);
        await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);

        var req = stub.LastRequest!;
        Assert.Equal("alert", req.Headers.GetValues("apns-push-type").Single());
        Assert.Equal("10", req.Headers.GetValues("apns-priority").Single());
        Assert.Equal("req-123", req.Headers.GetValues("apns-collapse-id").Single());
        Assert.Contains("apns-topic", req.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task SendAlertAsync_Body_Contains_Title_Body_And_Data()
    {
        var sender = CreateSender(HttpStatusCode.OK, null, out var stub);
        await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("New access request", root.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("Agent \"cursor\" requests access to \"filesystem\"", root.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal("default", root.GetProperty("aps").GetProperty("sound").GetString());
        Assert.Equal("access_request", root.GetProperty("type").GetString());
        Assert.Equal("req-123", root.GetProperty("accessRequestId").GetString());
    }

    [Fact]
    public async Task SendAlertAsync_Does_Not_Throw_On_HttpException()
    {
        var throwing = new ThrowingHandler();
        _handler = throwing;
        var factory = new StubHttpClientFactory(throwing);
        _transport = new ApnsTransport(Options.Create(DefaultOptions()), factory, NullLogger<ApnsTransport>.Instance);
        var sender = new ApnsAlertSender(_transport, NullLogger<ApnsAlertSender>.Instance);

        var result = await sender.SendAlertAsync("aabbccdd" + new string('0', 56), "apns", SampleContent(), CancellationToken.None);
        Assert.Equal(AlertSendResult.TransientFailure, result);
    }
}
