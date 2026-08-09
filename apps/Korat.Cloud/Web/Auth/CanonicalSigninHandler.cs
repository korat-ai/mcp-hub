using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Korat.Cloud.Web.Auth;

public sealed record CanonicalSigninRequest(
    LoginProvider Provider,
    string ProviderUserId,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    string ReturnUrl);

public sealed class CanonicalSigninHandler(
    KoratDbContext db,
    ISessionService sessions,
    IPendingLinkService pendingLinks,
    IUserProvisioningService userProvisioning,
    IAuthResolver authResolver,
    IOptions<BootstrapOptions> bootstrapOptions,
    ILogger<CanonicalSigninHandler> logger,
    TimeProvider time)
{
    public const string SessionCookieName = "__Host-korat_session";
    public const string PendingLinkCookieName = "__Host-korat_link_pending";
    /// <summary>The ephemeral cookie bridging the OAuth callback to sign-in completion — its name
    /// is the single source of truth, referenced by the AddCookie scheme in Program.cs and deleted
    /// in <see cref="CompleteAsync"/>/<see cref="LinkAsync"/> once the callback's claims are consumed.</summary>
    public const string IntermediateSessionCookieName = "__Host-korat_session_intermediate";

    /// <summary>
    /// Deletes the ephemeral intermediate cookie. Direct <c>Response.Cookies.Delete</c> rather than
    /// <c>SignOutAsync</c>: the latter needs <c>IAuthenticationService</c> in the request scope
    /// (absent when the handler is invoked directly, e.g. unit tests → ArgumentNullException),
    /// whereas this is a plain expired Set-Cookie. Options must match the set cookie (Path=/ +
    /// Secure — the <c>__Host-</c> prefix requires both) for the browser to target it. Called at
    /// the top of both entry points so it is otherwise never left lingering (up to 10 min) as a
    /// consumer-less <c>ctx.User</c> principal that would bind antiforgery tokens to a stale identity.
    /// </summary>
    private static void ClearIntermediateCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(IntermediateSessionCookieName,
            new CookieOptions { Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });

    public async Task<IResult> CompleteAsync(HttpContext ctx, CanonicalSigninRequest req, CancellationToken ct)
    {
        // The intermediate cookie has served its purpose (the finish endpoint already extracted the
        // OAuth claims into `req`); clear it up front so EVERY exit path — success, pending-link
        // interstitial, and the failure redirects — is covered. See ClearIntermediateCookie.
        ClearIntermediateCookie(ctx);
        var normalisedEmail = req.Email?.Trim().ToLowerInvariant();
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var uaHash = string.IsNullOrEmpty(ua) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua)).AsSpan(0, 16));

        var configuredAdminEmail = bootstrapOptions.Value.AdminEmail?.Trim().ToLowerInvariant();
        var isAdminEmail = !string.IsNullOrEmpty(configuredAdminEmail)
                           && normalisedEmail is not null
                           && normalisedEmail == configuredAdminEmail;

        // 1. Existing ExternalLogin — returning user via the same IdP.
        var existing = await db.ExternalLogins
            .FirstOrDefaultAsync(x => x.Provider == req.Provider && x.ProviderUserId == req.ProviderUserId, ct);

        User? user;
        if (existing is not null)
        {
            user = await db.Users.SingleAsync(u => u.Id == existing.UserId, ct);

            // Idempotent promote: if the returning user's email matches Bootstrap:AdminEmail
            // and IsAdmin is not yet set, elevate them now.
            if (isAdminEmail && !user.IsAdmin)
            {
                db.Entry(user).CurrentValues.SetValues(user with { IsAdmin = true });
                await db.SaveChangesAsync(ct);
                // Refresh the local reference to reflect the updated value.
                user = await db.Users.SingleAsync(u => u.Id == existing.UserId, ct);
                logger.LogInformation(
                    "Returning user {UserId} promoted to admin via Bootstrap:AdminEmail match", user.Id);
            }
        }
        else if (req.EmailVerified && normalisedEmail is not null)
        {
            // Verified email — check for auto-link candidate.
            var byEmail = await db.Users.FirstOrDefaultAsync(u =>
                u.PrimaryEmail == normalisedEmail && u.Status == UserStatus.Active, ct);
            if (byEmail is not null)
            {
                // Idempotent promote before routing through the interstitial.
                if (isAdminEmail && !byEmail.IsAdmin)
                {
                    db.Entry(byEmail).CurrentValues.SetValues(byEmail with { IsAdmin = true });
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Auto-link candidate {UserId} promoted to admin via Bootstrap:AdminEmail match", byEmail.Id);
                }

                // Route through interstitial — NEVER silently merge.
                var pending = new PendingLink(byEmail.Id, req.Provider, req.ProviderUserId,
                                              normalisedEmail, req.DisplayName,
                                              time.GetUtcNow().AddMinutes(10));
                var token = pendingLinks.Issue(pending);
                ctx.Response.Cookies.Append(PendingLinkCookieName, token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = TimeSpan.FromMinutes(10),
                });
                logger.LogInformation("Pending cross-IdP link issued for user {UserId} via {Provider}", byEmail.Id, req.Provider);
                return Results.Redirect("/app/signin/link-confirm");
            }

            // New signup. Registration is open; Bootstrap:AdminEmail only decides whether the
            // new account is an admin, not whether it may be created at all.
            user = isAdminEmail
                ? await CreateAdminUserAsync(req, normalisedEmail, ip, uaHash, ct)
                : await CreateUserAsync(req, normalisedEmail, ct);

            if (user is null)
            {
                // Defensive: both paths either return a user or throw.
                logger.LogWarning("Signin produced no user for {Provider}", req.Provider);
                return Results.Redirect("/app/signin?error=signin_failed");
            }
        }
        else
        {
            // Unverified email — cannot auto-link, cannot create new account.
            logger.LogInformation("Signin rejected — unverified email from {Provider}", req.Provider);
            return Results.Redirect("/app/signin?error=unverified_email");
        }

        var session = await sessions.CreateAsync(user.Id, ua, ip, ct);
        ctx.Response.Cookies.Append(SessionCookieName, session.Id.ToString("N"), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = SessionService.SlidingWindow,
        });
        var safeReturn = IsSafeReturnUrl.Check(req.ReturnUrl) ? req.ReturnUrl : "/app/";
        return Results.Redirect(safeReturn);
    }

    /// <summary>
    /// "Connect provider" (027): links the OAuth-proven identity in <paramref name="req"/>
    /// to the already-authenticated user — WITHOUT requiring the new provider's email to
    /// match the account's. The link target is the LIVE session user (re-resolved here),
    /// cross-checked against the signed <paramref name="expectedUserId"/> from the state.
    /// Keeps the current session (no new login).
    /// </summary>
    public async Task<IResult> LinkAsync(HttpContext ctx, CanonicalSigninRequest req, Guid expectedUserId, CancellationToken ct)
    {
        // Connect-provider also mints an intermediate cookie (carrying the NEW provider's identity)
        // in the OAuth callback; clear it on entry so none of LinkAsync's exit paths leave it live.
        ClearIntermediateCookie(ctx);
        const string returnDefault = "/app/account/profile";
        var safeReturn = IsSafeReturnUrl.Check(req.ReturnUrl) ? req.ReturnUrl : returnDefault;

        // Re-resolve the live session — the link target is the current session user, not
        // merely whatever the (signed) state claimed. Reject if it changed or expired.
        var identity = await authResolver.ResolveAsync(ctx, ct);
        if (identity is null || identity.UserId.Value != expectedUserId)
        {
            logger.LogWarning(
                "Provider-link rejected — no live session or session/user mismatch (expected {Expected})", expectedUserId);
            return Results.Redirect($"{returnDefault}?error=link_session");
        }

        // Linking a proven identity still requires it be verified, consistent with signin.
        if (!req.EmailVerified)
            return Results.Redirect($"{returnDefault}?error=link_unverified");

        var existing = await db.ExternalLogins
            .FirstOrDefaultAsync(x => x.Provider == req.Provider && x.ProviderUserId == req.ProviderUserId, ct);
        if (existing is not null)
        {
            // Already linked to this account — idempotent success (no duplicate row).
            if (existing.UserId == identity.UserId)
                return Results.Redirect(safeReturn);

            // The identity belongs to a different account — never reassign it.
            logger.LogWarning(
                "Provider-link rejected — {Provider} identity already linked to a different user", req.Provider);
            return Results.Redirect($"{returnDefault}?error=provider_in_use");
        }

        db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = identity.UserId,
            Provider = req.Provider,
            ProviderUserId = req.ProviderUserId,
            EmailAtLink = req.Email?.Trim().ToLowerInvariant() ?? string.Empty,
            EmailVerified = req.EmailVerified,
            LinkedAt = time.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // Concurrent link race: the explicit check above passed for two requests, then the
            // (Provider, ProviderUserId) unique index rejected the second insert. Translate the
            // DB rejection into the same graceful conflict path instead of a raw 500.
            logger.LogWarning("Provider-link lost race — {Provider} identity claimed concurrently", req.Provider);
            return Results.Redirect($"{returnDefault}?error=provider_in_use");
        }
        logger.LogInformation("Provider {Provider} linked to user {UserId} via connect flow", req.Provider, identity.UserId);

        return Results.Redirect(safeReturn);
    }

    /// <summary>
    /// Provisions a brand-new admin user without requiring an invite.
    /// Called only when <c>Bootstrap:AdminEmail</c> is configured and the signing-in
    /// email matches it and no existing user/external-login was found for this IdP identity.
    /// </summary>
    private async Task<User?> CreateAdminUserAsync(CanonicalSigninRequest req, string email, string? ip, string? uaHash, CancellationToken ct)
    {
        try
        {
            var displayName = req.DisplayName ?? email;
            var (user, _) = await userProvisioning.CreateUserWithDefaultSpaceAsync(email, displayName, ct, isAdmin: true);

            db.ExternalLogins.Add(new ExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Provider = req.Provider,
                ProviderUserId = req.ProviderUserId,
                EmailAtLink = email,
                EmailVerified = req.EmailVerified,
                LinkedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Bootstrap admin {UserId} provisioned via {Provider} (Bootstrap:AdminEmail match — no invite required)",
                user.Id, req.Provider);
            return user;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Admin user insert failed for Bootstrap:AdminEmail match via {Provider}.", req.Provider);
            throw;
        }
    }

    private async Task<User?> CreateUserAsync(CanonicalSigninRequest req, string email, CancellationToken ct)
    {
        // Open registration: anyone who reaches /app/signin and proves control of an email
        // address (magic link) or an OAuth identity gets an account and their own Space.
        // The invite gate that used to stand here was removed with the closed beta.
        // NOTE: the gate was also the only ceiling on account creation. Rate limits on
        // POST /signin/magic-link are now the sole barrier against signup floods.
        var displayName = req.DisplayName ?? email;
        var (user, _) = await userProvisioning.CreateUserWithDefaultSpaceAsync(email, displayName, ct);

        db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = req.Provider,
            ProviderUserId = req.ProviderUserId,
            EmailAtLink = email,
            EmailVerified = req.EmailVerified,
            LinkedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("New user {UserId} created via {Provider}", user.Id, req.Provider);
        return user;
    }
}
