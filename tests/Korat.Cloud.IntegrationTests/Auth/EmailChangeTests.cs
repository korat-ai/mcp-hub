using System.Net;
using System.Net.Http.Json;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for POST /api/auth/email/change (Task 2 of SP3).
///
/// Covers:
///  - Happy path: 202 Accepted, hashed token stored, verification link sent to new address.
///  - Conflict: 409 when new email already belongs to another user.
///  - Rate limit: 429 when the same user makes &gt;= 5 requests in an hour.
///  - Antiforgery: 400 without X-XSRF-TOKEN (shared with AntiforgeryEnforcementTests).
///
/// InMemory race-safety disclaimer: EF Core InMemory cannot serialise concurrent writes.
/// Sequential test execution is guaranteed by <see cref="AssemblyInfo"/> (DisableTestParallelization).
/// </summary>
public sealed class EmailChangeTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    // ── Helper: open a scoped DbContext from the test factory ─────────────────

    private KoratDbContext OpenDb()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KoratDbContext>();
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestEmailChange_StoresHashedToken_SendsLinkToNewAddress()
    {
        var seeded = await fixture.SeedUserAsync($"ec-happy-{Guid.NewGuid():N}@example.com", "Happy User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail = $"ec-happy-new-{Guid.NewGuid():N}@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Verify token was stored with a hash (not the raw value).
        await using var db = OpenDb();
        var token = await db.EmailChangeTokens
            .Where(t => t.UserId == seeded.UserId)
            .SingleOrDefaultAsync();
        Assert.NotNull(token);
        // TokenHash must be a 64-char hex string (SHA-256), never the raw token.
        Assert.Equal(64, token!.TokenHash.Length);
        Assert.Matches("^[0-9A-Fa-f]{64}$", token.TokenHash);
        // The hash itself must not contain the URL path (i.e. the raw token is not the hash).
        Assert.DoesNotContain("verify-email", token.TokenHash);

        // Verify a verification-link email was sent to the new address. The ConcurrentBag
        // accumulates across all tests in this fixture; we scope the assertion to the specific
        // email that was used in this test.
        var newEmail = token!.NewEmail;
        var sentMails = fixture.Factory.EmailChangeEmailSender.SentVerifications;
        Assert.Contains(sentMails, m =>
            m.To.Equals(newEmail, StringComparison.OrdinalIgnoreCase)
            && m.Body.Contains("/app/account/verify-email?token="));
    }

    [Fact]
    public async Task RequestEmailChange_WhenEmailInUse_Returns202ForAntiEnumeration()
    {
        // Anti-enumeration: the endpoint returns 202 even when the address belongs to
        // another account, so a signed-in attacker cannot probe which addresses are registered.
        // This mirrors the SP1 magic-link posture (MagicLinkEndpoints "anti-enumeration" comment).
        // The DB unique index on User.PrimaryEmail still enforces uniqueness at confirm time.
        var seeded = await fixture.SeedUserAsync($"ec-conflict-{Guid.NewGuid():N}@example.com", "Owner");
        var takenEmail = $"ec-taken-{Guid.NewGuid():N}@example.com";
        await fixture.SeedUserAsync(takenEmail, "Taken User");

        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);
        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail = takenEmail });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task RequestEmailChange_RateLimited_Returns429()
    {
        var seeded = await fixture.SeedUserAsync($"ec-rate-{Guid.NewGuid():N}@example.com", "Rate User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Send MaxRequestsPerWindow requests (each to a distinct email to avoid 409).
        for (var i = 0; i < EmailChangeService.MaxRequestsPerWindow; i++)
        {
            var r = await client.PostAsJsonAsync("/api/auth/email/change",
                new { newEmail = $"ec-rate-target-{Guid.NewGuid():N}-{i}@example.com" });
            // First MaxRequestsPerWindow must succeed (202) so the rate-limit counter is accurate.
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        }

        // The next request must be rate-limited.
        var resp = await client.PostAsJsonAsync("/api/auth/email/change",
            new { newEmail = $"ec-rate-final-{Guid.NewGuid():N}@example.com" });

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Fact]
    public async Task RequestEmailChange_WithoutAntiforgeryToken_Returns400()
    {
        // Authenticated (full-scope cookie) but no antiforgery token: the scope filter passes,
        // then antiforgery rejects the mutating request with 400. (Unauthenticated → 401.)
        var seeded = await fixture.SeedUserAsync($"ec-noxsrf-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail = "x@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RequestEmailChange_InvalidEmailFormat_Returns400()
    {
        var seeded = await fixture.SeedUserAsync($"ec-invalid-{Guid.NewGuid():N}@example.com", "Invalid User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail = "not-an-email" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task RequestEmailChange_SameAsCurrentEmail_Returns400WithDistinctError()
    {
        var email = $"ec-same-{Guid.NewGuid():N}@example.com";
        var seeded = await fixture.SeedUserAsync(email, "Same User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Requesting the exact same email that is already the user's primary email.
        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail = email });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // Must return "same-as-current", not "email-in-use" (which would be confusing).
        Assert.Contains("same-as-current", body);
    }

    [Fact]
    public async Task RequestEmailChange_SecondRequest_SupersedesPriorPendingToken()
    {
        var seeded = await fixture.SeedUserAsync($"ec-replace-{Guid.NewGuid():N}@example.com", "Replace User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // First request — creates a pending token.
        await client.PostAsJsonAsync("/api/auth/email/change",
            new { newEmail = $"ec-replace-first-{Guid.NewGuid():N}@example.com" });

        // Second request — should supersede the first token and create a new one.
        await client.PostAsJsonAsync("/api/auth/email/change",
            new { newEmail = $"ec-replace-second-{Guid.NewGuid():N}@example.com" });

        await using var db = OpenDb();
        // Only one active (unconsumed, unsuperseded) pending token per user at any time.
        var activeCount = await db.EmailChangeTokens
            .CountAsync(t => t.UserId == seeded.UserId && t.ConsumedAt == null && t.SupersededAt == null);
        Assert.Equal(1, activeCount);

        // The first token is still in the table (soft-deleted) for rate-limit counting.
        var totalCount = await db.EmailChangeTokens
            .CountAsync(t => t.UserId == seeded.UserId);
        Assert.Equal(2, totalCount);
    }
}

// ── Task 3 shared test infrastructure ────────────────────────────────────────

/// <summary>
/// Minimal controllable <see cref="TimeProvider"/> for Task 3 confirm tests.
/// Defined locally so the integration-test project has no dependency on
/// Korat.Auth.Tests (which is a sibling test project, not a shared library).
/// </summary>
internal sealed class FakeTimeProviderEc(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

// ── Task 3 — Confirm endpoint: service-level unit tests ──────────────────────
// These tests exercise EmailChangeService.ConfirmAsync in isolation (no HTTP,
// no Orleans). Each builds its own InMemory EF database and FakeTimeProvider
// so clock manipulation is deterministic.
//
// InMemory race-safety disclaimer: EF Core InMemory does not support raw SQL.
// The email-promotion path uses the change-tracking fallback that is correct
// for sequential test execution (enforced by AssemblyInfo DisableTestParallelization).
// Concurrent serialisation is only guaranteed by the Postgres unique index on
// User.PrimaryEmail — validated at manual integration / deploy time.

/// <summary>
/// Unit tests for <see cref="EmailChangeService.ConfirmAsync"/>:
/// valid token, expired token, double-consume (used token).
/// </summary>
public sealed class EmailChangeConfirmServiceTests
{
    private sealed class CapturingAlertSender : IEmailChangeEmailSender
    {
        public List<(string To, string NewEmail)> Alerts { get; } = new();
        public List<(string To, string Body)> SentVerifications { get; } = new();

        public Task SendVerificationLinkAsync(string toEmail, Uri verifyUrl, TimeSpan ttl, CancellationToken ct)
        {
            SentVerifications.Add((toEmail, verifyUrl.ToString()));
            return Task.CompletedTask;
        }

        public Task SendSecurityAlertAsync(string toEmail, string newEmail, CancellationToken ct)
        {
            Alerts.Add((toEmail, newEmail));
            return Task.CompletedTask;
        }
    }

    private static (EmailChangeService svc, KoratDbContext db, FakeTimeProviderEc time, CapturingAlertSender sender)
        Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new KoratDbContext(opts);
        var time = new FakeTimeProviderEc(DateTimeOffset.UtcNow);
        var sender = new CapturingAlertSender();
        var svc = new EmailChangeService(db, sender, NullLogger<EmailChangeService>.Instance, time);
        return (svc, db, time, sender);
    }

    /// <summary>Seed a user row directly (no Orleans provisioning required here).</summary>
    private static async Task<(UserId id, string email)> SeedUserAsync(KoratDbContext db, string email)
    {
        var userId = UserId.New();
        db.Users.Add(new User
        {
            Id = userId,
            PrimaryEmail = email,
            DisplayName = "Test User",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = UserStatus.Active,
            IsAdmin = false,
        });
        await db.SaveChangesAsync();
        return (userId, email);
    }

    [Fact]
    public async Task ConfirmAsync_ValidToken_PromotesPrimaryEmail_AlertsOldAddress()
    {
        var (svc, db, _, sender) = Build();
        var (userId, oldEmail) = await SeedUserAsync(db, "old@example.com");
        var appBase = new Uri("https://app.example.com");

        // Issue a token.
        var status = await svc.RequestAsync(userId, "new@example.com", appBase, default);
        Assert.Equal(EmailChangeRequestStatus.Success, status);

        // Extract the raw token from the verification URL captured by the sender.
        var verifyUrl = sender.SentVerifications.Single().Body;
        var rawToken = ExtractToken(verifyUrl);

        // Confirm.
        var result = await svc.ConfirmAsync(userId, rawToken, default);
        Assert.Equal(EmailChangeConfirmStatus.Success, result.Status);
        Assert.Equal("new@example.com", result.NewEmail);

        // DB row reflects promoted email.
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal("new@example.com", user.PrimaryEmail);

        // Security alert sent to old address.
        Assert.Single(sender.Alerts);
        Assert.Equal(oldEmail, sender.Alerts[0].To);
        Assert.Contains("new@example.com", sender.Alerts[0].NewEmail);

        // Token is consumed (single-use).
        var token = await db.EmailChangeTokens.SingleAsync(t => t.UserId == userId);
        Assert.NotNull(token.ConsumedAt);
    }

    [Fact]
    public async Task ConfirmAsync_ExpiredToken_ReturnsExpiredOrInvalid()
    {
        var (svc, db, time, sender) = Build();
        var (userId, _) = await SeedUserAsync(db, "owner@example.com");
        var appBase = new Uri("https://app.example.com");

        await svc.RequestAsync(userId, "new2@example.com", appBase, default);
        var verifyUrl = sender.SentVerifications.Single().Body;
        var rawToken = ExtractToken(verifyUrl);

        // Advance clock past TTL.
        time.Advance(EmailChangeService.TokenTtl + TimeSpan.FromSeconds(1));

        var result = await svc.ConfirmAsync(userId, rawToken, default);
        Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, result.Status);

        // Email must NOT have been promoted.
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal("owner@example.com", user.PrimaryEmail);
    }

    // ── Cov C4: cross-user token rejection ───────────────────────────────────

    [Fact]
    public async Task ConfirmAsync_CrossUser_TokenMintedForUserB_IsRejectedForUserA()
    {
        // Prove that ConfirmAsync.UserId-scoped lookup (EmailChangeService.cs:157-158)
        // prevents user A from consuming a token that was issued for user B.
        // The DB query includes `t.UserId == userId` so user A's lookup returns null
        // for user B's token, and the result is ExpiredOrInvalid.
        var (svc, db, _, senderB) = Build();
        var appBase = new Uri("https://app.example.com");

        // Seed user A and user B independently.
        var (userAId, _) = await SeedUserAsync(db, "user-a@example.com");
        var (userBId, _) = await SeedUserAsync(db, "user-b@example.com");

        // Issue a token for user B targeting a new email.
        var statusB = await svc.RequestAsync(userBId, "user-b-new@example.com", appBase, default);
        Assert.Equal(EmailChangeRequestStatus.Success, statusB);

        // Extract the raw token from the email sent to user B.
        var verifyUrlB = senderB.SentVerifications.Single().Body;
        var rawTokenForB = ExtractToken(verifyUrlB);

        // User A attempts to consume user B's token — must be rejected.
        var result = await svc.ConfirmAsync(userAId, rawTokenForB, default);

        Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, result.Status);

        // User B's email must NOT have been promoted (token not consumed by A).
        var userB = await db.Users.SingleAsync(u => u.Id == userBId);
        Assert.Equal("user-b@example.com", userB.PrimaryEmail);
    }

    [Fact]
    public async Task ConfirmAsync_UsedToken_ReturnsExpiredOrInvalid()
    {
        var (svc, db, _, sender) = Build();
        var (userId, _) = await SeedUserAsync(db, "user@example.com");
        var appBase = new Uri("https://app.example.com");

        await svc.RequestAsync(userId, "new3@example.com", appBase, default);
        var verifyUrl = sender.SentVerifications.Single().Body;
        var rawToken = ExtractToken(verifyUrl);

        // First confirm succeeds.
        var first = await svc.ConfirmAsync(userId, rawToken, default);
        Assert.Equal(EmailChangeConfirmStatus.Success, first.Status);

        // Second confirm with the same token must fail.
        var second = await svc.ConfirmAsync(userId, rawToken, default);
        Assert.Equal(EmailChangeConfirmStatus.ExpiredOrInvalid, second.Status);
    }

    private static string ExtractToken(string verifyUrl)
    {
        // URL form: https://host/app/account/verify-email?token=<raw>
        var uri = new Uri(verifyUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["token"] ?? throw new InvalidOperationException($"No token in URL: {verifyUrl}");
    }
}

/// <summary>
/// HTTP integration test for POST /api/auth/email/change/confirm (Task 3).
/// Exercises the full stack: endpoint → service → user grain (email promotion).
/// </summary>
public sealed class EmailChangeConfirmEndpointTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ConfirmEmailChange_ValidToken_PromotesEmail_AlertsOldAddress()
    {
        var oldEmail = $"confirm-old-{Guid.NewGuid():N}@example.com";
        var newEmail = $"confirm-new-{Guid.NewGuid():N}@example.com";
        var seeded = await fixture.SeedUserAsync(oldEmail, "Confirm User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Request the change — sends verification link to newEmail.
        var reqResp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail });
        Assert.Equal(HttpStatusCode.Accepted, reqResp.StatusCode);

        // Extract the raw token from the captured verification URL.
        var sentVerifications = fixture.Factory.EmailChangeEmailSender.SentVerifications;
        var verifyEntry = sentVerifications.First(m =>
            m.To.Equals(newEmail, StringComparison.OrdinalIgnoreCase));
        var uri = new Uri(verifyEntry.Body);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var rawToken = query["token"]!;
        Assert.False(string.IsNullOrEmpty(rawToken), "Verification URL must contain a token query param.");

        // Confirm.
        var confirmResp = await client.PostAsJsonAsync(
            "/api/auth/email/change/confirm", new { token = rawToken });
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);

        // GET /api/auth/me must reflect the new email.
        var meResp = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/auth/me");
        Assert.Equal(newEmail, meResp!.RootElement.GetProperty("email").GetString());

        // Security-alert email sent to old address.
        var alerts = fixture.Factory.EmailChangeEmailSender.SentAlerts;
        Assert.Contains(alerts, a =>
            a.To.Equals(oldEmail, StringComparison.OrdinalIgnoreCase) &&
            a.Body.Contains("security"));
    }

    [Fact]
    public async Task ConfirmEmailChange_InvalidToken_Returns410()
    {
        var seeded = await fixture.SeedUserAsync(
            $"confirm-bad-{Guid.NewGuid():N}@example.com", "Bad Token User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var resp = await client.PostAsJsonAsync(
            "/api/auth/email/change/confirm", new { token = "completely-wrong-token" });

        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmailChange_WithoutAntiforgeryToken_Returns400()
    {
        // Authenticated (full-scope cookie) but no antiforgery token: the scope filter passes,
        // then antiforgery rejects the mutating request with 400. (Unauthenticated → 401.)
        var seeded = await fixture.SeedUserAsync($"ec-confirm-noxsrf-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var resp = await client.PostAsJsonAsync(
            "/api/auth/email/change/confirm", new { token = "any" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
