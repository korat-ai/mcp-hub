using Korat.Domain.Entities;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Domain;

public static class StateTransitions
{
    /// <summary>
    /// Returns true if the approval was applied, false if it was already approved (idempotent).
    /// Throws <see cref="KoratDomainException"/> with <see cref="KoratErrorCode.InvalidStateTransition"/>
    /// if the request is in a non-pending, non-approved state (e.g. Denied).
    /// </summary>
    public static bool ApproveAccessRequest(AccessRequest request, UserId userId, DateTimeOffset now)
    {
        if (request.Status == AccessRequestStatus.Approved)
            return false; // already approved — idempotent

        if (request.Status != AccessRequestStatus.Pending)
            throw new KoratDomainException(KoratErrorCode.InvalidStateTransition,
                "Only pending requests can be approved.");

        request.Status = AccessRequestStatus.Approved;
        request.ResolvedAt = now;
        request.ResolvedByUserId = userId;
        return true;
    }

    public static void DenyAccessRequest(AccessRequest request, UserId userId, DateTimeOffset now)
    {
        if (request.Status != AccessRequestStatus.Pending)
            throw new KoratDomainException(KoratErrorCode.InvalidStateTransition,
                "Only pending requests can be denied.");

        request.Status = AccessRequestStatus.Denied;
        request.ResolvedAt = now;
        request.ResolvedByUserId = userId;
    }

    public static void RevokeGrant(Grant grant, UserId userId, DateTimeOffset now)
    {
        if (grant.Status != GrantStatus.Active)
            throw new KoratDomainException(KoratErrorCode.InvalidStateTransition,
                "Only active grants can be revoked.");

        grant.Status = GrantStatus.Revoked;
        grant.RevokedAt = now;
        grant.RevokedByUserId = userId;
    }

    /// <summary>
    /// Р26: suspend a permission because the server's definition changed under it — not because a
    /// person revoked it.
    ///
    /// <para>Deliberately distinct from <see cref="RevokeGrant"/>: <c>RevokedByUserId</c> stays
    /// null, so the audit trail never attributes this to an owner who did not act. The owner's
    /// next step is the same either way (approve the new access request), but "you revoked this"
    /// and "what you approved was replaced" are different facts and must not be recorded as one.
    /// </para>
    ///
    /// <para>Idempotent by refusal, like <see cref="RevokeGrant"/>: a non-Active grant is already
    /// not in force, and re-suspending it would rewrite <c>RevokedAt</c>.</para>
    /// </summary>
    public static void SuspendGrantForRedefinition(Grant grant, DateTimeOffset now)
    {
        if (grant.Status != GrantStatus.Active)
            throw new KoratDomainException(KoratErrorCode.InvalidStateTransition,
                "Only active grants can be suspended.");

        grant.Status = GrantStatus.Revoked;
        grant.RevokedAt = now;
        grant.RevokedByUserId = null;
    }

    /// <summary>
    /// Returns true if the server transitioned to Disabled, false if it was already Disabled
    /// (idempotent no-op — UpdatedAt is NOT bumped and no state is mutated). Mirrors the
    /// false-if-already convention used by <see cref="ApproveAccessRequest"/> so callers
    /// (McpServerGrain.DisableAsync → the /disable endpoint) can skip the audit write on a
    /// repeat disable of an already-Disabled server.
    /// </summary>
    public static bool DisableMcpServer(McpServer server, DateTimeOffset now)
    {
        if (server.Status == McpServerStatus.Disabled)
            return false; // already disabled — idempotent no-op

        server.Status = McpServerStatus.Disabled;
        server.UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Symmetric to <see cref="DisableMcpServer"/>: owner re-enable puts the server back to
    /// Published. Does NOT touch IsAsserted — availability still requires the publishing node
    /// to be online and asserting the server (see Endpoints.cs availability formula); re-enabling
    /// a server whose node has since gone quiet simply leaves it Published-but-unavailable until
    /// the node re-asserts, mirroring how a fresh PublishAsync/UpdateCommandAsync would behave.
    /// Returns true if the server transitioned to Published, false if it was already Published
    /// (idempotent no-op — UpdatedAt is NOT bumped and no state is mutated), same convention as
    /// <see cref="DisableMcpServer"/> above.
    ///
    /// Increment 2 (HTTP MCP OAuth): enable must never bypass consent. <paramref
    /// name="hasUsableOAuthToken"/> is computed by the CALLER (McpServerGrain.EnableAsync, which
    /// holds IMetadataRepository) — this function stays pure, no IO. For a non-oauth server the
    /// flag is irrelevant (the target status is always Published). For an oauth server with no
    /// usable access/refresh token, the target status is NeedsReauth instead of Published,
    /// whether this is the very first activation (never consented) or a re-enable after a
    /// refresh failure — enable is only ever "recover to the correct effective state," never
    /// "manufacture a Published server." Idempotent: returns false (no mutation) whenever the
    /// server is ALREADY at its target status, matching DisableMcpServer's convention exactly.
    /// </summary>
    public static bool EnableMcpServer(McpServer server, DateTimeOffset now, bool hasUsableOAuthToken = true)
    {
        var targetStatus = McpServerAuthModes.IsOAuth(server.AuthMode) && !hasUsableOAuthToken
            ? McpServerStatus.NeedsReauth
            : McpServerStatus.Published;

        if (server.Status == targetStatus)
            return false; // idempotent no-op — already in the correct effective state

        server.Status = targetStatus;
        server.UpdatedAt = now;
        return true;
    }

    public static void CloseSession(RelaySession session, SessionCloseReason reason, DateTimeOffset now)
    {
        session.Status = SessionStatus.Closed;
        session.CloseReason = reason;
        session.EndedAt = now;
    }
}
