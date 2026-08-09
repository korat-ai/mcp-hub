using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Auth.Endpoints;

/// <summary>
/// Device Authorization Grant (RFC 8628) endpoints for the CLI.
///
/// POST /api/auth/cli/device-code   — anonymous; starts the handshake, returns device_code + user_code
/// POST /api/auth/cli/token         — anonymous; CLI polls until approved or expired
/// POST /api/auth/cli/approve       — cookie-authenticated; approves the user_code
/// POST /api/auth/cli/deny          — cookie-authenticated; denies the user_code
/// POST /api/auth/cli/revoke        — Bearer; revokes the current CLI token (idempotent 200)
/// POST /api/auth/cli/revoke-all    — resolved identity; revokes all CLI tokens for the user
/// </summary>
public static class CliDeviceEndpoints
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static void MapCliDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth/cli");

        // ── Device Code ─────────────────────────────────────────────────────
        g.MapPost("/device-code", async (
            IDeviceCodeStore store,
            HttpRequest req,
            IOptions<CliOptions> cliOpts,
            CancellationToken ct) =>
        {
            var entry = await store.CreateAsync(Ttl, ct);

            // Build the verification URI from a trusted configured origin when available
            // (host-header injection defence: AllowedHosts="*" means the Host header is
            // not validated for anonymous routes). In development / unset, falls back to
            // req.Scheme://req.Host which is acceptable for local use.
            var baseUri = !string.IsNullOrEmpty(cliOpts.Value.PublicOrigin)
                ? cliOpts.Value.PublicOrigin.TrimEnd('/')
                : $"{req.Scheme}://{req.Host}";

            return Results.Ok(new
            {
                device_code               = entry.DeviceCode,
                user_code                 = entry.UserCode,
                verification_uri          = $"{baseUri}/app/cli/authorize",
                verification_uri_complete = $"{baseUri}/app/cli/authorize?code={entry.UserCode}",
                interval                  = 5,
                expires_in                = (int)Ttl.TotalSeconds,
            });
        }).RequireRateLimiting(RateLimiterRegistration.CliDeviceCodePolicy);

        // ── Token (CLI polls) ────────────────────────────────────────────────
        //
        // Ordering: peek status (non-destructive) → if Approved, issue CLI token durably
        // → THEN mark the handshake consumed. This ensures the irreversible grain state
        // mutation happens ONLY after the credential has been persisted to the database.
        // If IssueAsync or its SaveChanges throws, the grain stays Approved and the CLI
        // can retry the next poll and recover — rather than losing the approval permanently.
        g.MapPost("/token", async (
            TokenReq body,
            IDeviceCodeStore store,
            ICliTokenService cli,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.device_code)) return Err("expired_token");

            // Non-destructive peek: reads current status without burning the handshake.
            var entry = await store.GetStatusAsync(body.device_code, ct);

            switch (entry?.Status)
            {
                case DeviceCodeStatus.Pending:
                    return Err("authorization_pending");

                case DeviceCodeStatus.Denied:
                    // Burn the denied handshake so subsequent polls are clean.
                    await store.MarkConsumedAsync(body.device_code, ct);
                    return Err("access_denied");

                case DeviceCodeStatus.Approved when entry.UserId is { } uid:
                    // Issue the credential first; only burn the handshake on success.
                    var result = await RespondWithToken(cli, uid, time, ct);
                    await store.MarkConsumedAsync(body.device_code, ct);
                    return result;

                default:
                    // null (unknown device_code) or Expired.
                    return Err("expired_token");
            }
        }).RequireRateLimiting(RateLimiterRegistration.CliTokenPollPolicy);

        // ── Approve (cookie-authenticated) ───────────────────────────────────
        // CSRF guard: /approve is a cookie-authenticated, state-changing JSON POST.
        // UseAntiforgery() middleware only auto-validates form-encoded endpoints;
        // JSON minimal-API POSTs require RequireAntiforgeryValidation() explicitly,
        // mirroring the pattern on InviteAdminEndpoints, AuthApiEndpoints, etc.
        // Without this guard, a cross-site page that auto-POSTs a user_code would
        // approve an attacker's device as the victim (account-takeover vector).
        g.MapPost("/approve", async (
            UserCodeReq body,
            HttpContext ctx,
            IAuthResolver resolver,
            IDeviceCodeStore store,
            CancellationToken ct) =>
        {
            var id = await resolver.ResolveAsync(ctx, ct);
            if (id is null) return Results.Unauthorized();
            // MAJOR-2: bridge-only tokens must not approve device-code flows — a bridge-only
            // token could otherwise escalate itself to a full CLI token via the device-code
            // grant.  Cookie/session principals always resolve to Scope="full", so this does
            // not affect the legitimate browser approval path.
            if (id.Scope != "full") return Results.StatusCode(StatusCodes.Status403Forbidden);
            var code = IDeviceCodeStore.NormalizeUserCode(body.user_code ?? "");
            var ok = await store.ApproveAsync(code, id.UserId.Value, ct);
            return ok ? Results.Ok() : Results.NotFound();
        }).RequireAntiforgeryValidation()
          .RequireRateLimiting(RateLimiterRegistration.CliApprovePolicy);

        // ── Deny (cookie-authenticated) ──────────────────────────────────────
        // CSRF guard: same rationale as /approve above.
        g.MapPost("/deny", async (
            UserCodeReq body,
            HttpContext ctx,
            IAuthResolver resolver,
            IDeviceCodeStore store,
            CancellationToken ct) =>
        {
            var id = await resolver.ResolveAsync(ctx, ct);
            if (id is null) return Results.Unauthorized();
            // MAJOR-2: same scope gate as /approve — bridge-only tokens must not deny flows either.
            if (id.Scope != "full") return Results.StatusCode(StatusCodes.Status403Forbidden);
            var code = IDeviceCodeStore.NormalizeUserCode(body.user_code ?? "");
            var ok = await store.DenyAsync(code, ct);
            return ok ? Results.Ok() : Results.NotFound();
        }).RequireAntiforgeryValidation()
          .RequireRateLimiting(RateLimiterRegistration.CliApprovePolicy);

        // ── Revoke current token (Bearer) ─────────────────────────────────────
        // Returns 200 unconditionally — intentional idempotent-success contract per
        // OAuth 2.0 Token Revocation (RFC 7009 §2.2): the server MUST respond with
        // HTTP 200 even if the token was already revoked or not found. This avoids
        // leaking token existence. Clients should not treat a 200 here as proof the
        // token was live; use /revoke-all for "ensure all sessions are terminated".
        //
        // Rate limit (sec L3): AuthDefaultPolicy (60/min per session cookie, falling
        // back to per-IP for Bearer-only callers) — same budget as other authenticated
        // CLI-token management endpoints to prevent revoke-loop abuse.
        g.MapPost("/revoke", async (HttpRequest req, ICliTokenService cli, CancellationToken ct) =>
        {
            var authz = req.Headers.Authorization.ToString();
            if (authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                await cli.RevokeAsync(authz["Bearer ".Length..].Trim(), ct);
            return Results.Ok();
        }).RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy);

        // ── Revoke all tokens for the resolved identity ───────────────────────
        // CSRF guard: /revoke-all is cookie-resolvable and state-changing — same
        // rationale as /approve and /deny above.
        g.MapPost("/revoke-all", async (
            HttpContext ctx,
            IAuthResolver resolver,
            ICliTokenService cli,
            CancellationToken ct) =>
        {
            var id = await resolver.ResolveAsync(ctx, ct);
            if (id is null) return Results.Unauthorized();
            // Bridge-only tokens must not reach account-management mutations — same "full"-scope
            // floor as /approve and /deny above and RequireFullScope on the token-list surface.
            if (id.Scope != "full") return Results.StatusCode(StatusCodes.Status403Forbidden);
            await cli.RevokeAllForUserAsync(id.UserId.Value, ct);
            return Results.Ok();
        }).RequireAntiforgeryValidation()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Issues a CLI token via <paramref name="cli"/> and returns the 200 token response.
    /// Named distinctly from <see cref="ICliTokenService.IssueAsync"/> to avoid
    /// confusion when reading the /token handler.
    /// </summary>
    private static async Task<IResult> RespondWithToken(
        ICliTokenService cli,
        Guid userId,
        TimeProvider time,
        CancellationToken ct)
    {
        var r = await cli.IssueAsync(userId, "full", ct);
        // Compute expires_in against the same TimeProvider the service used for ExpiresAt,
        // preserving the TimeProvider discipline used across CliTokenService/SessionService.
        var expiresIn = (int)(r.ExpiresAt - time.GetUtcNow()).TotalSeconds;
        return Results.Ok(new
        {
            cli_token  = r.RawToken,
            scope      = "full",
            expires_in = expiresIn,
        });
    }

    private static IResult Err(string error) => Results.BadRequest(new { error });

    private sealed record TokenReq(string? device_code);
    private sealed record UserCodeReq(string? user_code);
}

