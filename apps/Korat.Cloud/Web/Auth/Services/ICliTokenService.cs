using Korat.Domain.Auth;

namespace Korat.Cloud.Web.Auth.Services;

public interface ICliTokenService
{
    Task<CliTokenIssueResult> IssueAsync(Guid userId, string scope, CancellationToken ct);
    Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct);
    /// <summary>
    /// Same as <see cref="ValidateAsync"/> but also returns the token's <c>Scope</c>.
    /// Used by <see cref="Korat.Cloud.Web.Auth.PolymorphicAuthResolver"/> so that the
    /// resolved identity carries the scope and privilege-checking filters can reject
    /// bridge-only tokens on admin / developer surfaces.
    /// </summary>
    Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct);
    /// <summary>
    /// Space-MCP (increment 1, Task 1): returns the CliToken row's own DB primary key (a
    /// stable identity distinct from the raw token value) for a live (non-revoked) token, or
    /// null if unknown/revoked. Used by <c>SpaceMcpAuth</c>'s CLI-scoped branch — the stable id
    /// that <c>SpaceMcpConsumerIdentity.Derive</c> (Task 3) hashes into the durable consumer
    /// ConsumerId, fed directly into <c>SpaceMcpPrincipal.ConsumerIdentity</c> since the
    /// inc-2a SF-4 reshape. Does not re-check expiry/scope — callers are expected to have
    /// already validated the token via <see cref="ValidateWithScopeAsync"/> in the same request.
    /// </summary>
    Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct);
    /// <returns>true if a live token was found and revoked; false if already revoked or not found.</returns>
    Task<bool> RevokeAsync(string rawToken, CancellationToken ct);
    /// <returns>The number of live tokens that were revoked.</returns>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct);
    /// <summary>
    /// Lists all non-revoked CLI tokens for the given user (most-recently-issued first).
    /// Used by GET /api/cli/tokens — callers must scope to the authenticated user's id.
    /// </summary>
    Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct);
    /// <summary>
    /// Revokes the CLI token identified by <paramref name="tokenId"/> only when it
    /// belongs to <paramref name="userId"/>. Returns true on success; false (cloaked-403)
    /// when the id is unknown, already revoked, or belongs to a different user.
    /// </summary>
    Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct);
}

public sealed record CliTokenIssueResult(string RawToken, DateTimeOffset ExpiresAt);

/// <summary>Projection returned by <see cref="ICliTokenService.ListForUserAsync"/>.</summary>
public sealed record CliTokenListItem(
    Guid Id,
    string Scope,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt);
