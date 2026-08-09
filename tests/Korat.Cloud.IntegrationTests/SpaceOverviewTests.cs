using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Р36: <c>GET /api/space</c> is the single request almost the whole console renders from —
/// overview, servers, runtimes — and it had no direct test. The register's
/// <c>space-dashboard-overview</c> entry said exactly that.
///
/// <para>Two properties matter more than the field list. First, <b>isolation</b>: this endpoint
/// resolves the Space from the caller's identity, never from a parameter, so one owner must never
/// see another's runtimes or servers through it. Second, <b>the presence contract</b>: the console
/// derives online/offline itself from <c>serverTime</c> and <c>presenceStaleSeconds</c> rather
/// than trusting a stored flag. Drop either field and the console silently starts reporting stale
/// runtimes as live — no error anywhere, just a wrong answer on the screen an owner uses to decide
/// whether something is reachable.</para>
/// </summary>
public sealed class SpaceOverviewTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Get_ReturnsOnlyTheCallersOwnSpaceContents()
    {
        var (mine, myServerName) = await SeedSpaceWithServerAsync("mine");
        var (theirs, theirServerName) = await SeedSpaceWithServerAsync("theirs");

        using var client = await fixture.CreateAuthenticatedClientAsync(mine.UserId);
        var body = await client.GetFromJsonAsync<JsonElement>("/api/space");

        Assert.Equal(mine.SpaceId, body.GetProperty("id").GetProperty("value").GetString());

        var serverNames = body.GetProperty("mcpServers").EnumerateArray()
            .Select(s => s.GetProperty("displayName").GetString())
            .ToList();
        Assert.Contains(myServerName, serverNames);
        Assert.DoesNotContain(theirServerName, serverNames);

        // Belt and braces: the other owner's Space id must not appear anywhere in the payload.
        // A leak through some field other than mcpServers would otherwise pass the check above.
        Assert.DoesNotContain(theirs.SpaceId, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_CarriesThePresenceContract_TheConsoleComputesFrom()
    {
        var seeded = await fixture.SeedUserAsync($"overview-presence-{Guid.NewGuid():N}@example.com", "Presence");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var body = await client.GetFromJsonAsync<JsonElement>("/api/space");
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        // serverTime must be the SERVER's clock, not an echo of anything the client sent: the
        // console subtracts lastSeenAt from it, so a client-derived value would make presence
        // depend on the viewer's clock skew.
        var serverTime = body.GetProperty("serverTime").GetDateTimeOffset();
        Assert.InRange(serverTime, before, after);

        // The threshold is published rather than duplicated in the frontend, so both sides cannot
        // disagree about what "stale" means. Asserting it equals the domain rule is the invariant;
        // a hard-coded number here would just be the same duplication moved into the test.
        Assert.Equal(
            (int)NodePresenceRules.StaleThreshold.TotalSeconds,
            body.GetProperty("presenceStaleSeconds").GetInt32());
    }

    [Fact]
    public async Task Get_Unauthenticated_Returns401()
    {
        using var anon = fixture.Factory.CreateClient();
        var response = await anon.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(KoratIntegrationFixture.SeededUser User, string ServerName)> SeedSpaceWithServerAsync(string tag)
    {
        var seeded = await fixture.SeedUserAsync(
            $"overview-{tag}-{Guid.NewGuid():N}@example.com", $"Overview {tag}");
        var serverName = $"srv-{tag}-{Guid.NewGuid():N}";
        await fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId)
            .PublishMcpServerAsync(NodeId.New(), serverName, "echo", tag);
        return (seeded, serverName);
    }
}
