using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for the Bootstrap:AdminEmail first-admin mechanism implemented in
/// <see cref="CanonicalSigninHandler"/>.
///
/// Covers four contracts:
/// (a) New user whose email == Bootstrap:AdminEmail is provisioned as admin without an invite.
/// (b) New user whose email != Bootstrap:AdminEmail still requires an invite.
/// (c) Existing non-admin user signing in with the admin email gets promoted (idempotent).
/// (d) Feature is off when Bootstrap:AdminEmail is unset — normal invite flow for everyone.
/// </summary>
public class AdminEmailBootstrapTests
{
    // ── stubs ────────────────────────────────────────────────────────────────

    private sealed class NoOpPendingLinkService : IPendingLinkService
    {
        public string Issue(PendingLink link) => "stub-token";
        public PendingLink? TryRead(string protectedValue) => null;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed record Sut(CanonicalSigninHandler Handler, KoratDbContext Db);

    private static Sut Build(string? adminEmail)
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new KoratDbContext(opts);

        var bootstrapOptions = Options.Create(new BootstrapOptions { AdminEmail = adminEmail });
        var sessions = new SessionService(db, NullLogger<SessionService>.Instance, TimeProvider.System);
        var pendingLinks = new NoOpPendingLinkService();
        var provisioning = new UserProvisioningService(db, TimeProvider.System, NullLogger<UserProvisioningService>.Instance);
        var authResolver = new NoSessionAuthResolver();
        var handler = new CanonicalSigninHandler(
            db, sessions, pendingLinks, provisioning, authResolver, bootstrapOptions,
            NullLogger<CanonicalSigninHandler>.Instance, TimeProvider.System);

        return new Sut(handler, db);
    }

