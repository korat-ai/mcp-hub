using Korat.Domain.Auth;
using Korat.Persistence;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Atomically provisions a new User with its default Space and the owner SpaceMember row.
/// Every user-creation call site (CanonicalSigninHandler — invite path and
/// Bootstrap:AdminEmail bypass) goes through this seam — a User can never exist without
/// a default Space (SC-2).
///
/// InMemory race-safety disclaimer: EF Core InMemory does not support raw SQL and cannot
/// serialise concurrent writes. The InMemory branch is used in unit tests only. Production
/// relies on Postgres FK + unique-filtered-index guarantees enforced at the DB layer.
/// </summary>
public sealed class UserProvisioningService(
    KoratDbContext db,
    TimeProvider time,
    ILogger<UserProvisioningService> logger) : IUserProvisioningService
{
    public async Task<(User User, SpaceRecord Space)> CreateUserWithDefaultSpaceAsync(
        string email, string displayName, CancellationToken ct, bool isAdmin = false, UserId? userId = null)
    {
        // SP1 parity: normalize email before persisting (trim + lowercase), matching the
        // invite-code normalization in commit 95adbad. Prevents identity-split where
        // "Alice@x.io" and "alice@x.io" appear as two distinct users each with their own
        // default Space, defeating canonical-identity deduplication.
        var normalizedEmail = email.Trim().ToLowerInvariant();

        var now = time.GetUtcNow();
        var resolvedUserId = userId ?? UserId.New();
        var spaceId = Guid.NewGuid().ToString("N");

        var user = new User
        {
            Id = resolvedUserId,
            PrimaryEmail = normalizedEmail,
            DisplayName = displayName,
            CreatedAt = now,
            Status = UserStatus.Active,
            IsAdmin = isAdmin,
        };

        var spaceName = BuildSpaceName(displayName, normalizedEmail);

        var space = new SpaceRecord
        {
            Id = spaceId,
            OwnerUserId = resolvedUserId.Value.ToString("N"),
            DisplayName = spaceName,
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var member = new SpaceMemberRecord
        {
            SpaceId = spaceId,
            UserId = resolvedUserId.Value.ToString("N"),
            Role = SpaceMemberRole.Owner,
            JoinedAt = now,
        };

        db.Users.Add(user);
        db.Spaces.Add(space);
        db.SpaceMembers.Add(member);
        // Single SaveChangesAsync — atomic: user never exists without default Space + owner member.
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Provisioned user {UserId} with default Space {SpaceId}", resolvedUserId, spaceId);

        return (user, space);
    }

    /// <summary>
    /// Derives a friendly space name from the user's display name with a
    /// fallback chain:
    ///   1. displayName (trimmed) when non-empty
    ///   2. email local-part (before '@') when non-empty
    ///   3. "My space"
    /// Guarantees the result is never "'s space" with an empty name prefix.
    /// </summary>
    internal static string BuildSpaceName(string? displayName, string normalizedEmail)
    {
        var name = displayName?.Trim();
        if (!string.IsNullOrEmpty(name))
            return $"{name}'s space";

        var atIdx = normalizedEmail.IndexOf('@', StringComparison.Ordinal);
        // atIdx == -1 → no '@', use whole string; atIdx == 0 → empty local-part, skip.
        var localPart = atIdx > 0 ? normalizedEmail[..atIdx]
                      : atIdx < 0 ? normalizedEmail
                      : string.Empty;
        if (!string.IsNullOrEmpty(localPart))
            return $"{localPart}'s space";

        return "My space";
    }
}
