using Korat.Domain.Auth;
using Korat.Persistence;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Creates users with their default Space atomically (G2, SC-2).
/// All user creation (invite-based new users and Bootstrap:AdminEmail first-admin)
/// goes through this single seam — no user can exist without a default Space.
/// </summary>
public interface IUserProvisioningService
{
    /// <summary>
    /// Atomically inserts a User + default Space + owner SpaceMember in a single
    /// SaveChangesAsync call. Returns the created entities.
    /// <paramref name="isAdmin"/> is true for the Bootstrap:AdminEmail first-admin path (SC-3).
    /// </summary>
    Task<(User User, SpaceRecord Space)> CreateUserWithDefaultSpaceAsync(
        string email, string displayName, CancellationToken ct, bool isAdmin = false, UserId? userId = null);
}
