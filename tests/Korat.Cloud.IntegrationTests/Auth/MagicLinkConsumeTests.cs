using Korat.Cloud.Web.Auth.Services;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for MagicLinkService double-consume protection
/// and basic issue/consume semantics.
/// Each test uses its own isolated InMemory database.
///
/// Race-safety (Postgres serialised UPDATE) is marked [Skip] — see
/// ConcurrentConsume_SameToken_OnlyOneSucceeds.
/// </summary>
public sealed class MagicLinkConsumeTests
{
    private sealed class CapturingEmailSender : IEmailSender
    {
        public Uri? LastUrl { get; private set; }
        public Task SendMagicLinkAsync(string toEmail, Uri consumeUrl, TimeSpan ttl, CancellationToken ct)
        {
            LastUrl = consumeUrl;
            return Task.CompletedTask;
        }
    }

    private static (MagicLinkService svc, KoratDbContext db, CapturingEmailSender mail) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new KoratDbContext(opts);
        var mail = new CapturingEmailSender();
        var svc = new MagicLinkService(db, mail, NullLogger<MagicLinkService>.Instance, TimeProvider.System);
        return (svc, db, mail);
    }

    // Extract the raw token from the emailed URL (?token=<value>).
    private static string ExtractRawToken(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["token"] ?? throw new InvalidOperationException("No 'token' param in URL");
    }

    [Fact]
    public async Task TryConsumeAsync_Succeeds_OnFirstUse()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("test@example.com",  null, null, new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.LastUrl!);

        var result = await svc.TryConsumeAsync(rawToken, null, null, default);

        Assert.NotNull(result);
        Assert.Equal("test@example.com", result!.Email);
    }

    [Fact]
    public async Task TryConsumeAsync_ReturnsNull_OnDoubleConsume()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("double@example.com",  null, null, new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.LastUrl!);

        var first = await svc.TryConsumeAsync(rawToken, null, null, default);
        var second = await svc.TryConsumeAsync(rawToken, null, null, default);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryConsumeAsync_ReturnsNull_ForUnknownToken()
    {
        var (svc, _, _) = Build();
        // A random string that was never issued produces no match.
        var result = await svc.TryConsumeAsync("unknownTokenThatWasNeverIssued", null, null, default);
        Assert.Null(result);
    }

    [Fact(Skip = "Requires Postgres backend — InMemory cannot prove serialised UPDATE race-safety; validated at manual integration / deploy.")]
    public async Task ConcurrentConsume_SameToken_OnlyOneSucceeds()
    {
        var (svc, _, mail) = Build();
        await svc.IssueAsync("race@example.com",  null, null, new Uri("https://test.local"), default);
        var rawToken = ExtractRawToken(mail.LastUrl!);

        var t1 = svc.TryConsumeAsync(rawToken, null, null, default);
        var t2 = svc.TryConsumeAsync(rawToken, null, null, default);
        var results = await Task.WhenAll(t1, t2);

        var wins = results.Count(r => r is not null);
        Assert.Equal(1, wins);
    }
}
