using System.Xml.Linq;
using Korat.Persistence;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.DataProtection;

/// <summary>
/// 010-drop-redis-to-postgres: Data Protection key repository backed by Postgres through
/// <see cref="KoratDbContext"/>. The app registers <see cref="IDbContextFactory{TContext}"/>
/// (not a scoped DbContext), so the built-in <c>PersistKeysToDbContext</c> — which resolves a
/// scoped context — can't be used. This adapter stores keys in the EF-managed
/// <c>DataProtectionKeys</c> table via the factory, keeping the codebase's factory pattern.
///
/// Keys are stored as XML; they are NOT encrypted at rest by default (same as the prior Redis
/// setup). Anyone with DB read access can read the key material — acceptable since DB access is
/// privileged; add ProtectKeysWith… for defense-in-depth if required.
/// </summary>
public sealed class DbContextXmlRepository(IDbContextFactory<KoratDbContext> contextFactory) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = contextFactory.CreateDbContext();
        return db.DataProtectionKeys
            .AsNoTracking()
            .Where(key => key.Xml != null)
            .Select(key => key.Xml!)
            .ToList()
            .Select(xml => XElement.Parse(xml))
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = contextFactory.CreateDbContext();
        db.DataProtectionKeys.Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}
