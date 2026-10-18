using System.Net;
using System.Net.Http.Headers;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

public sealed class SessionSuspensionTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendingUser_RejectsAlreadyIssuedCookieAndBearer(bool bearer)
    {
        var user = await fixture.SeedUserAsync($"suspended-http-{Guid.NewGuid():N}@test.local", "Owner");
        using var client = bearer
            ? fixture.Factory.CreateClient()
            : await fixture.CreateCookieAuthenticatedClientAsync(user.UserId);
        if (bearer)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await fixture.IssueCliTokenAsync(user.UserId));
        using var before = await client.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var stored = await db.Users.SingleAsync(u => u.Id == user.UserId);
            db.Entry(stored).CurrentValues.SetValues(stored with { Status = UserStatus.Suspended });
            await db.SaveChangesAsync();
        }

        using var after = await client.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
