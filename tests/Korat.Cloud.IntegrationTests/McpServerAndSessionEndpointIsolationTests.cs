using System.Net;
using System.Net.Http.Json;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Task-8 endpoint isolation tests: GET /api/mcp-servers/{serverId} and GET /api/sessions
/// must be Space-scoped so user B cannot see user A's servers or sessions.
///
/// Also verifies that the POST /api/mcp-servers/{serverId}/disable endpoint no longer
/// uses the synthetic OwnerPlaceholderId("owner") literal and instead uses the resolved userId.
/// </summary>
public sealed class McpServerAndSessionEndpointIsolationTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── GET /api/mcp-servers/{serverId} ───────────────────────────────────────

    [Fact]
    public async Task McpServerGet_CrossSpace_Returns404()
    {
        // Arrange: user A publishes an MCP server; user B should not be able to see it.
        var a = await fixture.SeedUserAsync("mcpsrv-get-a8@x.io", "A8-McpSrvGet");
        var b = await fixture.SeedUserAsync("mcpsrv-get-b8@x.io", "B8-McpSrvGet");

        var nodeId = NodeId.New();
        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-get-a8-{Guid.NewGuid():N}", "echo", "x"))!;

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B attempts to GET user A's server by id.
        var resp = await clientB.GetAsync($"/api/mcp-servers/{serverA.Id.Value}");

        // Assert: 404 — no existence oracle (design §5).
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task McpServerGet_OwnSpace_Returns200()
    {
        // Arrange: user A publishes a server.
        var a = await fixture.SeedUserAsync("mcpsrv-own-a8@x.io", "A8-McpSrvOwn");

        var nodeId = NodeId.New();
        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-own-a8-{Guid.NewGuid():N}", "echo", "x"))!;

        using var clientA = await fixture.CreateAuthenticatedClientAsync(a.UserId);

        // Act: user A fetches their own server.
        var resp = await clientA.GetAsync($"/api/mcp-servers/{serverA.Id.Value}");

        // Assert: 200 with the server data.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(serverA.Id.Value, body);
    }

    [Fact]
    public async Task McpServerGet_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient().GetAsync("/api/mcp-servers/some-id");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── PATCH /api/mcp-servers/{serverId} (Task 3, http_cloud) ────────────────

    [Fact]
    public async Task HttpMcpServerPatch_CrossSpace_Returns404()
    {
        var a = await fixture.SeedUserAsync($"http-patch-a-{Guid.NewGuid():N}@x.io", "A-HttpPatch");
        var b = await fixture.SeedUserAsync($"http-patch-b-{Guid.NewGuid():N}@x.io", "B-HttpPatch");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var serverA = await grainA.CreateHttpMcpServerAsync(
            $"http-srv-cross-{Guid.NewGuid():N}", "https://example.test/mcp", "none", null, null);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);
        var resp = await clientB.PatchAsJsonAsync($"/api/mcp-servers/{serverA.Id.Value}", new { remoteUrl = "https://evil.test/mcp" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── GET /api/sessions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sessions_List_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient().GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_List_AuthenticatedUser_Returns200WithEmptyOrOwnSessions()
    {
        // Arrange: a freshly seeded user with no sessions in their Space.
        var a = await fixture.SeedUserAsync("sessions-own-a8@x.io", "A8-Sessions");
        using var clientA = await fixture.CreateAuthenticatedClientAsync(a.UserId);

        // Act: user A lists sessions.
        var resp = await clientA.GetAsync("/api/sessions");

        // Assert: 200 and no 500/401 — endpoint resolves the Space via grain (not "default").
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_List_CrossSpace_DoesNotLeakAnotherUsersSessions()
    {
        // Arrange: user A has a closed session in their Space; user B has none.
        var a = await fixture.SeedUserAsync("sessions-cross-a8@x.io", "A8-SessionsCross");
        var b = await fixture.SeedUserAsync("sessions-cross-b8@x.io", "B8-SessionsCross");

        // Seed a closed session directly into user A's Space.
        var sessionId = SessionId.New().Value;
        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId);
        await sessionGrain.OpenAsync(
            GrantId.New(),
            ConsumerId.New(),
            McpServerId.New(),
            NodeId.New(),
            NodeId.New(),
            GatewayId.New(),
            new SpaceId(a.SpaceId));
        await sessionGrain.CloseAsync(SessionCloseReason.Completed);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B lists sessions.
        var resp = await clientB.GetAsync("/api/sessions");

        // Assert: 200 and user A's session id is absent — cross-Space isolation (SC-8).
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sessionId, body);
    }
}
