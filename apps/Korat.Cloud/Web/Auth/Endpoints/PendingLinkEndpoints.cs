using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth.Endpoints;

public static class PendingLinkEndpoints
{
    public static IEndpointRouteBuilder MapPendingLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/pending-link", (HttpContext ctx, IPendingLinkService pending) =>
        {
            if (!ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.PendingLinkCookieName, out var raw))
                return Results.NotFound();
            var link = pending.TryRead(raw);
            if (link is null) return Results.NotFound();
            return Results.Ok(new { provider = link.Provider.ToString(), email = link.Email, displayName = link.DisplayName });
        }).RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy);

        app.MapPost("/api/auth/pending-link/confirm",
            async (HttpContext ctx, IPendingLinkService pending, ISessionService sessions, Korat.Persistence.KoratDbContext db, ILogger<PendingLinkEndpointsLog> logger, TimeProvider time, CancellationToken ct) =>
        {
            if (!ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.PendingLinkCookieName, out var raw))
                return Results.BadRequest(new { error = "no-pending-link" });
            var link = pending.TryRead(raw);
            if (link is null) return Results.BadRequest(new { error = "expired" });

            // Idempotency: if the user double-clicks Confirm (or the pending cookie is
            // replayed within its TTL), we'd otherwise add duplicate ExternalLogin rows
            // (which then fail the unique (Provider, ProviderUserId) index from Task 3).
            // Check existence and skip the insert; still issue a fresh session cookie so
            // the user-visible outcome is the same as first confirm.
            var alreadyLinked = await db.ExternalLogins.AnyAsync(
                x => x.Provider == link.Provider && x.ProviderUserId == link.ProviderUserId, ct);
            if (!alreadyLinked)
            {
                db.ExternalLogins.Add(new ExternalLogin
                {
                    Id = Guid.NewGuid(),
                    UserId = link.ExistingUserId,
                    Provider = link.Provider,
                    ProviderUserId = link.ProviderUserId,
                    EmailAtLink = link.Email,
                    EmailVerified = true,
                    LinkedAt = time.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Cross-IdP link confirmed: user {UserId} now has additional {Provider} identity", link.ExistingUserId, link.Provider);
            }

            var session = await sessions.CreateAsync(link.ExistingUserId, ctx.Request.Headers.UserAgent.ToString(), ctx.Connection.RemoteIpAddress?.ToString(), ct);
            ctx.Response.Cookies.Append(CanonicalSigninHandler.SessionCookieName, session.Id.ToString("N"), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = SessionService.SlidingWindow,
            });
            // __Host- cookie delete requires Secure+Path=/ to be accepted by browser
            ctx.Response.Cookies.Delete(CanonicalSigninHandler.PendingLinkCookieName, new CookieOptions
            {
                Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax,
            });
            return Results.NoContent();
        }).RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        app.MapPost("/api/auth/pending-link/cancel", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(CanonicalSigninHandler.PendingLinkCookieName, new CookieOptions
            {
                Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax,
            });
            return Results.NoContent();
        }).RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();

        return app;
    }

    // Marker type for ILogger<T> category (logger category becomes "Korat.Cloud.Web.Auth.Endpoints.PendingLinkEndpointsLog").
    public sealed class PendingLinkEndpointsLog;
}
