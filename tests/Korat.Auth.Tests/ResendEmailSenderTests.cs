using System.Net;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

public class ResendEmailSenderTests
{
    private sealed class CapturingLogger : ILogger<ResendEmailSender>, IDisposable
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> resp) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(resp(request));
        }
    }

    [Fact]
    public async Task SendMagicLinkAsync_ThrowsWithoutSending_WhenApiKeyMissing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions { ApiKey = "" });
        var sender = new ResendEmailSender(http, opts, NullLogger<ResendEmailSender>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendMagicLinkAsync(
            "a@b.co", new Uri("https://x.test/c?token=abc"), TimeSpan.FromMinutes(15), default));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendMagicLinkAsync_MissingApiKey_DoesNotLogRawToken()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger();
        var sender = new ResendEmailSender(
            new HttpClient(handler), Options.Create(new ResendOptions { ApiKey = "" }), logger);
        const string rawToken = "super-secret-magic-link-token";

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendMagicLinkAsync(
            "a@b.co", new Uri($"https://x.test/c?token={rawToken}"), TimeSpan.FromMinutes(15), default));

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(rawToken, message));
    }

    [Fact]
    public async Task SendMagicLinkAsync_PostsToResend_WithBearer_WhenConfigured()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions { ApiKey = "test-key", FromEmail = "f@x.test", FromName = "Korat" });
        var sender = new ResendEmailSender(http, opts, NullLogger<ResendEmailSender>.Instance);
        await sender.SendMagicLinkAsync("a@b.co", new Uri("https://x.test/c?token=abc"), TimeSpan.FromMinutes(15), default);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://api.resend.com/emails", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendMagicLinkAsync_ThrowsHttpRequestException_WithStatus_OnNonSuccess()
    {
        const string rawToken = "secret-value";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent($"provider echoed token={rawToken}"),
        });
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions { ApiKey = "test-key" });
        var logger = new CapturingLogger();
        var sender = new ResendEmailSender(http, opts, logger);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sender.SendMagicLinkAsync(
                "a@b.co", new Uri($"https://x.test/c?token={rawToken}"), TimeSpan.FromMinutes(15), default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.DoesNotContain(rawToken, ex.Message);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(rawToken, message));
    }
}
