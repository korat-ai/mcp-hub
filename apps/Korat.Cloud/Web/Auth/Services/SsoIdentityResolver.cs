using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth.Services;

public interface ISsoIdentityResolver
{
    /// <summary>
    /// The person this SSO subject already belongs to here, or null when nobody has linked it.
    /// Never creates anything.
    /// </summary>
    Task<UserId?> FindAsync(string ssoSubject, CancellationToken ct);
}

/// <summary>
/// Turns an SSO subject into a person known to this app.
///
/// The link lives in the existing ExternalLogins table, with the provider set to
/// <see cref="LoginProvider.KoratSso"/> — no new table. That table already answers exactly this
/// question ("which external identity is which local person") for GitHub and Google, and the
/// SSO subject is the same kind of fact.
///
/// Lookup only, never creation, and that is the point. A bearer token is presented on the
/// relay port and on the MCP surface, neither of which has a rate limit or a human in front of
/// it; creating accounts there would mean anyone holding a token from the provider could
/// populate this database. Accounts are created where a person is actually present — the
/// browser sign-in path, through <see cref="IUserProvisioningService"/>, which also gives them
/// a default Space. Without that, an auto-created user resolves to no Space and fails on the
/// next call anyway, having left a row behind.
/// </summary>
public sealed class SsoIdentityResolver(KoratDbContext db) : ISsoIdentityResolver
{
    public async Task<UserId?> FindAsync(string ssoSubject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ssoSubject)) return null;

        // Блокировка учётки проверяется здесь, а не только у провайдера.
        //
        // У провайдера своя база людей и своя блокировка; наша — про доступ к ЭТОМУ
        // приложению и заменена ею не будет. Старый пропуск гаснет мгновенно, потому что
        // CliTokenService требует активного статуса; без такого же условия здесь два
        // способа входа одного человека отвечали бы по-разному, и блокировка молча
        // перестала бы действовать ровно тогда, когда все переедут на новый путь.
        var userId = await db.ExternalLogins
            .AsNoTracking()
            .Where(l => l.Provider == LoginProvider.KoratSso && l.ProviderUserId == ssoSubject)
            .Join(db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active),
                  l => l.UserId, u => u.Id, (l, _) => (UserId?)l.UserId)
            .FirstOrDefaultAsync(ct);

        return userId;
    }
}
