using Korat.Domain.Auth;

namespace Korat.Cloud.Web.Auth.Services;

public sealed record PendingLink(UserId ExistingUserId, LoginProvider Provider, string ProviderUserId,
                                  string Email, string? DisplayName, DateTimeOffset ExpiresAt);

public interface IPendingLinkService
{
    string Issue(PendingLink link);
    PendingLink? TryRead(string protectedValue);
}
