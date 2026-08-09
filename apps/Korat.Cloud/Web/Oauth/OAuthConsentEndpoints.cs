using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a, Task 8 (SF-6 owner-facing revocation): list + revoke the owner's OAuth
/// consents (OpenIddict permanent authorizations carrying the korat:space property). Revoke =
/// (1) every token under the authorization revoked (reference tokens → dead on the very next
/// /mcp request), (2) the authorization revoked (no new refresh chains), (3) every LIVE
/// aggregator session for the derived (client × owner × Space) identity terminated via the
/// Task-7 registry's own <see cref="ISpaceMcpConsumerSessionsGrain.TerminateAllAsync"/> fan-out
/// (the endpoint does NOT re-implement that loop — TerminateAllAsync's [Reentrant]
/// snapshot-then-mutate care lives entirely inside the grain), (4) audited fail-closed —
/// mirroring POST /api/grants/{grantId}/revoke (Endpoints.cs:1108-1154).
///
/// BOLA defenses: the list endpoint only ever enumerates authorizations whose Subject ==
/// the caller's own UserId "N" (<see cref="IOpenIddictAuthorizationManager.FindBySubjectAsync"/>
/// itself is the scoping — no cross-owner row is ever materialized). The revoke endpoint looks
/// the authorization up by id FIRST and re-checks Subject == caller before doing anything else,
/// returning a cloaked 404 (identical to "unknown id") for a foreign/unknown consent so an
/// attacker cannot distinguish "not yours" from "does not exist".
/// </summary>
public static class OAuthConsentEndpoints
{
    public static void MapOAuthConsentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/oauth/consents", async (
            HttpContext ctx,
            IOpenIddictAuthorizationManager authorizations,
            IOpenIddictApplicationManager applications,
            IMetadataRepository repository,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var subject = userId.Value.ToString("N");
            var results = new List<object>();
            // Memoize Space display-name lookups — an owner can have several consents for the
            // same Space (different clients), no need to re-fetch it per row.
            var spaceNames = new Dictionary<string, string>(StringComparer.Ordinal);

            // MARS-safety: drain the subject's authorizations BEFORE the loop. The body calls
            // applications.FindByIdAsync (a real query on cache miss) on the SAME scoped
            // KoratDbContext, and OpenIddict's FindBySubjectAsync streams a DataReader when entity
            // caching is disabled — a nested command on an open reader throws
            // NpgsqlOperationInProgressException on Npgsql (no MARS; cf. the DCR reaper #343). Safe
            // today only because caching buffers; materializing first removes that dependency.
            var subjectAuthorizations = new List<object>();
            await foreach (var a in authorizations.FindBySubjectAsync(subject, ct))
                subjectAuthorizations.Add(a);
            foreach (var authorization in subjectAuthorizations)
            {
                if (await authorizations.GetTypeAsync(authorization, ct) != AuthorizationTypes.Permanent)
                    continue;
                if (await authorizations.GetStatusAsync(authorization, ct) != Statuses.Valid)
                    continue;
                var properties = await authorizations.GetPropertiesAsync(authorization, ct);
                if (!properties.TryGetValue(KoratOAuthConstants.AuthorizationSpaceProperty, out var spaceElement)
                    || spaceElement.ValueKind != System.Text.Json.JsonValueKind.String
                    || spaceElement.GetString() is not { } spaceIdValue)
                    continue; // not a Space-MCP consent (future OIDC consents won't carry it)

                var applicationId = await authorizations.GetApplicationIdAsync(authorization, ct);
                var application = applicationId is null ? null : await applications.FindByIdAsync(applicationId, ct);

                if (!spaceNames.TryGetValue(spaceIdValue, out var spaceName))
                {
                    var space = await repository.GetSpaceAsync(new SpaceId(spaceIdValue), ct);
                    spaceName = space?.DisplayName ?? spaceIdValue;
                    spaceNames[spaceIdValue] = spaceName;
                }

                results.Add(new
                {
                    id = await authorizations.GetIdAsync(authorization, ct),
                    clientId = application is null ? null : await applications.GetClientIdAsync(application, ct),
                    clientDisplayName = application is null ? null : await applications.GetDisplayNameAsync(application, ct),
                    spaceId = spaceIdValue,
                    spaceName,
                    createdAt = await authorizations.GetCreationDateAsync(authorization, ct),
                });
            }
            return Results.Ok(results);
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/oauth/consents/{consentId}/revoke", async (
            string consentId,
            HttpContext ctx,
            IOpenIddictAuthorizationManager authorizations,
            IOpenIddictApplicationManager applications,
            IOpenIddictTokenManager tokens,
            IClusterClient clusterClient,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var subject = userId.Value.ToString("N");

            var authorization = await authorizations.FindByIdAsync(consentId, ct);
            if (authorization is null
                || await authorizations.GetSubjectAsync(authorization, ct) != subject)
                return Results.NotFound(); // cloaked — foreign/unknown are indistinguishable

            var properties = await authorizations.GetPropertiesAsync(authorization, ct);
            if (!properties.TryGetValue(KoratOAuthConstants.AuthorizationSpaceProperty, out var spaceElement)
                || spaceElement.ValueKind != System.Text.Json.JsonValueKind.String
                || spaceElement.GetString() is not { } spaceIdValue)
                return Results.NotFound(); // not a Space-MCP consent

            var applicationId = await authorizations.GetApplicationIdAsync(authorization, ct);
            var application = applicationId is null ? null : await applications.FindByIdAsync(applicationId, ct);
            var clientId = application is null ? null : await applications.GetClientIdAsync(application, ct);

            // (1)+(2) token death, then the authorization itself. MARS-safety: drain the token
            // list BEFORE revoking — TryRevokeAsync (UPDATE + SaveChanges) inside the
            // FindByAuthorizationIdAsync stream would be a nested command on the open reader
            // (Npgsql no-MARS) once entity caching is disabled; materialize first.
            var consentTokens = new List<object>();
            await foreach (var token in tokens.FindByAuthorizationIdAsync(consentId, ct))
                consentTokens.Add(token);
            foreach (var token in consentTokens)
                await tokens.TryRevokeAsync(token, ct);
            await authorizations.TryRevokeAsync(authorization, ct);

            // (3) SF-6 live-session teardown via the Task-7 registry. Task-7 handoff: call
            // TerminateAllAsync() — do NOT duplicate its snapshot-then-fan-out loop here. The
            // ListAsync() snapshot below is ONLY for the audit-details count and is inherently
            // best-effort/approximate (a session could register/unregister in the tiny window
            // before TerminateAllAsync's own snapshot) — it never substitutes for the grain's
            // own teardown, which is authoritative.
            var terminatedSessions = 0;
            if (clientId is not null)
            {
                var identity = SpaceMcpConsumerIdentity.DeriveOAuth(clientId, userId, new SpaceId(spaceIdValue));
                var registry = clusterClient.GetGrain<ISpaceMcpConsumerSessionsGrain>(identity.Value);
                terminatedSessions = (await registry.ListAsync()).Length;
                await registry.TerminateAllAsync();
            }

            // (4) audited fail-closed (house pattern: grant revoke, Endpoints.cs:1136-1144).
            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.OAuthConsentRevoked,
                TargetType: "oauth_consent",
                TargetId: consentId,
                SpaceId: spaceIdValue,
                ActorType: AuditActorTypes.User,
                ActorId: userId.Value.ToString(),
                DetailsJson: AuditDetails.Json(new { clientId, terminatedSessions })),
                required: true, ct);

            return Results.NoContent();
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}