    /// <summary>These tests exercise CompleteAsync (signin), not the connect-provider
    /// LinkAsync path, so the resolver is never invoked — return no session.</summary>
    private sealed class NoSessionAuthResolver : IAuthResolver
    {
        public Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct) =>
            Task.FromResult<ResolvedIdentity?>(null);
    }

    private static HttpContext FakeHttpContext() =>
        new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = System.Net.IPAddress.Loopback },
        };

    private static CanonicalSigninRequest MakeRequest(string email, string? inviteCode = null) =>
        new(
            Provider: LoginProvider.MagicLink,
            ProviderUserId: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(email))),
            Email: email,
            EmailVerified: true,
            DisplayName: "Test User",
            ReturnUrl: "/app/");

    // ── (a) new user with admin email — provisioned as admin, no invite ────

    [Fact]
    public async Task NewUser_WithAdminEmail_IsProvisionedAsAdmin_WithoutInvite()
    {
        var sut = Build(adminEmail: "admin@example.com");

        await sut.Handler.CompleteAsync(FakeHttpContext(), MakeRequest("admin@example.com"), default);

        var user = await sut.Db.Users.SingleOrDefaultAsync();
        Assert.NotNull(user);
        Assert.True(user!.IsAdmin);
        Assert.Equal("admin@example.com", user.PrimaryEmail);

        // A default Space must have been created via the shared seam.
        var space = await sut.Db.Spaces.SingleOrDefaultAsync(s => s.IsDefault);
        Assert.NotNull(space);
    }

    [Fact]
    public async Task NewUser_WithAdminEmail_CaseInsensitive_IsProvisionedAsAdmin()
    {
        // Bootstrap:AdminEmail configured as lowercase; sign in with mixed case + whitespace.
        var sut = Build(adminEmail: "admin@example.com");

        await sut.Handler.CompleteAsync(FakeHttpContext(), MakeRequest("  Admin@Example.COM  "), default);

        var user = await sut.Db.Users.SingleOrDefaultAsync();
        Assert.NotNull(user);
        Assert.True(user!.IsAdmin);
    }

    // ── (b) registration is open: a non-admin email creates a plain account ──

    [Fact]
    public async Task NewUser_WithNonAdminEmail_IsProvisionedAsNonAdmin()
    {
        // Before open registration this case was rejected outright (no invite code → no
        // account). Bootstrap:AdminEmail now decides admin-ness only, never admission.
        var sut = Build(adminEmail: "admin@example.com");

        await sut.Handler.CompleteAsync(FakeHttpContext(), MakeRequest("other@example.com"), default);

        var user = await sut.Db.Users.SingleAsync(u => u.PrimaryEmail == "other@example.com");
        Assert.False(user.IsAdmin);
    }

    // ── (c) existing non-admin user signing in with admin email gets promoted ─

    [Fact]
    public async Task ExistingNonAdminUser_WithAdminEmail_GetsPromoted()
    {
        var sut = Build(adminEmail: "admin@example.com");

        // Seed an existing active user with the admin email but IsAdmin=false.
        var now = DateTimeOffset.UtcNow;
        var userId = UserId.New();
        const string providerUserId = "existing-provider-id";

        sut.Db.Users.Add(new User
        {
            Id = userId,
            PrimaryEmail = "admin@example.com",
            DisplayName = "Pre-existing User",
            CreatedAt = now,
            Status = UserStatus.Active,
            IsAdmin = false,
        });
        sut.Db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = LoginProvider.MagicLink,
            ProviderUserId = providerUserId,
            EmailAtLink = "admin@example.com",
            EmailVerified = true,
            LinkedAt = now,
        });
        // A default Space so the returning-user session creation does not fail.
        sut.Db.Spaces.Add(new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerUserId = userId.Value.ToString("N"),
            DisplayName = "Admin Space",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await sut.Db.SaveChangesAsync();

        var req = new CanonicalSigninRequest(
            Provider: LoginProvider.MagicLink,
            ProviderUserId: providerUserId,
            Email: "admin@example.com",
            EmailVerified: true,
            DisplayName: null,
            ReturnUrl: "/app/");

        await sut.Handler.CompleteAsync(FakeHttpContext(), req, default);

        var user = await sut.Db.Users.SingleAsync(u => u.Id == userId);
        Assert.True(user.IsAdmin);
    }

    [Fact]
    public async Task ExistingAdminUser_WithAdminEmail_RemainsAdmin_Idempotent()
    {
        var sut = Build(adminEmail: "admin@example.com");

        var now = DateTimeOffset.UtcNow;
        var userId = UserId.New();
        const string providerUserId = "already-admin-provider-id";

        sut.Db.Users.Add(new User
        {
            Id = userId,
            PrimaryEmail = "admin@example.com",
            DisplayName = "Already Admin",
            CreatedAt = now,
            Status = UserStatus.Active,
            IsAdmin = true,
        });
        sut.Db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = LoginProvider.MagicLink,
            ProviderUserId = providerUserId,
            EmailAtLink = "admin@example.com",
            EmailVerified = true,
            LinkedAt = now,
        });
        sut.Db.Spaces.Add(new SpaceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerUserId = userId.Value.ToString("N"),
            DisplayName = "Admin Space",
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await sut.Db.SaveChangesAsync();

        var req = new CanonicalSigninRequest(
            Provider: LoginProvider.MagicLink,
            ProviderUserId: providerUserId,
            Email: "admin@example.com",
            EmailVerified: true,
            DisplayName: null,
            ReturnUrl: "/app/");

        // Should not throw; IsAdmin should still be true afterwards.
        await sut.Handler.CompleteAsync(FakeHttpContext(), req, default);

        var user = await sut.Db.Users.SingleAsync(u => u.Id == userId);
        Assert.True(user.IsAdmin);
    }

    // ── (d) feature off when Bootstrap:AdminEmail unset — account yes, admin no ──
    //
    // These two used to assert rejection: with the invite gate in place, an unset
    // Bootstrap:AdminEmail meant nobody could sign up at all. Registration is open now,
    // so the surviving claim is narrower and the one that actually matters — an unset
    // (or blank) setting must never hand out admin.

    [Fact]
    public async Task NewUser_FeatureOff_NullAdminEmail_IsNotAdmin()
    {
        var sut = Build(adminEmail: null);

        await sut.Handler.CompleteAsync(FakeHttpContext(), MakeRequest("anyemail@example.com"), default);

        var user = await sut.Db.Users.SingleAsync();
        Assert.False(user.IsAdmin);
    }

    [Fact]
    public async Task NewUser_FeatureOff_EmptyAdminEmail_IsNotAdmin()
    {
        var sut = Build(adminEmail: "");

        await sut.Handler.CompleteAsync(FakeHttpContext(), MakeRequest("anyemail@example.com"), default);

        var user = await sut.Db.Users.SingleAsync();
        Assert.False(user.IsAdmin);
    }
}
