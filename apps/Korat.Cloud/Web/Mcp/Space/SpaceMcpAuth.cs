using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Korat.Cloud.Web.Spaces;
using Korat.Cloud.Web.Mcp;
using Korat.Cloud.Web.Oauth;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using OpenIddict.Abstractions;
using OpenIddict.Validation;

namespace Korat.Cloud.Web.Mcp.Space;

/// <summary>
/// The identity the responder trusts once <see cref="SpaceMcpAuth.AuthenticateAsync"/>
/// succeeds. Since inc-2a the durable <see cref="ConsumerIdentity"/> is derived HERE — per
/// credential kind (inc-1 scoped token: cliTokenId × Space; OAuth: client_id × owner × Space)
/// — so the dispatcher/grain layer never branches on how the caller authenticated (SF-4).
/// </summary>
public sealed record SpaceMcpPrincipal(UserId Owner, SpaceId SpaceId, ConsumerId ConsumerIdentity);

/// <summary>
/// Auth gate for <c>/mcp/{spaceSeg}</c> (Space-MCP increment 1, Task 1; extended inc-2a
/// Task 6 with an OAuth resource-server branch).
///
/// Deliberately reads the Bearer header DIRECTLY instead of going through
/// <see cref="Korat.Cloud.Web.Auth.PolymorphicAuthResolver"/> — that resolver's cookie
/// branch would let a browser session authenticate this surface, which the design
/// explicitly forbids (SF-4: "rejects `full` tokens AND cookie sessions, both directions
/// tested"). A <c>/mcp/{spaceSeg}</c> caller must present EITHER a Space-pinned
/// <c>space-mcp</c> scope <c>korat_cli_</c> bearer (inc-1, KEPT alongside OAuth per the
/// plan's O1 decision) OR a valid <c>korat:mcp</c>-scoped OAuth access token consented for
/// THIS Space (inc-2a) — nothing else is ever accepted, regardless of privilege.
///
/// Failure semantics (each branch sets the status code directly and returns null — callers
/// must treat a null return as "response already written", mirroring
/// <see cref="Korat.Cloud.Web.Spaces.InferenceDispatcher"/>'s streaming convention):
///   • No/malformed <c>Authorization: Bearer</c> header              → 401
///   • Bearer present but invalid/expired/revoked/garbage            → 401
///   • CLI path: valid token, but scope isn't a Space-pinned
///     <c>space-mcp:*</c> scope (rejects "full" AND "bridge-only")   → 403
///   • CLI path: valid <c>space-mcp:{pinnedSpaceId}</c> token used
///     against a DIFFERENT Space's path segment (S5 cross-Space
///     guard)                                                        → 403
///   • OAuth path: valid token, but missing the <c>korat:mcp</c>
///     scope                                                         → 403
///   • OAuth path: audience isn't this exact per-Space URL, OR the
///     consent-Space claim isn't the path-resolved Space (BLOCKER-1,
///     both checks independently fail-closed)                        → 401
///   • <paramref name="spaceSeg"/> doesn't resolve to a real Space   → 404
///   • The resolved Space's owner isn't the caller's UserId (F45
///     analog — S1/live owner-owns-Space re-check)                  → 404
/// </summary>
public static class SpaceMcpAuth
{
    public static async Task<SpaceMcpPrincipal?> AuthenticateAsync(
        HttpContext ctx,
        string spaceSeg,
        SpaceSlugService slugService,
        ICliTokenService cliTokens,
        IMetadataRepository repository,
        CliOptions cliOptions,
        OpenIddictValidationService oauthValidation,
        CancellationToken ct)
    {
        var authzHeader = ctx.Request.Headers.Authorization.ToString();
        if (!authzHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        var raw = authzHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        // Р25: OAuth only. A korat_cli_ token used to be accepted here as a second entrance, and
        // that entrance did not merely duplicate the first — it collapsed the model. The CLI path
        // derived the consumer identity from the TOKEN (SpaceMcpConsumerIdentity.Derive), and there
        // is one CLI token per machine, so every agent arriving that way shared a single cagg_
        // identity. A permission granted "to an agent" was in fact granted to the machine, and any
        // process on it received not someone else's access but its own, legitimately. Per-agent
        // grants, per-agent revocation and per-agent activity were all fiction while this branch
        // existed.
        //
        // OAuth derives the identity from the client_id instead — one per client, in that client's
        // own storage. That does NOT make agents on one machine isolated from each other (see
        // docs/security/threat-model.md, "Not protected" §1); it makes them individually visible
        // and individually revocable, which the shared-token path could never be.
        //
        // A korat_cli_ token now falls through to the OAuth validator, which will reject it. That
        // is the intended outcome: an explicit 401 pointing at the OAuth metadata, not a quiet
        // downgrade to machine-wide access.
        return await AuthenticateOAuthAsync(ctx, spaceSeg, raw, slugService, repository, cliOptions, oauthValidation, ct);
    }

    /// <summary>
    /// Inc-2a OAuth branch (spec §Pillar C "Resource-server validation" + §Identity BLOCKER-1).
    /// Validation is in-process (UseLocalServer shares keys + EF stores → reference-token
    /// revocation is immediate). The two LOAD-BEARING checks:
    ///   (1) audience == the exact per-Space URL this request arrived at, and
    ///   (2) the consent-Space claim == the path-resolved Space
    /// — both must hold (RFC 8707 + RFC 9700 mix-up class). Ownership is then RE-checked
    /// against the live Space row (consent-time ownership is not trusted forever).
    /// </summary>
    private static async Task<SpaceMcpPrincipal?> AuthenticateOAuthAsync(
        HttpContext ctx,
        string spaceSeg,
        string raw,
        SpaceSlugService slugService,
        IMetadataRepository repository,
        CliOptions cliOptions,
        OpenIddictValidationService oauthValidation,
        CancellationToken ct)
    {
        System.Security.Claims.ClaimsPrincipal principal;
        try
        {
            principal = await oauthValidation.ValidateAccessTokenAsync(raw, ct);
        }
        catch (Exception)
        {
            // Unknown/expired/revoked/garbage — indistinguishable to the caller, all 401 +
            // the RFC 9728 challenge so a real MCP client (re-)runs the OAuth flow.
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        if (!principal.HasScope(KoratOAuthConstants.McpScope))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        var spaceId = await slugService.ResolveSpaceSegmentAsync(spaceSeg, ct);
        if (spaceId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        // BLOCKER-1 check 1: audience == the EXACT URL of this request (strict ordinal — the
        // client must use the same per-Space URL it consented to; slug-vs-hex or case variants
        // are DIFFERENT audiences by design).
        var origin = McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions, ctx.Request);
        var expectedAudience = $"{origin}/mcp/{spaceSeg}";
        if (!principal.GetAudiences().Contains(expectedAudience, StringComparer.Ordinal))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        // BLOCKER-1 check 2: consent-Space == path-Space (independent of URL spelling).
        var spaceClaim = principal.GetClaim(KoratOAuthConstants.SpaceClaim);
        if (!string.Equals(spaceClaim, spaceId.Value.Value, StringComparison.Ordinal))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        var subject = principal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (!Guid.TryParse(subject, out var ownerGuid))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }
        var owner = new UserId(ownerGuid);

        // Ownership re-checked at request time (Space deleted/transferred since consent → 404).
        var space = await repository.GetSpaceAsync(spaceId.Value, ct);
        if (space is null || space.OwnerUserId != owner)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        var clientId = principal.GetClaim(KoratOAuthConstants.ClientClaim);
        if (string.IsNullOrEmpty(clientId))
        {
            return Unauthorized(ctx, spaceSeg, cliOptions);
        }

        return new SpaceMcpPrincipal(
            owner, spaceId.Value, SpaceMcpConsumerIdentity.DeriveOAuth(clientId, owner, spaceId.Value));
    }

    /// <summary>
    /// Inc-2a (RFC 9728 §5.1 / spec §Pillar C "401 challenge"): every 401 this gate emits
    /// points the client at this Space's protected-resource-metadata document — the exact
    /// challenge shape Korat's own client-side McpOAuthDiscoveryService parses
    /// (resource_metadata="…", McpOAuthDiscoveryService.cs:57 — dogfood). Uses the RAW path
    /// segment (even unresolved/unknown): the challenge must not leak Space existence, and
    /// the PRM document is served for any segment anyway. 403/404 branches deliberately do
    /// NOT carry the challenge — MCP clients key their auth flow off 401 alone.
    /// </summary>
    private static SpaceMcpPrincipal? Unauthorized(HttpContext ctx, string spaceSeg, CliOptions cliOptions)
    {
        var origin = McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions, ctx.Request);
        ctx.Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{origin}/.well-known/oauth-protected-resource/mcp/{spaceSeg}\"";
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return null;
    }
}
