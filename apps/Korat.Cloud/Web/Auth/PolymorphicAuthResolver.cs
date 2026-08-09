using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;

namespace Korat.Cloud.Web.Auth;

/// <summary>
/// The result of a successful identity resolution. Carries the resolved user identity and the
/// privilege scope of the credential used:
/// <list type="bullet">
///   <item><c>"full"</c> — cookie session or a full-scope CLI token (admin and developer surfaces allowed).</item>
///   <item><c>"bridge-only"</c> — CLI token issued for relay agents (admin and developer surfaces REJECTED with 403).</item>
/// </list>
/// </summary>
public sealed record ResolvedIdentity(UserId UserId, string Scope = "full");

public interface IAuthResolver
{
    Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct);
}

public sealed class PolymorphicAuthResolver(
    ISessionService sessions,
    ICliTokenService cliTokens,
    ISsoTokenValidator ssoTokens,
    ISsoIdentityResolver ssoIdentities,
    ILogger<PolymorphicAuthResolver> logger) : IAuthResolver
{
    public async Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct)
    {
        // 1. RelaySession cookie always wins if present and valid.
        if (ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.SessionCookieName, out var raw)
            && Guid.TryParse(raw, out var sessionId))
        {
            var bumped = await sessions.ValidateAndBumpAsync(sessionId, ct);
            if (bumped is not null)
            {
                return new ResolvedIdentity(bumped.UserId);
            }
            // Invalid session — clear cookie to prevent zombie state. The __Host- prefix
            // requires Secure + Path=/ + no Domain on the Set-Cookie itself (browsers reject
            // a non-conforming Set-Cookie for __Host- cookies, including the delete header).
            ctx.Response.Cookies.Delete(CanonicalSigninHandler.SessionCookieName, new CookieOptions
            {
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
            });
        }

        // 2. CLI bearer token (Authorization: Bearer korat_cli_...).
        // ValidateWithScopeAsync returns (UserId, Scope) so the resolved identity carries the
        // token's privilege scope — admin and developer filters reject Scope != "full".
        var authzHeader = ctx.Request.Headers.Authorization.ToString();
        if (authzHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authzHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                var validated = await cliTokens.ValidateWithScopeAsync(token, ct);
                if (validated is not null)
                    return new ResolvedIdentity(new UserId(validated.Value.UserId), validated.Value.Scope);

                // 3. Token from the Korat sign-in provider (Authorization: Bearer <JWT>).
                //
                // Tried after the CLI token, and the order costs nothing: the two credentials
                // are told apart by shape, not by trial. Our own token is korat_cli_ followed
                // by base64url, which never contains a dot; a JWT has exactly two. Each
                // validator turns the other's credential away without a database hit or a
                // network call, so both can be accepted during the migration and neither
                // slows the other down.
                var principal = await ssoTokens.ValidateAsync(token, ct);
                if (principal is not null)
                {
                    var userId = await ssoIdentities.FindAsync(principal.Subject, ct);
                    if (userId is not null)
                        return new ResolvedIdentity(userId.Value);

                    // Valid token, unknown person. Not an error to hide: the token is genuine
                    // and the holder needs to know that signing in through the browser once is
                    // what links their account here.
                    logger.LogInformation(
                        "SSO token accepted but subject {Subject} is not linked to any account here",
                        principal.Subject);
                }
            }
        }

        return null;
    }
}
