using System.Net;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="ResendEmailChangeEmailSender"/>.
///
/// Sec M3 guard: when the Resend API key is absent (dev fallback), the raw
/// email-change token must NOT appear in the logged message — it must be redacted.
/// </summary>
public class ResendEmailChangeEmailSenderTests
{
    // ── capturing logger ─────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger<ResendEmailChangeEmailSender>, IDisposable
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
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    // ── stub HTTP handler (never reached when key is missing) ─────────────────

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("HTTP send must not be called when API key is missing.");
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sec M3: the raw token in the ?token= query parameter must not appear in any log
    /// message when the API key is absent and the dev-fallback log path is taken.
    /// </summary>
    [Fact]
    public async Task SendVerificationLinkAsync_DevFallback_DoesNotLogRawToken()
    {
        var logger = new CapturingLogger();
        var http = new HttpClient(new NeverCalledHandler());
        var opts = Options.Create(new ResendOptions { ApiKey = "" });
        var sender = new ResendEmailChangeEmailSender(http, opts, logger);

        const string rawToken = "super-secret-raw-token-value";
        var verifyUrl = new Uri($"https://app.example.com/app/account/verify-email?token={rawToken}");

        await sender.SendVerificationLinkAsync("user@example.com", verifyUrl, TimeSpan.FromMinutes(30), default);

        // The raw token must appear in NO logged message.
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain(rawToken, msg);
        }

        // At least one log message must have been emitted (to confirm the dev-fallback path ran).
        Assert.NotEmpty(logger.Messages);
    }

    [Fact]
    public async Task SendVerificationLinkAsync_WithApiKey_CallsResend_DoesNotLog()
    {
        var logger = new CapturingLogger();
        var called = false;
        var handler = new StubOkHandler(_ => { called = true; });
        var http = new HttpClient(handler);
        var opts = Options.Create(new ResendOptions
        {
            ApiKey = "live-key",
            FromEmail = "noreply@example.com",
            FromName = "Korat"
        });
        var sender = new ResendEmailChangeEmailSender(http, opts, logger);

        await sender.SendVerificationLinkAsync(
            "user@example.com",
            new Uri("https://app.example.com/app/account/verify-email?token=sometoken"),
            TimeSpan.FromMinutes(30),
            default);

        Assert.True(called, "Resend HTTP call should have been made when API key is configured.");
        // No log message should be emitted on the success path (no dev-fallback).
        Assert.Empty(logger.Messages);
    }

    private sealed class StubOkHandler(Action<HttpRequestMessage> onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            onSend(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        // Unused but satisfies the compiler for the non-async overload path.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            onSend(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
