using System.Net;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

public class ResendEmailSenderTests
{
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
    public async Task SendMagicLinkAsync_SkipsSend_WhenApiKeyMissing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions { ApiKey = "" });
        var sender = new ResendEmailSender(http, opts, NullLogger<ResendEmailSender>.Instance);
        await sender.SendMagicLinkAsync("a@b.co", new Uri("https://x.test/c?token=abc"), TimeSpan.FromMinutes(15), default);
        Assert.Null(handler.LastRequest);
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
    public async Task SendMagicLinkAsync_SwallowsNonSuccessStatus_WithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions { ApiKey = "test-key" });
        var sender = new ResendEmailSender(http, opts, NullLogger<ResendEmailSender>.Instance);
        var ex = await Record.ExceptionAsync(() => sender.SendMagicLinkAsync("a@b.co", new Uri("https://x.test/c?t=1"), TimeSpan.FromMinutes(15), default));
        Assert.Null(ex);  // logged, not thrown
    }
}
