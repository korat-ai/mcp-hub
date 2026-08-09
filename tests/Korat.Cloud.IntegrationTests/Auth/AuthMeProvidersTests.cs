using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Regression test for the GET /api/auth/me "providers" projection that feeds the
/// account page's "Connected providers" list. The endpoint previously omitted the
/// caller's ExternalLogin rows entirely, so a linked account always rendered
/// "No connected providers" (caught during the prod cutover login check).
/// </summary>
public sealed class AuthMeProvidersTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Me_returns_linked_providers()
    {
        var seeded = await fixture.SeedUserAsync($"me-prov-{Guid.NewGuid():N}@example.com", "Prov User");

        // Link a GitHub identity — mirrors what CanonicalSigninHandler writes at signin.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            db.ExternalLogins.Add(new ExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = seeded.UserId,
                Provider = LoginProvider.GitHub,
                ProviderUserId = "gh-12345",
                EmailAtLink = "me-prov@example.com",
                EmailVerified = true,
                LinkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);
        var me = await client.GetFromJsonAsync<JsonDocument>("/api/auth/me");

        Assert.NotNull(me);
        var providers = me!.RootElement.GetProperty("providers");
        Assert.Equal(1, providers.GetArrayLength());
        Assert.Equal("github", providers[0].GetProperty("provider").GetString());
        Assert.Equal("gh-12345", providers[0].GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Me_returns_empty_providers_when_none_linked()
    {
        var seeded = await fixture.SeedUserAsync($"me-noprov-{Guid.NewGuid():N}@example.com", "NoProv User");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var me = await client.GetFromJsonAsync<JsonDocument>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal(0, me!.RootElement.GetProperty("providers").GetArrayLength());
    }
}
