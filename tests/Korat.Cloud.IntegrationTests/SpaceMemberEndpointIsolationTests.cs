using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Task-7 endpoint isolation tests: access-request and grant endpoints must be
/// scoped to the caller's own Space (resolved via SpaceResolver) rather than the
/// shared synthetic "default" grain.
///
/// Pattern: seed two users; user A's access-requests / grants are not visible to
/// user B, and user B's mutations on user A's request IDs return 404 (no oracle).
/// </summary>
public sealed class SpaceMemberEndpointIsolationTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── access-request isolation ───────────────────────────────────────────────

    [Fact]
    public async Task AccessRequests_List_DoesNotLeakAnotherUsersRequests()
    {
        // Arrange: user A has a pending access request; user B has none.
        var a = await fixture.SeedUserAsync("ar-list-a7@x.io", "A7-AR");
        var b = await fixture.SeedUserAsync("ar-list-b7@x.io", "B7-AR");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-ar-list-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B lists access requests.
        var resp = await clientB.GetAsync("/api/access-requests");

        // Assert: 200 and user A's request id is absent.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(requestA.Id.Value, body);
    }

    [Fact]
    public async Task AccessRequests_List_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient().GetAsync("/api/access-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AccessRequests_GetById_CrossSpace_Returns404()
    {
        // Arrange: user A has a pending request; user B should not be able to see it.
        var a = await fixture.SeedUserAsync("ar-get-a7@x.io", "A7-ARGet");
        var b = await fixture.SeedUserAsync("ar-get-b7@x.io", "B7-ARGet");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-ar-get-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B GETs user A's access request by id.
        var resp = await clientB.GetAsync($"/api/access-requests/{requestA.Id.Value}");

        // Assert: 404 — no existence oracle (design §5).
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AccessRequests_Approve_CrossSpace_Returns404()
    {
        // Arrange: user A has a pending request; user B tries to approve it.
        var a = await fixture.SeedUserAsync("ar-approve-a7@x.io", "A7-ARApprove");
        var b = await fixture.SeedUserAsync("ar-approve-b7@x.io", "B7-ARApprove");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-ar-approve-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B POSTs approve on user A's request.
        var resp = await clientB.PostAsync($"/api/access-requests/{requestA.Id.Value}/approve", null);

        // Assert: 404 — no existence oracle.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Verify the request is still Pending in user A's grain (mutation was blocked).
        var requests = await fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId).ListAccessRequestsAsync();
        var r = requests.Single(rq => rq.Id == requestA.Id);
        Assert.Equal(AccessRequestStatus.Pending, r.Status);
    }

    [Fact]
    public async Task AccessRequests_Approve_OwnSpace_Succeeds()
    {
        // Arrange: user A has a pending request and approves it themselves.
        var a = await fixture.SeedUserAsync("ar-approve-self-a7@x.io", "A7-ARApproveSelf");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-ar-self-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);

        using var clientA = await fixture.CreateAuthenticatedClientAsync(a.UserId);

        // Act: user A approves their own request.
        var resp = await clientA.PostAsync($"/api/access-requests/{requestA.Id.Value}/approve", null);

        // Assert: 200 OK and grant is created.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var grants = await grainA.ListGrantsAsync();
        Assert.Contains(grants, g => g.Status == GrantStatus.Active && g.McpServerId == serverA.Id);
    }

    // ── grant isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Grants_List_DoesNotLeakAnotherUsersGrants()
    {
        // Arrange: user A has an active grant; user B has none.
        var a = await fixture.SeedUserAsync("grant-list-a7@x.io", "A7-Grant");
        var b = await fixture.SeedUserAsync("grant-list-b7@x.io", "B7-Grant");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-grant-list-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);
        var grant = await grainA.ApproveAccessRequestAsync(requestA.Id, a.UserId);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B lists grants.
        var resp = await clientB.GetAsync("/api/grants");

        // Assert: 200 and user A's grant id is absent.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(grant.Id.Value, body);
    }

    [Fact]
    public async Task Grants_List_Unauthenticated_Returns401()
    {
        var resp = await fixture.Factory.CreateClient().GetAsync("/api/grants");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Grants_Revoke_CrossSpace_Returns404()
    {
        // Arrange: user A has an active grant; user B tries to revoke it.
        var a = await fixture.SeedUserAsync("grant-revoke-a7@x.io", "A7-GrantRevoke");
        var b = await fixture.SeedUserAsync("grant-revoke-b7@x.io", "B7-GrantRevoke");

        var grainA = fixture.ClusterClient.GetGrain<ISpaceGrain>(a.SpaceId);
        var nodeId = NodeId.New();
        var serverA = (await grainA.PublishMcpServerAsync(nodeId, $"srv-grant-revoke-{Guid.NewGuid():N}", "echo", "x"))!;
        var requestA = await grainA.CreateAccessRequestAsync(ConsumerId.New(), serverA.Id, nodeId);
        var grantA = await grainA.ApproveAccessRequestAsync(requestA.Id, a.UserId);

        using var clientB = await fixture.CreateAuthenticatedClientAsync(b.UserId);

        // Act: user B tries to revoke user A's grant.
        var resp = await clientB.PostAsync($"/api/grants/{grantA.Id.Value}/revoke", null);

        // Assert: 404 — no existence oracle.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // Verify the grant is still Active in user A's grain.
        var grants = await grainA.ListGrantsAsync();
        Assert.Equal(GrantStatus.Active, grants.Single(g => g.Id == grantA.Id).Status);
    }
}
