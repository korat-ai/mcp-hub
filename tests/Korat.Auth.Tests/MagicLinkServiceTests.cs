using System.Net;
using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

public class MagicLinkServiceTests
{
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<(string Email, Uri Url)> Sent { get; } = new();
        public Task SendMagicLinkAsync(string toEmail, Uri consumeUrl, TimeSpan ttl, CancellationToken ct)
        {
            Sent.Add((toEmail, consumeUrl));
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedHttpHandler(params Func<CancellationToken, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            return Task.FromResult(responses[index](ct));
        }
    }

    private static (MagicLinkService svc, KoratDbContext db, CapturingEmailSender mail) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var mail = new CapturingEmailSender();
        var svc = new MagicLinkService(db, mail, NullLogger<MagicLinkService>.Instance, TimeProvider.System);
        return (svc, db, mail);
    }

    // Helper: extract the raw token string from the emailed URL (?token=<value>).
    private static string ExtractRawToken(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["token"] ?? throw new InvalidOperationException("No 'token' param in URL");
    }

    [Fact]
    public void NormaliseEmail_LowercasesAndTrims()
    {
        Assert.Equal("a@b.co", MagicLinkService.NormaliseEmail("  A@B.CO  "));
    }

    [Fact]
    public async Task IssueAsync_SendsEmail_WithTokenOnlyUrl_NoEmailParam()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);
        Assert.Single(mail.Sent);
        var url = mail.Sent[0].Url.ToString();
        Assert.DoesNotContain("email=", url);
        Assert.Contains("token=", url);
    }

    // F5: the raw token must NOT be stored in the database — only its SHA-256 hash.
    [Fact]
    public async Task IssueAsync_StoresOnlyHash_NotRawToken_InDatabase()
    {
        var (svc, db, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);

        var rawToken = ExtractRawToken(mail.Sent[0].Url);
        var record = db.MagicLinkTokens.Single(t => t.Email == "a@b.co");

        // The raw token must NOT appear in the stored TokenHash.
        Assert.NotEqual(rawToken, record.TokenHash);

        // The stored hash must equal SHA-256(rawToken) — exactly the CliToken pattern.
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        Assert.Equal(expectedHash, record.TokenHash);
    }

    // F5: the Id (surrogate PK) must no longer be the URL secret.
    [Fact]
    public async Task IssueAsync_DatabaseIdIsNotExposedInUrl()
    {
        var (svc, db, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);

        var record = db.MagicLinkTokens.Single(t => t.Email == "a@b.co");
        var rawToken = ExtractRawToken(mail.Sent[0].Url);

        // The URL token must differ from the database primary key.
        Assert.NotEqual(record.Id.ToString("N"), rawToken);
        Assert.NotEqual(record.Id.ToString(), rawToken);
    }

    [Fact]
    public async Task IssueAsync_GlobalPerEmailRateLimit_SuppressesSecondSendWithinCooldown()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task IssueAsync_RealSender503_RemovesOnlyFailedToken_AndImmediateRetrySucceeds()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var handler = new SequencedHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var sender = new ResendEmailSender(
            new HttpClient(handler),
            Options.Create(new ResendOptions { ApiKey = "test-key" }),
            NullLogger<ResendEmailSender>.Instance);
        var svc = new MagicLinkService(db, sender, NullLogger<MagicLinkService>.Instance, TimeProvider.System);

        await svc.IssueAsync("retry@example.com", null, null, new Uri("https://test.local"), default);
        Assert.Empty(db.MagicLinkTokens);

        await svc.IssueAsync("retry@example.com", null, null, new Uri("https://test.local"), default);

        Assert.Single(db.MagicLinkTokens);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task IssueAsync_MissingSenderConfiguration_RemovesFailedToken_AndAllowsRetry()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var handler = new SequencedHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var misconfiguredSender = new ResendEmailSender(
            new HttpClient(handler),
            Options.Create(new ResendOptions { ApiKey = "" }),
            NullLogger<ResendEmailSender>.Instance);
        var misconfiguredService = new MagicLinkService(
            db, misconfiguredSender, NullLogger<MagicLinkService>.Instance, TimeProvider.System);

        await misconfiguredService.IssueAsync(
            "configure-and-retry@example.com", null, null, new Uri("https://test.local"), default);
        Assert.Empty(db.MagicLinkTokens);
        Assert.Equal(0, handler.CallCount);

        var configuredSender = new ResendEmailSender(
            new HttpClient(handler),
            Options.Create(new ResendOptions { ApiKey = "test-key" }),
            NullLogger<ResendEmailSender>.Instance);
        var configuredService = new MagicLinkService(
            db, configuredSender, NullLogger<MagicLinkService>.Instance, TimeProvider.System);

        await configuredService.IssueAsync(
            "configure-and-retry@example.com", null, null, new Uri("https://test.local"), default);

        Assert.Single(db.MagicLinkTokens);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task IssueAsync_TransportFailure_RemovesFailedToken_AndAllowsRetry()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var handler = new SequencedHttpHandler(
            _ => throw new HttpRequestException("transport failed"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var sender = new ResendEmailSender(
            new HttpClient(handler),
            Options.Create(new ResendOptions { ApiKey = "test-key" }),
            NullLogger<ResendEmailSender>.Instance);
        var svc = new MagicLinkService(db, sender, NullLogger<MagicLinkService>.Instance, TimeProvider.System);

        await svc.IssueAsync("transport@example.com", null, null, new Uri("https://test.local"), default);
        await svc.IssueAsync("transport@example.com", null, null, new Uri("https://test.local"), default);

        Assert.Single(db.MagicLinkTokens);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task IssueAsync_CallerCancellation_RemovesFailedToken_AndPropagatesCancellation()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        using var cts = new CancellationTokenSource();
        var handler = new SequencedHttpHandler(ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        });
        var sender = new ResendEmailSender(
            new HttpClient(handler),
            Options.Create(new ResendOptions { ApiKey = "test-key" }),
            NullLogger<ResendEmailSender>.Instance);
        var svc = new MagicLinkService(db, sender, NullLogger<MagicLinkService>.Instance, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.IssueAsync("cancel@example.com", null, null, new Uri("https://test.local"), cts.Token));

        Assert.Empty(db.MagicLinkTokens);
    }

    // F5: consuming with the correct raw token (from email URL) must succeed.
    [Fact]
    public async Task TryConsumeAsync_WithCorrectRawToken_ReturnsEmailAndInviteCode()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  "1.2.3.4", "uahash", new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.Sent[0].Url);

        var result = await svc.TryConsumeAsync(rawToken, "1.2.3.4", "uahash", default);

        Assert.NotNull(result);
        Assert.Equal("a@b.co", result!.Email);
        Assert.False(result.ForensicsDivergence);
    }

    // F5: consuming with a tampered/wrong token must fail (anti-enumeration preserved).
    [Fact]
    public async Task TryConsumeAsync_WithTamperedToken_ReturnsNull()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);

        var result = await svc.TryConsumeAsync("completelyWrongToken", null, null, default);

        Assert.Null(result);
    }

    // F5: single-use guarantee — second consume with the same raw token must return null.
    [Fact]
    public async Task TryConsumeAsync_IsAtomicSingleUse_SecondCallReturnsNull()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  null, null, new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.Sent[0].Url);

        var first = await svc.TryConsumeAsync(rawToken, null, null, default);
        var second = await svc.TryConsumeAsync(rawToken, null, null, default);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    // F5: expired token must return null (TTL preserved).
    [Fact]
    public async Task TryConsumeAsync_ExpiredToken_ReturnsNull()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var mail = new CapturingEmailSender();

        // Use a fixed-time provider that starts 2 hours ahead so issued token is already expired.
        var frozenNow = DateTimeOffset.UtcNow;
        var fakeClock = new FakeTimeProvider(frozenNow);
        var svc = new MagicLinkService(db, mail, NullLogger<MagicLinkService>.Instance, fakeClock);

        // Issue at T=0
        await svc.IssueAsync("x@y.co",  null, null, new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.Sent[0].Url);

        // Advance time past TTL
        fakeClock.Advance(MagicLinkService.TokenTtl + TimeSpan.FromSeconds(1));

        var result = await svc.TryConsumeAsync(rawToken, null, null, default);
        Assert.Null(result);
    }

    // Forensic divergence flag is preserved when IP/UA differ.
    [Fact]
    public async Task TryConsumeAsync_DifferentIpUA_SetsDivergenceFlag()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("a@b.co",  "1.2.3.4", "agentA", new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.Sent[0].Url);

        var result = await svc.TryConsumeAsync(rawToken, "9.9.9.9", "agentB", default);

        Assert.NotNull(result);
        Assert.True(result!.ForensicsDivergence);
    }

    // Helper fake TimeProvider for controllable time.
    private sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
