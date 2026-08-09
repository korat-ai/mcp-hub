namespace Korat.Cloud.Web.Auth;

/// <summary>
/// Well-known keys for <see cref="HttpContext.Items"/> populated by Korat's auth filters.
/// Promoting these to constants prevents drift between producer (PolymorphicAuthResolver via
/// RequireSpaceOwner filter) and consumers (Sub-project 2 per-user-isolation read sites).
/// </summary>
public static class KoratHttpContextItems
{
    /// <summary>
    /// The resolved <c>Korat.Domain.Auth.UserId</c> for the current authenticated request.
    /// Set by <c>RequireSpaceOwner</c> after successful <see cref="IAuthResolver"/>.ResolveAsync.
    /// Consumers cast: <c>(UserId)ctx.HttpContext.Items[KoratHttpContextItems.UserIdKey]!</c>.
    /// </summary>
    public const string UserIdKey = "KoratUserId";
}
