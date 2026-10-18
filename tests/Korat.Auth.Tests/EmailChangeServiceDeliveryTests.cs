using System.Net;
using System.Text.Json;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

/// <summary>
/// Delivery-failure regressions using the real email-change service and Resend sender
/// over a fake HTTP transport.
/// </summary>
public sealed class EmailChangeServiceDeliveryTests
{
    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task RequestAsync_FiveFailedDeliveries_LeaveNoTokenOrRateLimitRecord_ThenRetryWorks()
    {
        var attemptedTokens = new List<string>();
        var sendCount = 0;
        var (service, db) = await BuildAsync(async (request, ct) =>
        {
            attemptedTokens.Add(await ExtractVerificationTokenAsync(request, ct));
            sendCount++;
            return new HttpResponseMessage(
                sendCount <= EmailChangeService.MaxRequestsPerWindow
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.OK);
        });

        await using (db)
        {
            var userId = await SeedUserAsync(db, "owner@example.com");

            for (var attempt = 0; attempt < EmailChangeService.MaxRequestsPerWindow; attempt++)
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => service.RequestAsync(
                    userId,
                    $"new-{attempt}@example.com",
                    new Uri("https://app.example.com"),
                    CancellationToken.None));

                Assert.Equal(0, await db.EmailChangeTokens.CountAsync(t => t.UserId == userId));

                var confirmation = await service.ConfirmAsync(
                    userId,
                    attemptedTokens[attempt],
                    CancellationToken.None);
                Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, confirmation.Status);
            }

            var retry = await service.RequestAsync(
                userId,
                "retry@example.com",
                new Uri("https://app.example.com"),
                CancellationToken.None);

            Assert.Equal(EmailChangeRequestStatus.Success, retry);
            Assert.Equal(EmailChangeService.MaxRequestsPerWindow + 1, sendCount);
            Assert.Equal(1, await db.EmailChangeTokens.CountAsync(t => t.UserId == userId));
        }
    }

    [Fact]
    public async Task RequestAsync_CancelledDuringDelivery_RemovesOnlyUndeliveredToken()
    {
        using var cancellation = new CancellationTokenSource();
        string? attemptedToken = null;
        var sendCount = 0;
        var (service, db) = await BuildAsync(async (request, ct) =>
        {
            sendCount++;
            if (sendCount == 2)
            {
                attemptedToken = await ExtractVerificationTokenAsync(request, ct);
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation did not reach the HTTP handler.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using (db)
        {
            var userId = await SeedUserAsync(db, "cancel-owner@example.com");
            var firstStatus = await service.RequestAsync(
                userId,
                "first-delivered@example.com",
                new Uri("https://app.example.com"),
                CancellationToken.None);
            Assert.Equal(EmailChangeRequestStatus.Success, firstStatus);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RequestAsync(
                userId,
                "cancel-new@example.com",
                new Uri("https://app.example.com"),
                cancellation.Token));

            Assert.NotNull(attemptedToken);
            var retained = await db.EmailChangeTokens.SingleAsync(t => t.UserId == userId);
            Assert.Equal("first-delivered@example.com", retained.NewEmail);
            Assert.NotNull(retained.SupersededAt);

            var confirmation = await service.ConfirmAsync(
                userId,
                attemptedToken!,
                CancellationToken.None);
            Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, confirmation.Status);
        }
    }

    [Fact]
    public async Task ConfirmAsync_OldAddressNotificationFails_KeepsCommittedEmailChange()
    {
        string? verificationToken = null;
        var sendCount = 0;
        var (service, db) = await BuildAsync(async (request, ct) =>
        {
            sendCount++;
            if (sendCount == 1)
            {
                verificationToken = await ExtractVerificationTokenAsync(request, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await using (db)
        {
            var userId = await SeedUserAsync(db, "alert-old@example.com");
            var requestStatus = await service.RequestAsync(
                userId,
                "alert-new@example.com",
                new Uri("https://app.example.com"),
                CancellationToken.None);
            Assert.Equal(EmailChangeRequestStatus.Success, requestStatus);

            var result = await service.ConfirmAsync(
                userId,
                verificationToken!,
                CancellationToken.None);

            Assert.Equal(EmailChangeConfirmStatus.Success, result.Status);
            Assert.Equal("alert-new@example.com", result.NewEmail);
            Assert.Equal(2, sendCount);

            var user = await db.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal("alert-new@example.com", user.PrimaryEmail);

            var token = await db.EmailChangeTokens.SingleAsync(t => t.UserId == userId);
            Assert.NotNull(token.ConsumedAt);
        }
    }

    private static async Task<(EmailChangeService Service, KoratDbContext Db)> BuildAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        var dbOptions = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new KoratDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var sender = new ResendEmailChangeEmailSender(
            new HttpClient(new DelegateHandler(sendAsync)),
            Options.Create(new ResendOptions
            {
                ApiKey = "test-key",
                FromEmail = "noreply@example.com",
                FromName = "Korat",
            }),
            NullLogger<ResendEmailChangeEmailSender>.Instance);

        var service = new EmailChangeService(
            db,
            sender,
            NullLogger<EmailChangeService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));
        return (service, db);
    }

    private static async Task<UserId> SeedUserAsync(KoratDbContext db, string email)
    {
        var userId = UserId.New();
        db.Users.Add(new User
        {
            Id = userId,
            PrimaryEmail = email,
            DisplayName = "Delivery Test User",
            CreatedAt = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
            Status = UserStatus.Active,
            IsAdmin = false,
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task<string> ExtractVerificationTokenAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var json = await request.Content!.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Resend request did not contain a text body.");
        var verificationUrl = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(verificationUrl).Query);
        return query["token"]
            ?? throw new InvalidOperationException("Verification URL did not contain a token.");
    }
}
