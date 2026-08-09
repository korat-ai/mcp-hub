using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Korat.Auth.Tests;

/// <summary>
/// Turning an SSO subject into a person known here.
///
/// The suspension case is the one that matters: the provider keeps its own list of people and
/// its own suspension, and ours is about access to THIS app — theirs does not replace it. The
/// old credential died instantly because CliTokenService requires an active status. Without the
/// same condition here, the two ways one person signs in would answer differently, and
/// suspension would quietly stop working exactly when everyone had moved to the new path.
/// </summary>
public sealed class SsoIdentityResolverTests
{
    private const string Subject = "9fdc73931e2548528f467372bc838d7d";

    private static KoratDbContext NewDb(InMemoryDatabaseRoot root, string name) =>
        new(new DbContextOptionsBuilder<KoratDbContext>().UseInMemoryDatabase(name, root).Options);

    private static async Task<(KoratDbContext Db, UserId UserId)> SeedAsync(UserStatus status)
    {
        var db = NewDb(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var userId = UserId.New();

        db.Users.Add(new User
        {
            Id = userId,
            PrimaryEmail = "me@example.test",
            DisplayName = "Me",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            IsAdmin = false,
        });
        db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = LoginProvider.KoratSso,
            ProviderUserId = Subject,
            EmailAtLink = "me@example.test",
            EmailVerified = true,
            LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (db, userId);
    }

    [Fact]
    public async Task An_active_person_is_found()
    {
        var (db, userId) = await SeedAsync(UserStatus.Active);
        await using var _ = db;

        Assert.Equal(userId, await new SsoIdentityResolver(db).FindAsync(Subject, CancellationToken.None));
    }

    [Fact]
    public async Task A_suspended_person_is_not()
    {
        var (db, _) = await SeedAsync(UserStatus.Suspended);
        await using var __ = db;

        // The link row still exists — suspension does not delete it. Reading the link without
        // looking at the status is exactly the mistake this pins.
        Assert.Null(await new SsoIdentityResolver(db).FindAsync(Subject, CancellationToken.None));
    }

    [Fact]
    public async Task A_subject_nobody_linked_is_not_found()
    {
        var (db, _) = await SeedAsync(UserStatus.Active);
        await using var __ = db;

        Assert.Null(await new SsoIdentityResolver(db).FindAsync("someone-else", CancellationToken.None));
    }

    [Fact]
    public async Task A_link_from_another_provider_does_not_count()
    {
        var db = NewDb(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        await using var __ = db;

        var userId = UserId.New();
        db.Users.Add(new User
        {
            Id = userId, PrimaryEmail = "me@example.test", DisplayName = "Me",
            Status = UserStatus.Active, CreatedAt = DateTimeOffset.UtcNow, IsAdmin = false,
        });
        // Same string, different provider. GitHub subject ids and SSO subject ids live in one
        // column; matching on the value alone would let one provider's id resolve to another's
        // person.
        db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(), UserId = userId, Provider = LoginProvider.GitHub,
            ProviderUserId = Subject, EmailAtLink = "me@example.test",
            EmailVerified = true, LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Null(await new SsoIdentityResolver(db).FindAsync(Subject, CancellationToken.None));
    }
}
