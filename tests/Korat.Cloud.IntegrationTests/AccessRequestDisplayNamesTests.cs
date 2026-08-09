using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 028: access-request list endpoints must include consumerDisplayName and
/// mcpServerDisplayName so the console can render names instead of raw GUIDs.
/// Covers both GET /api/access-requests and the pendingAccessRequests array in
/// GET /api/space.
/// </summary>
public sealed class AccessRequestDisplayNamesTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── GET /api/access-requests ──────────────────────────────────────────────

    [Fact]
    public async Task AccessRequests_List_IncludesMcpServerDisplayName()
    {
        var user = await fixture.SeedUserAsync("acr-server-name@x.io", "A-AcrServerName");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string serverDisplayName = "korat-repo-fs-028";
        var server = (await grain.PublishMcpServerAsync(nodeId, serverDisplayName, "npx", "-y @mcp/fs"))!;

        var agentClientId = ConsumerId.New();
        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        var resp = await client.GetAsync("/api/access-requests");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var pending = doc!.RootElement.EnumerateArray().ToList();
        var item = pending.FirstOrDefault(r =>
            r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined,
            "Pending request for the seeded server was not returned.");

        var name = item.GetProperty("mcpServerDisplayName").GetString();
        Assert.Equal(serverDisplayName, name);
    }

    [Fact]
    public async Task AccessRequests_List_IncludesAgentClientDisplayName()
    {
        var user = await fixture.SeedUserAsync("acr-agent-name@x.io", "A-AcrAgentName");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string nodeDisplayName = "work-mac-028";

        // Register the node so the SpaceGrain can resolve its DisplayName for agent name lookup.
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = nodeDisplayName,
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-acr-agent-{Guid.NewGuid():N}", "echo", "x"))!;

        // Register an agent client on that node so ConsumerGrain.GetAsync returns a NodeId.
        var agentClientId = ConsumerId.New();
        var agentGrain = fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value);
        await agentGrain.RegisterAsync(new SpaceId(user.SpaceId), nodeId, "claude-code");

        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        var resp = await client.GetAsync("/api/access-requests");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var item = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined,
            "Pending request for the seeded agent was not returned.");

        // consumerDisplayName resolves via Consumer → NodeId → Node.DisplayName.
        var agentName = item.GetProperty("consumerDisplayName").GetString();
        Assert.Equal(nodeDisplayName, agentName);
    }

    [Fact]
    public async Task AccessRequests_List_IncludesPublisherNodeName()
    {
        // 144 [info]: /api/access-requests (this standalone list) was missing
        // publisherNodeName while /api/space's pendingAccessRequests already had it —
        // both access-request surfaces must agree on how to label the publisher.
        var user = await fixture.SeedUserAsync("acr-publisher-name@x.io", "A-AcrPublisherName");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string publisherNodeDisplayName = "publisher-node-144";
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = publisherNodeDisplayName,
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-acr-pub-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        var resp = await client.GetAsync("/api/access-requests");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var item = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined,
            "Pending request for the seeded server was not returned.");

        var publisherNodeName = item.GetProperty("publisherNodeName").GetString();
        Assert.Equal(publisherNodeDisplayName, publisherNodeName);
    }

    [Fact]
    public async Task AccessRequests_List_KeepsRawIdsAlongsideNames()
    {
        var user = await fixture.SeedUserAsync("acr-raw-ids@x.io", "A-AcrRawIds");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-raw-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var resp = await client.GetAsync("/api/access-requests");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var item = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined);

        // Raw id fields must be preserved.
        Assert.Equal(agentClientId.Value, item.GetProperty("consumerId").GetProperty("value").GetString());
        Assert.Equal(server.Id.Value, item.GetProperty("mcpServerId").GetProperty("value").GetString());
        // Display name fields must also be present.
        Assert.True(item.TryGetProperty("consumerDisplayName", out _),
            "consumerDisplayName must be present");
        Assert.True(item.TryGetProperty("mcpServerDisplayName", out _),
            "mcpServerDisplayName must be present");
    }

    // ── GET /api/space — pendingAccessRequests ────────────────────────────────

    [Fact]
    public async Task Space_PendingAccessRequests_IncludesMcpServerDisplayName()
    {
        var user = await fixture.SeedUserAsync("space-acr-server@x.io", "A-SpaceAcrServer");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string serverDisplayName = "space-server-028";
        var server = (await grain.PublishMcpServerAsync(nodeId, serverDisplayName, "npx", "-y @mcp/fs"))!;
        var agentClientId = ConsumerId.New();
        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        var resp = await client.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var pending = doc!.RootElement.GetProperty("pendingAccessRequests").EnumerateArray().ToList();
        var item = pending.FirstOrDefault(r =>
            r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined,
            "Pending request not found in /api/space response.");

        var name = item.GetProperty("mcpServerDisplayName").GetString();
        Assert.Equal(serverDisplayName, name);
    }

    [Fact]
    public async Task Space_PendingAccessRequests_IncludesAgentClientDisplayName()
    {
        var user = await fixture.SeedUserAsync("space-acr-agent@x.io", "A-SpaceAcrAgent");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string nodeDisplayName = "space-node-028";
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = nodeDisplayName,
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-space-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(user.SpaceId), nodeId, "cursor");

        var request = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        var resp = await client.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var item = doc!.RootElement.GetProperty("pendingAccessRequests")
            .EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);
        Assert.True(item.ValueKind != JsonValueKind.Undefined,
            "Pending request not found in /api/space pendingAccessRequests.");

        var agentName = item.GetProperty("consumerDisplayName").GetString();
        Assert.Equal(nodeDisplayName, agentName);
    }
}
