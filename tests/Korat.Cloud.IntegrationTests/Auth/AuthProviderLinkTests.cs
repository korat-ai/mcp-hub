using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Tests the "connect provider" flow (spec 027): CanonicalSigninHandler.LinkAsync links
/// an OAuth-proven identity to the LIVE-session user, without an email match. Exercised
/// at the handler level with a DefaultHttpContext carrying the user's real session cookie
/// (resolved by the production IAuthResolver).
/// </summary>
public sealed class AuthProviderLinkTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private async Task<(IServiceScope Scope, CanonicalSigninHandler Handler, HttpContext Ctx)> AuthedAsync(UserId userId)
    {
        var scope = fixture.Factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var session = await sessions.CreateAsync(userId, "test-ua", "127.0.0.1", default);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{CanonicalSigninHandler.SessionCookieName}={session.Id:N}";
        var handler = scope.ServiceProvider.GetRequiredService<CanonicalSigninHandler>();
        return (scope, handler, ctx);
    }

    private static CanonicalSigninRequest LinkReq(LoginProvider provider, string providerUserId, bool verified = true) =>
        new(provider, providerUserId, $"{providerUserId}@example.com", verified, "Test", "/app/account/profile");

    [Fact]
    public async Task LinkAsync_links_new_provider_to_current_user()
    {
        var user = await fixture.SeedUserAsync($"link-{Guid.NewGuid():N}@example.com", "Link User");
        var (scope, handler, ctx) = await AuthedAsync(user.UserId);
        using (scope)
        {
            await handler.LinkAsync(ctx, LinkReq(LoginProvider.Google, "goog-new"), user.UserId.Value, default);

            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var link = await db.ExternalLogins.SingleOrDefaultAsync(
                x => x.Provider == LoginProvider.Google && x.ProviderUserId == "goog-new");
            Assert.NotNull(link);
            Assert.Equal(user.UserId, link!.UserId);
        }
    }

    [Fact]
    public async Task LinkAsync_rejects_identity_owned_by_another_user()
    {
        var owner = await fixture.SeedUserAsync($"owner-{Guid.NewGuid():N}@example.com", "Owner");
        var other = await fixture.SeedUserAsync($"other-{Guid.NewGuid():N}@example.com", "Other");
        const string sharedId = "goog-shared-xyz";

        // `owner` already has the Google identity.
        using (var seed = fixture.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<KoratDbContext>();
            db.ExternalLogins.Add(new ExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = owner.UserId,
                Provider = LoginProvider.Google,
                ProviderUserId = sharedId,
                EmailAtLink = "owner@example.com",
                EmailVerified = true,
                LinkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // `other` (logged in) tries to claim the same identity.
        var (scope, handler, ctx) = await AuthedAsync(other.UserId);
        using (scope)
        {
            await handler.LinkAsync(ctx, LinkReq(LoginProvider.Google, sharedId), other.UserId.Value, default);

            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var link = await db.ExternalLogins.SingleAsync(
                x => x.Provider == LoginProvider.Google && x.ProviderUserId == sharedId);
            // Still owned by the original user — never reassigned.
            Assert.Equal(owner.UserId, link.UserId);
        }
    }

    [Fact]
    public async Task LinkAsync_is_idempotent_for_already_linked_self()
    {
        var user = await fixture.SeedUserAsync($"idem-{Guid.NewGuid():N}@example.com", "Idem User");
        const string id = "goog-self";

        using (var seed = fixture.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<KoratDbContext>();
            db.ExternalLogins.Add(new ExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = user.UserId,
                Provider = LoginProvider.Google,
                ProviderUserId = id,
                EmailAtLink = "idem@example.com",
                EmailVerified = true,
                LinkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (scope, handler, ctx) = await AuthedAsync(user.UserId);
        using (scope)
        {
            await handler.LinkAsync(ctx, LinkReq(LoginProvider.Google, id), user.UserId.Value, default);

            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var count = await db.ExternalLogins.CountAsync(
                x => x.Provider == LoginProvider.Google && x.ProviderUserId == id);
            Assert.Equal(1, count); // no duplicate row
        }
    }

    [Fact]
    public async Task LinkAsync_rejects_when_no_live_session()
    {
        var user = await fixture.SeedUserAsync($"nosess-{Guid.NewGuid():N}@example.com", "NoSess");
        using var scope = fixture.Factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CanonicalSigninHandler>();
        var ctx = new DefaultHttpContext(); // no session cookie

        await handler.LinkAsync(ctx, LinkReq(LoginProvider.Google, "goog-nosess"), user.UserId.Value, default);

        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var any = await db.ExternalLogins.AnyAsync(x => x.ProviderUserId == "goog-nosess");
        Assert.False(any); // nothing linked without a live session
    }

    [Fact]
    public async Task LinkAsync_rejects_unverified_email()
    {
        var user = await fixture.SeedUserAsync($"unverif-{Guid.NewGuid():N}@example.com", "Unverif");
        var (scope, handler, ctx) = await AuthedAsync(user.UserId);
        using (scope)
        {
            await handler.LinkAsync(ctx, LinkReq(LoginProvider.Google, "goog-unverif", verified: false), user.UserId.Value, default);

            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            Assert.False(await db.ExternalLogins.AnyAsync(x => x.ProviderUserId == "goog-unverif"));
        }
    }
}
