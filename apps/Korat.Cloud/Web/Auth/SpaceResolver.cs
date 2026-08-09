using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth;

/// <summary>
/// Maps an identity-resolved <see cref="UserId"/> to that user's default <see cref="SpaceId"/>.
/// Returns an id, not Space content — content reads still funnel through ISpaceGrain
/// (design §3.3). The userId→spaceId mapping changes only at user creation.
///
/// InMemory disclaimer: EF Core InMemory does not enforce the filtered unique index
/// <c>(OwnerUserId) WHERE IsDefault</c>. In unit tests, two default Spaces for the same
/// owner would resolve non-deterministically (OrderBy tiebreaker stabilises the pick but
/// cannot guarantee uniqueness). Production uses the Postgres branch where the filtered
/// index enforces the SC-2 single-default-Space invariant at the DB layer.
/// </summary>
public sealed class SpaceResolver(KoratDbContext db, ILogger<SpaceResolver> logger)
{
    /// <summary>
    /// Returns the default <see cref="SpaceId"/> for the given <paramref name="userId"/>,
    /// or <c>null</c> if the user has no default Space (which indicates a broken invariant
    /// — callers should respond with 403 Forbidden + log an error).
    /// </summary>
    public async Task<SpaceId?> ResolveDefaultSpaceIdAsync(UserId userId, CancellationToken ct)
    {
        var result = await ResolveDefaultSpaceAsync(userId, ct);
        return result?.SpaceId;
    }

    /// <summary>
    /// Returns the default Space id + display name for the given <paramref name="userId"/>
    /// in a single DB round-trip, or <c>null</c> on broken-invariant (no default Space).
    /// Use this overload when both the id and the display name are needed (e.g. GET /api/space)
    /// to avoid a redundant second query.
    /// </summary>
    public async Task<(SpaceId SpaceId, string DisplayName)?> ResolveDefaultSpaceAsync(UserId userId, CancellationToken ct)
    {
        var ownerKey = userId.Value.ToString("N");
        var row = await db.Spaces
            .Where(s => s.OwnerUserId == ownerKey && s.IsDefault)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)   // deterministic tiebreaker if filtered-unique index absent
            .Select(s => new { s.Id, s.DisplayName })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            // Broken invariant: every provisioned user must have exactly one default Space (SC-2).
            // Log error so the signal is visible even when the provisioning seam regresses.
            logger.LogError(
                "No default Space found for userId={UserId} — provisioning invariant SC-2 violated",
                userId.Value);
            return null;
        }

        return (new SpaceId(row.Id), row.DisplayName);
    }
}
