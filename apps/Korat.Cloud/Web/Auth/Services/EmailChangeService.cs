using System.Net.Mail;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Implements the email-change request flow: validates format and uniqueness, enforces
/// per-user rate limit, generates a hashed single-use token, and sends the verification
/// link to the <em>new</em> address.
///
/// Token pattern mirrors <see cref="CliTokenService"/>: only <c>SHA-256(raw)</c> is
/// persisted via <see cref="AuthTokens.Sha256Hex"/>; the raw bytes are sent once in
/// the magic-link and never stored.
///
/// InMemory race-safety disclaimer:
/// EF Core InMemory does not support raw SQL. The uniqueness and rate-limit checks use
/// LINQ (AnyAsync / CountAsync) for both InMemory (tests) and Postgres (production).
/// These are advisory fast-path checks only — the DB-level unique index on
/// User.PrimaryEmail is the real arbiter for the uniqueness invariant. Concurrent
/// requests racing past the advisory check will be rejected by the unique constraint
/// when Task 3's verify endpoint attempts the UPDATE. Sequential test execution
/// (DisableTestParallelization) keeps tests safe. The LINQ filters MUST mirror the
/// SQL WHERE clauses one-for-one.
/// </summary>
public sealed class EmailChangeService(
    KoratDbContext db,
    IEmailChangeEmailSender emailSender,
    ILogger<EmailChangeService> logger,
    TimeProvider time) : IEmailChangeService
{
    public static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);
    public const int MaxRequestsPerWindow = 5;

    public async Task<EmailChangeRequestStatus> RequestAsync(
        UserId userId,
        string newEmail,
        Uri appBaseUri,
        CancellationToken ct)
    {
        // 0. Syntactic validation at the service boundary so callers other than the
        //    endpoint cannot bypass the format guard and persist a malformed address.
        if (!IsValidEmail(newEmail))
        {
            logger.LogInformation("EmailChange rejected: invalid email format");
            return EmailChangeRequestStatus.InvalidEmailFormat;
        }

        var normalised = NormaliseEmail(newEmail);
        var now = time.GetUtcNow();

        // 1. Reject if requesting the same address that is already the primary.
        //    Checked before the broad duplicate scan so the user gets a clear,
        //    distinct signal instead of the confusing "email already in use" message.
        var currentUser = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (currentUser is not null &&
            NormaliseEmail(currentUser.PrimaryEmail) == normalised)
        {
            logger.LogInformation("EmailChange rejected: requested email is same as current for user {UserId}", userId.Value);
            return EmailChangeRequestStatus.SameAsCurrentEmail;
        }

        // 2. Advisory uniqueness check — fast-path 409 before spending round trips on
        //    token issuance. The DB-level unique index on User.PrimaryEmail (migration
        //    AddEmailChangeToken) is the true arbiter; concurrent races that slip past
        //    this check will be rejected by the constraint at Task 3's verify step.
        var emailTaken = await db.Users
            .AnyAsync(u => u.PrimaryEmail == normalised, ct);
        if (emailTaken)
        {
            logger.LogInformation("EmailChange rejected: {Email} already in use", HashForLog(normalised));
            return EmailChangeRequestStatus.EmailAlreadyInUse;
        }

        // 3. Per-user rate limit: max MaxRequestsPerWindow issuance events within
        //    RateLimitWindow. Superseded rows are deliberately counted — this prevents
        //    a user from draining the window by repeatedly issuing and cancelling.
        var windowStart = now - RateLimitWindow;
        var recentCount = await db.EmailChangeTokens
            .CountAsync(t => t.UserId == userId && t.CreatedAt > windowStart, ct);
        if (recentCount >= MaxRequestsPerWindow)
        {
            logger.LogInformation("EmailChange rate-limited for user {UserId}", userId.Value);
            return EmailChangeRequestStatus.RateLimited;
        }

        // 4. Supersede any prior pending (unconsumed, not yet superseded) token for this user.
        // We soft-delete (mark SupersededAt) rather than hard-delete so the row is still
        // counted by the rate-limit query above on future requests — preserving the per-user
        // issuance-event window without a separate audit table.
        var prior = await db.EmailChangeTokens
            .Where(t => t.UserId == userId && t.ConsumedAt == null && t.SupersededAt == null)
            .ToListAsync(ct);
        foreach (var p in prior)
        {
            db.Entry(p).CurrentValues.SetValues(p with { SupersededAt = now });
        }

        // 5. Generate raw token, store only the SHA-256 hash.
        var rawToken = AuthTokens.GenerateRawBase64Url();
        var tokenHash = AuthTokens.Sha256Hex(rawToken);

        var token = new EmailChangeToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            NewEmail = normalised,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + TokenTtl,
            ConsumedAt = null,
        };
        db.EmailChangeTokens.Add(token);
        await db.SaveChangesAsync(ct);

        // 6. Send magic-link to the NEW address (ownership proof step).
        // The verify link is built from appBaseUri. appBaseUri is supplied by the
        // endpoint from Request.Scheme + Request.Host. In production, the host is
        // fixed by the TrustForwardedIp + Fly edge proxy configuration (Program.cs
        // §SEC-H1/L1) which rewrites Host from X-Forwarded-Host; this is the same
        // defensive posture used throughout the application. There is no separate
        // configured canonical-origin setting in this deployment — if one is added
        // in future it should be wired here instead of Request.Host.
        //
        // /app prefix is required: the SPA router basepath is /app (router.ts), vite
        // base is /app/ (vite.config.ts), and the server catch-all is MapFallback("/app/{*path}").
        // A bare /account/* path has no server route and returns 404.
        var verifyUrl = new Uri(appBaseUri, $"/app/account/verify-email?token={rawToken}");

        // If the mail send fails, the token row exists in the DB (counting toward the
        // rate-limit window) but the user has no link. Rethrow so the caller (endpoint)
        // returns a 5xx and the user can retry — a 202 with no link delivered is confusing.
        await emailSender.SendVerificationLinkAsync(normalised, verifyUrl, TokenTtl, ct);

        logger.LogInformation("EmailChange token issued for user {UserId} → {EmailHash}",
            userId.Value, HashForLog(normalised));

        return EmailChangeRequestStatus.Success;
    }

    /// <inheritdoc />
    public async Task<EmailChangeConfirmResult> ConfirmAsync(
        UserId userId,
        string rawToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);

        var tokenHash = AuthTokens.Sha256Hex(rawToken);
        var now = time.GetUtcNow();

        // Load the token row scoped to this user (prevents cross-user token usage).
        var token = await db.EmailChangeTokens
            .Where(t => t.UserId == userId && t.TokenHash == tokenHash)
            .SingleOrDefaultAsync(ct);

        if (token is null)
        {
            logger.LogInformation("EmailChange confirm: token not found for user {UserId}", userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        // Reject expired, consumed, or superseded tokens.
        if (token.ExpiresAt <= now)
        {
            logger.LogInformation("EmailChange confirm: expired token for user {UserId}", userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        if (token.ConsumedAt is not null)
        {
            logger.LogInformation("EmailChange confirm: already-consumed token for user {UserId}", userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        if (token.SupersededAt is not null)
        {
            logger.LogInformation("EmailChange confirm: superseded token for user {UserId}", userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        // Load the current user to capture their old primary email for the security alert.
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            logger.LogError("EmailChange confirm: user {UserId} not found in DB", userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        var oldEmail = user.PrimaryEmail;
        var newEmail = token.NewEmail;

        // Promote the new email in the database.
        // Production path: atomic parameterised UPDATE via ExecuteUpdateAsync (Postgres).
        // Test/InMemory path: change-tracking fallback (EF Core InMemory does not support
        // ExecuteUpdateAsync). Sequential test execution (DisableTestParallelization) keeps
        // InMemory safe; production Postgres unique index is the real concurrency arbiter.
        //
        // TOCTOU race: two concurrent ConfirmAsync calls for the same user (different tokens)
        // could both pass the advisory uniqueness check in RequestAsync and both reach here.
        // The second concurrent UPDATE will throw a DbUpdateException wrapping a Postgres
        // unique-constraint violation (SqlState 23505). We catch it and return ExpiredOrInvalid
        // so the user gets a graceful error rather than an unhandled 500. Data integrity is safe
        // because the DB unique index on User.PrimaryEmail is the real arbiter.
        var providerName = db.Database.ProviderName;
        try
        {
            if (providerName is not null && providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
            {
                var tracked = await db.Users.SingleAsync(u => u.Id == userId, ct);
                var updated = tracked with { PrimaryEmail = newEmail };
                db.Entry(tracked).CurrentValues.SetValues(updated);
            }
            else
            {
                var affected = await db.Users
                    .Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.PrimaryEmail, newEmail), ct);

                if (affected == 0)
                {
                    logger.LogError("EmailChange confirm: UPDATE affected 0 rows for user {UserId}", userId.Value);
                    return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
                }
            }

            // Mark the token consumed (single-use).
            db.Entry(token).CurrentValues.SetValues(token with { ConsumedAt = now });

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // Unique constraint violation on User.PrimaryEmail — concurrent confirm race.
            // Return ExpiredOrInvalid so the second request gets a graceful 410 response.
            logger.LogInformation(
                "EmailChange confirm: unique-constraint violation for user {UserId} — concurrent confirm race",
                userId.Value);
            return new EmailChangeConfirmResult(EmailChangeConfirmStatus.ExpiredOrInvalid);
        }

        // Send security-alert email to the OLD address AFTER the DB write so that an
        // exception during email delivery does not roll back the promotion — a duplicate
        // alert is safer than a silent failed alert. The mail is informational; the user
        // can recover via support if needed.
        // Observable failure: a silently swallowed exception here means the old address
        // never receives the account-takeover early-warning. Log at Error level so the
        // failure is detectable by monitoring / alerting on error-rate metrics.
        try
        {
            await emailSender.SendSecurityAlertAsync(oldEmail, newEmail, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "EmailChange confirm: failed to send security-alert email to old address for user {UserId}. " +
                "The email promotion succeeded but the old address was NOT notified.",
                userId.Value);
            // Do not rethrow — the DB write is already committed and returning an error here
            // would confuse the user (their email WAS changed). The observable log entry is
            // the intended monitoring signal.
        }

        logger.LogInformation(
            "EmailChange confirmed for user {UserId}: {OldEmailHash} → {NewEmailHash}",
            userId.Value, HashForLog(NormaliseEmail(oldEmail)), HashForLog(newEmail));

        return new EmailChangeConfirmResult(EmailChangeConfirmStatus.Success, newEmail);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static string NormaliseEmail(string email) =>
        (email ?? throw new ArgumentNullException(nameof(email))).Trim().ToLowerInvariant();

    /// <summary>
    /// Validates email format using <see cref="MailAddress"/> round-trip.
    /// Mirrors the endpoint's IsValidEmail guard so the invariant holds regardless
    /// of which entry point calls <see cref="RequestAsync"/>.
    /// Note: <see cref="MailAddress"/> is permissive — it accepts addresses that
    /// some mail servers would reject. The DB unique index on <c>User.PrimaryEmail</c>
    /// is the real arbiter for uniqueness; format validation here is a fast-path
    /// sanity check only.
    /// </summary>
    internal static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        try
        {
            var addr = new MailAddress(email.Trim());
            return addr.Address == email.Trim();
        }
        catch
        {
            return false;
        }
    }

    private static string HashForLog(string normalised)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(bytes.AsSpan(0, 8)); // first 64 bits only — privacy-light
    }
}
