using Korat.Cloud.Web.Auth.Services;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>Unit tests for the BuildSpaceName fallback chain.</summary>
public class BuildSpaceNameTests
{
    [Theory]
    [InlineData("Alice",    "alice@x.io",    "Alice's space")]
    [InlineData("  Bob  ",  "bob@x.io",      "Bob's space")]   // trim
    [InlineData(null,       "carol@x.io",    "carol's space")]  // fallback → local-part
    [InlineData("",         "dave@x.io",     "dave's space")]   // empty → fallback
    [InlineData("   ",      "eve@x.io",      "eve's space")]    // whitespace → fallback
    [InlineData(null,       "noemail",       "noemail's space")] // no @, whole string
    [InlineData(null,       "",              "My space")]        // ultimate fallback
    [InlineData(null,       "@x.io",         "My space")]        // empty local-part → ultimate fallback
    public void BuildSpaceName_FallbackChain(string? displayName, string email, string expected)
    {
        var result = UserProvisioningService.BuildSpaceName(displayName, email);
        Assert.Equal(expected, result);
    }
}

public class UserProvisioningTests
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
    public async Task CreateUserWithDefaultSpaceAsync_CreatesUserSpaceAndOwnerMember()
    {
        // InMemory race-safety disclaimer: EF Core InMemory does not support raw SQL and
        // cannot serialise concurrent writes. This test exercises the happy-path atomicity
        // shape only. Production uses the Postgres branch.
        var (svc, db) = Build();

        var (user, space) = await svc.CreateUserWithDefaultSpaceAsync("u@x.io", "U", default);

        Assert.Equal(user.Id.Value.ToString("N"), space.OwnerUserId);
        Assert.True(space.IsDefault);
        Assert.True(await db.SpaceMembers.AnyAsync(m =>
            m.SpaceId == space.Id && m.UserId == user.Id.Value.ToString("N")));
    }

    [Fact]
    public async Task CreateUserWithDefaultSpaceAsync_SetsExpectedFields()
    {
        var (svc, db) = Build();

        var (user, space) = await svc.CreateUserWithDefaultSpaceAsync("alice@example.com", "Alice", default);

        Assert.Equal("alice@example.com", user.PrimaryEmail);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal("Alice's space", space.DisplayName);
        Assert.True(space.IsDefault);
        Assert.Equal(user.Id.Value.ToString("N"), space.OwnerUserId);
    }

    [Fact]
    public async Task CreateUserWithDefaultSpaceAsync_TwoUsers_GetDistinctSpaces()
    {
        var (svc, _) = Build();

        var (userA, spaceA) = await svc.CreateUserWithDefaultSpaceAsync("a@x.io", "A", default);
        var (userB, spaceB) = await svc.CreateUserWithDefaultSpaceAsync("b@x.io", "B", default);

        Assert.NotEqual(userA.Id, userB.Id);
        Assert.NotEqual(spaceA.Id, spaceB.Id);
        Assert.NotEqual(spaceA.OwnerUserId, spaceB.OwnerUserId);
    }
}
