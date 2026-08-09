using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

public class SpaceResolverTests
{
    private static (UserProvisioningService svc, KoratDbContext db) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var svc = new UserProvisioningService(db, TimeProvider.System, NullLogger<UserProvisioningService>.Instance);
        return (svc, db);
    }

    [Fact]
    public async Task ResolveDefaultSpaceIdAsync_ReturnsOwnersDefaultSpace()
    {
        // InMemory race-safety disclaimer: EF Core InMemory does not enforce filtered
        // unique indexes. This test exercises the happy-path lookup only. Production
        // uses the Postgres branch where the filtered index guarantees exactly one
        // default Space per owner.
        var (svc, db) = Build();
        var (user, space) = await svc.CreateUserWithDefaultSpaceAsync("r@x.io", "R", default);

        var resolver = new SpaceResolver(db, NullLogger<SpaceResolver>.Instance);
        var resolved = await resolver.ResolveDefaultSpaceIdAsync(user.Id, default);

        Assert.NotNull(resolved);
        Assert.Equal(new SpaceId(space.Id), resolved!.Value);
    }

    [Fact]
    public async Task ResolveDefaultSpaceIdAsync_UnknownUser_ReturnsNull()
    {
        var (_, db) = Build();
        var resolver = new SpaceResolver(db, NullLogger<SpaceResolver>.Instance);

        var resolved = await resolver.ResolveDefaultSpaceIdAsync(UserId.New(), default);

        Assert.Null(resolved);
    }
}
