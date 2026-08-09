using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Auth.Endpoints;

public static class AuthApiEndpoints
{
    public static IEndpointRouteBuilder MapAuthApiEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/auth/me — reads profile through the user grain (grains-are-the-cache).
        // Also reads pending email-change state directly from EmailChangeToken rows; this is
        // transient token metadata, not profile state, so a direct DB read is appropriate here.
        app.MapGet("/api/auth/me", async (
            HttpContext ctx,
            IAuthResolver resolver,
            IClusterClient cluster,
            KoratDbContext db,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var identity = await resolver.ResolveAsync(ctx, ct);
            if (identity is null) return Results.Unauthorized();

            var grain = cluster.GetGrain<IUserGrain>(identity.UserId.Value.ToString("N"));
            var user = await grain.GetAsync();
            if (user is null) return Results.Unauthorized();

            // Surface any outstanding email-change request so the frontend can show
            // "Verification pending for new@x — expires in N min" after a reload.
            // Spec §3.2 / review finding: local React state is lost on navigation.
            var now = time.GetUtcNow();
            var pending = await db.EmailChangeTokens
                .Where(t => t.UserId == identity.UserId
                         && t.ConsumedAt == null
                         && t.SupersededAt == null
                         && t.ExpiresAt > now)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new { t.NewEmail, t.ExpiresAt })
                .FirstOrDefaultAsync(ct);

            var providers = await LoadProvidersAsync(db, identity.UserId, ct);
            return Results.Ok(MeResponse.From(user, providers, pending is not null
                ? new PendingEmailChangeDto(pending.NewEmail, pending.ExpiresAt)
                : null));
        }).RequireRateLimiting(RateLimiterRegistration.AuthMePolicy);

        app.MapPut("/api/auth/me", async (
            HttpContext ctx,
            IClusterClient cluster,
            KoratDbContext db,
            UpdateMeRequest body,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            var trimmed = body.DisplayName?.Trim() ?? string.Empty;
            if (!DisplayNameRules.IsValidProfileDisplayName(trimmed))
                return Results.BadRequest(new
                {
                    error = "display-name-invalid",
                    message = DisplayNameRules.ProfileDisplayNameValidationMessage(),
                });

            // Grains are the cache: write through the user grain so the in-memory copy stays consistent.
            var grain = cluster.GetGrain<IUserGrain>(userId.Value.ToString("N"));
            var updated = await grain.UpdateDisplayNameAsync(trimmed);

            var providers = await LoadProvidersAsync(db, userId, ct);
            return Results.Ok(MeResponse.From(updated, providers));
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        app.MapPost("/api/auth/signout", async (HttpContext ctx, ISessionService sessions, CancellationToken ct) =>
        {
            if (ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.SessionCookieName, out var raw)
                && Guid.TryParse(raw, out var sessionId))
            {
                await sessions.RevokeAsync(sessionId, ct);
            }
            ctx.Response.Cookies.Delete(CanonicalSigninHandler.SessionCookieName, new CookieOptions
            {
                Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax,
            });
            return Results.NoContent();
        }).RequireRateLimiting(RateLimiterRegistration.SignoutPolicy)
          .RequireAntiforgeryValidation();

        app.MapGet("/api/auth/sessions", async (HttpContext ctx, ISessionService sessions, CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            // Identify the caller's own session so the frontend can badge it "This device".
            // The cookie stores the raw Guid (ToString("N"), no hyphens). Tolerate both formats.
            Guid? callerSessionId = null;
            if (ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.SessionCookieName, out var raw)
                && Guid.TryParse(raw, out var parsedId))
            {
                callerSessionId = parsedId;
            }

            var list = await sessions.ListActiveAsync(userId, ct);
            return Results.Ok(list.Select(s => new
            {
                id = s.Id,
                userAgent = s.UserAgent,
                createdFromIp = s.CreatedFromIp,
                createdAt = s.CreatedAt,
                lastUsedAt = s.LastUsedAt,
                expiresAt = s.ExpiresAt,
                current = callerSessionId.HasValue && s.Id == callerSessionId.Value,
            }));
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy);

        app.MapPost("/api/auth/sessions/{id:guid}/revoke",
            async (Guid id, HttpContext ctx, ISessionService sessions, Korat.Persistence.KoratDbContext db, CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var owns = await db.AuthSessions.AnyAsync(s => s.Id == id && s.UserId == userId, ct);
            if (!owns) return Results.NotFound();  // cloaked-403 → 404
            await sessions.RevokeAsync(id, ct);
            return Results.NoContent();
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        // Revoke every active session EXCEPT the caller's current one ("Revoke all other sessions").
        app.MapPost("/api/auth/sessions/revoke-others",
            async (HttpContext ctx, ISessionService sessions, CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            var currentSessionId = Guid.Empty;
            if (ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.SessionCookieName, out var raw)
                && Guid.TryParse(raw, out var parsed))
            {
                currentSessionId = parsed;
            }

            await sessions.RevokeOthersAsync(userId, currentSessionId, ct);
            return Results.NoContent();
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        // POST /api/auth/email/change/confirm — validates hashed token (single-use, TTL-bounded),
        // promotes the new email via the service (DB write) and refreshes the user grain cache.
        // Returns 200 + { email } on success; 410 Gone for expired/used/missing tokens.
        // Requires antiforgery (token is in request body, not URL, so CSRF is a valid concern).
        app.MapPost("/api/auth/email/change/confirm", async (
            HttpContext ctx,
            IEmailChangeService emailChange,
            IClusterClient cluster,
            ConfirmEmailChangeRequest body,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.StatusCode(StatusCodes.Status410Gone);

            var result = await emailChange.ConfirmAsync(userId, body.Token.Trim(), ct);

            if (result.Status == EmailChangeConfirmStatus.ExpiredOrInvalid)
                return Results.Json(
                    new { error = "token-expired-or-invalid", message = "Link expired or already used — request a new change." },
                    statusCode: StatusCodes.Status410Gone);

            // Refresh grain in-memory cache (grains-are-the-cache: the service has already
            // written the new email to Postgres; we now propagate it to the grain's _state).
            var grain = cluster.GetGrain<IUserGrain>(userId.Value.ToString("N"));
            await grain.UpdatePrimaryEmailAsync(result.NewEmail!);

            return Results.Ok(new { email = result.NewEmail });
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        // POST /api/auth/email/change — initiates email-change verification flow.
        // Validates format, rejects already-in-use addresses (409), enforces per-user
        // rate limit (429), issues a hashed single-use token and mails the verify link.
        app.MapPost("/api/auth/email/change", async (
            HttpContext ctx,
            IEmailChangeService emailChange,
            IOptions<CliOptions> cliOpts,
            IWebHostEnvironment env,
            EmailChangeRequest body,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            // Fast-path format validation before hitting the service.
            // The service also validates (EmailChangeService.IsValidEmail) so direct
            // callers of IEmailChangeService get the same guard; this early-out is
            // purely for a cheaper 400 at the HTTP boundary.
            var raw = body.NewEmail?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(raw) || !EmailChangeService.IsValidEmail(raw))
                return Results.BadRequest(new { error = "invalid-email", message = "A valid email address is required." });

            // Derive the app base URI from a trusted configured origin when available
            // (host-header injection defence: AllowedHosts="*" means the Host header is
            // not validated for authenticated routes). In Development only, fall back to
            // req.Scheme://req.Host which is acceptable for local use.
            // Reuses the same CliOptions.PublicOrigin pattern as the /device-code endpoint
            // (CliDeviceEndpoints.cs) so there is a single configuration seam for both.
            var req = ctx.Request;
            Uri appBase;
            var publicOrigin = cliOpts.Value.PublicOrigin;
            if (!string.IsNullOrEmpty(publicOrigin))
            {
                appBase = new Uri(publicOrigin.TrimEnd('/'));
            }
            else if (env.IsDevelopment() || env.IsEnvironment("Testing"))
            {
                appBase = new Uri($"{req.Scheme}://{req.Host}");
            }
            else
            {
                // Non-Development without PublicOrigin: fall back but warn.
                appBase = new Uri($"{req.Scheme}://{req.Host}");
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("EmailChange");
                logger.LogWarning(
                    "Email-change verification link built from request Host header because " +
                    "Korat:Cli:PublicOrigin is not configured. Set PublicOrigin to prevent " +
                    "host-header injection in verification emails.");
            }

            var result = await emailChange.RequestAsync(userId, raw, appBase, ct);

            return result switch
            {
                EmailChangeRequestStatus.InvalidEmailFormat =>
                    Results.BadRequest(new { error = "invalid-email", message = "A valid email address is required." }),
                EmailChangeRequestStatus.SameAsCurrentEmail =>
                    Results.BadRequest(new { error = "same-as-current", message = "That is already your primary email address." }),
                // Anti-enumeration: return 202 for EmailAlreadyInUse — same as success —
                // so a signed-in user cannot probe whether arbitrary addresses are registered.
                // This mirrors SP1's MagicLinkService / MagicLinkEndpoints anti-enumeration posture
                // (see MagicLinkEndpoints.cs "anti-enumeration" comment). The DB unique index on
                // User.PrimaryEmail still enforces uniqueness at confirm time.
                EmailChangeRequestStatus.EmailAlreadyInUse =>
                    Results.Accepted(),
                EmailChangeRequestStatus.RateLimited =>
                    Results.Json(
                        new { error = "rate-limited", message = "Too many requests — try again later." },
                        statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Accepted(),
            };
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.EmailChangeRequestPolicy)
          .RequireAntiforgeryValidation();

        return app;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Single response shape for both GET and PUT /api/auth/me — one source of truth
    /// prevents the two handlers from drifting when new profile fields are added.
    /// </summary>
    private sealed record MeResponse(
        string UserId,
        string Email,
        string? DisplayName,
        bool IsAdmin,
        IReadOnlyList<ProviderLinkResponse> Providers,
        PendingEmailChangeDto? PendingEmailChange = null)
    {
        public static MeResponse From(
            Korat.Domain.Auth.User u,
            IReadOnlyList<ProviderLinkResponse> providers,
            PendingEmailChangeDto? pending = null) =>
            new(u.Id.ToString(), u.PrimaryEmail, u.DisplayName, u.IsAdmin, providers, pending);
    }

    /// <summary>One OAuth/identity link shown on the account page (read-only).</summary>
    private sealed record ProviderLinkResponse(string Provider, string ExternalId);

    /// <summary>
    /// Reads the caller's linked identities (<c>ExternalLogin</c> rows) for the account
    /// page's "Connected providers" list. Direct DB read — these are display-only link
    /// records, mirroring the pending-email-change read in the same endpoint, so they
    /// don't need to round-trip the user grain.
    /// </summary>
    private static async Task<IReadOnlyList<ProviderLinkResponse>> LoadProvidersAsync(
        KoratDbContext db, Korat.Domain.Auth.UserId userId, CancellationToken ct)
    {
        var rows = await db.ExternalLogins
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.LinkedAt)
            .Select(e => new { e.Provider, e.ProviderUserId })
            .ToListAsync(ct);
        return rows
            .Select(r => new ProviderLinkResponse(r.Provider.ToString().ToLowerInvariant(), r.ProviderUserId))
            .ToList();
    }

    /// <summary>
    /// Pending email-change details surfaced on GET /api/auth/me so the frontend can
    /// persist the "check your inbox" state across navigations and warn that re-requesting
    /// will invalidate the outstanding link.
    /// </summary>
    private sealed record PendingEmailChangeDto(string NewEmail, DateTimeOffset ExpiresAt);

    /// <summary>Request body for PUT /api/auth/me.</summary>
    private sealed record UpdateMeRequest(string? DisplayName);

    /// <summary>Request body for POST /api/auth/email/change.</summary>
    private sealed record EmailChangeRequest(string? NewEmail);

    /// <summary>Request body for POST /api/auth/email/change/confirm.</summary>
    private sealed record ConfirmEmailChangeRequest(string? Token);
}