/// <summary>
/// Account-scoped CLI token management endpoints.
///
/// GET  /api/cli/tokens              — list non-revoked tokens for the authenticated user.
/// POST /api/cli/tokens/{id}/revoke  — revoke one token by id (IDOR-safe: ownership verified).
/// </summary>
public static class CliTokenManagementEndpoints
{
    public static void MapCliTokenManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/cli/tokens");

        // ── List (cookie-authenticated) ──────────────────────────────────────
        // Returns the caller's own live CLI tokens. Identity is resolved from
        // the session cookie (IAuthResolver) — no Bearer auth here.
        g.MapGet("/", async (
            HttpContext ctx,
            ICliTokenService cli,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            var tokens = await cli.ListForUserAsync(userId.Value, ct);
            return Results.Ok(tokens.Select(t => new
            {
                id         = t.Id,
                // CliToken has no display name field; use scope as the label the frontend
                // shows in the "name" column (CLI tokens are issued with scope "full" or
                // "bridge-only"). If a label field is added in future, project it here.
                name       = t.Scope,
                createdAt  = t.IssuedAt,
                lastUsedAt = t.LastUsedAt,
                expiresAt  = t.ExpiresAt,
            }));
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy);

        // ── Revoke by id (cookie-authenticated, CSRF-guarded) ────────────────
        // Ownership is verified inside RevokeByIdForUserAsync — the WHERE clause
        // requires UserId == caller's UserId so user A cannot revoke user B's token.
        // Returns 404 (cloaked-403) when the id is unknown, already revoked, or
        // belongs to a different user, mirroring the session-revoke handler pattern.
        g.MapPost("/{id:guid}/revoke", async (
            Guid id,
            HttpContext ctx,
            ICliTokenService cli,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            var revoked = await cli.RevokeByIdForUserAsync(userId.Value, id, ct);
            return revoked ? Results.NoContent() : Results.NotFound();  // cloaked-403 → 404
        }).RequireFullScope()
          .RequireRateLimiting(RateLimiterRegistration.AuthDefaultPolicy)
          .RequireAntiforgeryValidation();
    }
}
