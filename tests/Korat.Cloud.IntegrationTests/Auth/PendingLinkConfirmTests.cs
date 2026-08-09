using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Р36: linking a second identity provider to an existing account had no compiling test — the
/// register's <c>link-additional-provider</c> entry said the flow was "confirmed by reading the
/// code only".
///
/// <para>Reading is a weak instrument for this particular flow, because what it does is
/// <b>merge two ways of proving you are someone</b>. Get it wrong in the permissive direction and
/// an attacker who controls any account at any provider with a matching email inherits an existing
/// Korat account, its Space, and every permission in it. The confirm step is the only human gate
/// on that, and it is driven by a signed cookie — so the properties that matter are: a forged or
/// absent cookie confirms nothing, and a genuine one is not replayable into duplicate identities.
/// </para>
/// </summary>
public sealed class PendingLinkConfirmTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Confirm_WithValidPendingCookie_AddsTheExternalLogin()
    {
        var seeded = await fixture.SeedUserAsync($"link-ok-{Guid.NewGuid():N}@example.com", "Link Owner");
        var providerUserId = $"gh-{Guid.NewGuid():N}";

        using var client = await CreateClientWithPendingLinkAsync(seeded.UserId, providerUserId);
        var response = await client.PostAsync("/api/auth/pending-link/confirm", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var link = await db.ExternalLogins.AsNoTracking()
            .SingleAsync(x => x.ProviderUserId == providerUserId);
        Assert.Equal(seeded.UserId, link.UserId);
        Assert.True(link.EmailVerified);
    }

    [Fact]
    public async Task Confirm_Twice_DoesNotCreateASecondIdentityRow()
    {
        // The confirm cookie lives for its whole TTL, so a double-click or a replay reaches this
        // endpoint twice. Without the idempotency check the second insert violates the unique
        // (Provider, ProviderUserId) index and the user sees a 500 on a flow that already
        // succeeded.
        var seeded = await fixture.SeedUserAsync($"link-twice-{Guid.NewGuid():N}@example.com", "Link Twice");
        var providerUserId = $"gh-{Guid.NewGuid():N}";

        using var client = await CreateClientWithPendingLinkAsync(seeded.UserId, providerUserId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/pending-link/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/pending-link/confirm", null)).StatusCode);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var count = await db.ExternalLogins.AsNoTracking()
            .CountAsync(x => x.ProviderUserId == providerUserId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Confirm_WithoutPendingCookie_LinksNothing()
    {
        var seeded = await fixture.SeedUserAsync($"link-none-{Guid.NewGuid():N}@example.com", "Link None");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var response = await client.PostAsync("/api/auth/pending-link/confirm", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-pending-link", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Confirm_WithForgedPendingCookie_LinksNothing()
    {
        // The cookie is data-protected. An attacker who can set cookies but cannot forge the
        // protector's signature must not be able to name a victim's user id and have the server
        // attach their own provider identity to it — that would be account takeover with the
        // confirm screen bypassed entirely.
        var victim = await fixture.SeedUserAsync($"link-victim-{Guid.NewGuid():N}@example.com", "Victim");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(victim.UserId);
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{CanonicalSigninHandler.PendingLinkCookieName}=not-a-real-protected-value");

        var response = await client.PostAsync("/api/auth/pending-link/confirm", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        Assert.False(await db.ExternalLogins.AsNoTracking().AnyAsync(x => x.UserId == victim.UserId));
    }

    [Fact]
    public async Task Cancel_RemovesThePendingLink_WithoutLinking()
    {
        var seeded = await fixture.SeedUserAsync($"link-cancel-{Guid.NewGuid():N}@example.com", "Link Cancel");
        var providerUserId = $"gh-{Guid.NewGuid():N}";

        using var client = await CreateClientWithPendingLinkAsync(seeded.UserId, providerUserId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/pending-link/cancel", null)).StatusCode);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        Assert.False(await db.ExternalLogins.AsNoTracking().AnyAsync(x => x.ProviderUserId == providerUserId));
    }

    // ── helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a genuine pending-link cookie through the real <see cref="IPendingLinkService"/>. The
    /// alternative — driving two full OAuth callbacks — would test the provider stubs rather than
    /// the merge, and the merge is what has no coverage.
    /// </summary>
    private async Task<HttpClient> CreateClientWithPendingLinkAsync(
        UserId existingUserId, string providerUserId)
    {
        var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(existingUserId);

        using var scope = fixture.Factory.Services.CreateScope();
        var pending = scope.ServiceProvider.GetRequiredService<IPendingLinkService>();
        var cookie = pending.Issue(new PendingLink(
            existingUserId,
            LoginProvider.GitHub,
            providerUserId,
            $"{providerUserId}@example.com",
            "Linked Identity",
            DateTimeOffset.UtcNow.AddMinutes(10)));

        client.DefaultRequestHeaders.Add(
            "Cookie", $"{CanonicalSigninHandler.PendingLinkCookieName}={cookie}");
        return client;
    }
}
