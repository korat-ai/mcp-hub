using System.Security.Cryptography;
using System.Text;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth.Services;

public sealed class MagicLinkService(
    KoratDbContext db,
    IEmailSender emailSender,
    ILogger<MagicLinkService> logger,
    TimeProvider time) : IMagicLinkService
{
    // TTL must be >= GlobalPerEmailCooldown, otherwise a link can expire while the
    // per-email cooldown still blocks issuing a new one — a dead zone where the user
    // is locked out (link dead, no resend possible). Keeping them equal (1h) means a
    // freshly-issued link stays valid for the entire window before another can be sent.
    public static readonly TimeSpan TokenTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan GlobalPerEmailCooldown = TimeSpan.FromHours(1);

    public async Task IssueAsync(string email, string? ip, string? uaHash, Uri appBaseUri, CancellationToken ct)
    {
        var normalised = NormaliseEmail(email);
        var now = time.GetUtcNow();

        // Global per-email rate limit (NOT per-IP — prevents distributed mail-bomb against single inbox).
        var recentForEmail = await db.MagicLinkTokens
            .Where(t => t.Email == normalised && t.IssuedAt > now - GlobalPerEmailCooldown)
            .AnyAsync(ct);
        if (recentForEmail)
        {
            logger.LogInformation("MagicLink suppressed (rate limit) for email-hash {Hash}", HashEmailForLog(normalised));
            return;  // silently succeed — anti-enumeration: caller always sees 204
        }

        // F5: generate an opaque random token. Only the SHA-256 hash is persisted;
        // the raw token travels in the emailed URL and is never stored at rest.
        // Pattern mirrors CliTokenService / EmailChangeService.
        var rawToken = AuthTokens.GenerateRawBase64Url();
        var tokenHash = AuthTokens.Sha256Hex(rawToken);

        var token = new MagicLinkToken
        {
            Id = Guid.NewGuid(),
            TokenHash = tokenHash,
            Email = normalised,
            IssuedAt = now,
            ExpiresAt = now + TokenTtl,
            ConsumedAt = null,
            IssuedFromIp = ip,
            IssuedUaHash = uaHash,
            ConsumedFromIp = null,
            ConsumedUaHash = null,
        };
        db.MagicLinkTokens.Add(token);
        await db.SaveChangesAsync(ct);

        var consumeUrl = new Uri(appBaseUri, $"/signin/magic-link/consume?token={rawToken}");
        await emailSender.SendMagicLinkAsync(normalised, consumeUrl, TokenTtl, ct);
        logger.LogInformation("MagicLink issued for email-hash {Hash}", HashEmailForLog(normalised));
    }

    public async Task<MagicLinkConsumeResult?> TryConsumeAsync(string rawToken, string? ip, string? uaHash, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;

        var tokenHash = AuthTokens.Sha256Hex(rawToken);
        var now = time.GetUtcNow();

        string email;
        string? issuedFromIp;
        string? issuedUaHash;

        if (db.Database.IsInMemory())
        {
            // ─────────────────────────────────────────────────────────────────
            // InMemory race-safety disclaimer
            // EF Core InMemory does not support raw SQL and cannot serialise
            // concurrent UPDATE statements. This LINQ fallback exists for unit-
            // test ergonomics ONLY. Production uses the Postgres branch below,
            // which is validated by the integration test in Task 14.
            // The LINQ filter MUST mirror the SQL WHERE clause one-for-one.
            // ─────────────────────────────────────────────────────────────────
            var record = await db.MagicLinkTokens.FirstOrDefaultAsync(t =>
                t.TokenHash == tokenHash
                && t.ConsumedAt == null
                && t.ExpiresAt > now, ct);
            if (record is null) return null;
            var updated = record with { ConsumedAt = now, ConsumedFromIp = ip, ConsumedUaHash = uaHash };
            db.Entry(record).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            email = record.Email;
            issuedFromIp = record.IssuedFromIp;
            issuedUaHash = record.IssuedUaHash;
        }
        else
        {
            var rows = await db.Database.SqlQuery<ConsumeRow>($@"
                UPDATE ""MagicLinkToken""
                   SET ""ConsumedAt""      = {now},
                       ""ConsumedFromIp""  = {ip},
                       ""ConsumedUaHash""  = {uaHash}
                 WHERE ""TokenHash""    = {tokenHash}
                   AND ""ConsumedAt""   IS NULL
                   AND ""ExpiresAt""    > {now}
                RETURNING ""Email"", ""IssuedFromIp"", ""IssuedUaHash""
            ").ToListAsync(ct);

            if (rows.Count == 0) return null;
            var row = rows[0];
            email = row.Email;
            issuedFromIp = row.IssuedFromIp;
            issuedUaHash = row.IssuedUaHash;
        }

        var divergence = (issuedFromIp is not null && issuedFromIp != ip)
                      || (issuedUaHash is not null && issuedUaHash != uaHash);
        if (divergence)
        {
            logger.LogWarning(
                "MagicLink consume IP/UA divergence: issued IP={IssuedIp} UA={IssuedUa}, consumed IP={ConsumedIp} UA={ConsumedUa}",
                issuedFromIp, issuedUaHash, ip, uaHash);
        }
        return new MagicLinkConsumeResult(email, divergence);
    }

    public static string NormaliseEmail(string email) =>
        (email ?? throw new ArgumentNullException(nameof(email))).Trim().ToLowerInvariant();

    private static string HashEmailForLog(string normalisedEmail)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalisedEmail));
        return Convert.ToHexString(bytes.AsSpan(0, 8));  // log only first 64 bits — privacy-light
    }

    private sealed record ConsumeRow(string Email, string? IssuedFromIp, string? IssuedUaHash);
}
