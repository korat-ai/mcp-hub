using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 020-B: Grants and Sessions endpoints must include agentName + serverName
/// resolved from raw ids so the console stops showing 32-hex GUIDs.
/// </summary>
public sealed class FriendlyNamesEndpointTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── GET /api/grants — agentName + serverName ──────────────────────────────

    [Fact]
    public async Task Grants_List_IncludesServerName_EqualToPublishedDisplayName()
    {
        // Arrange: user with a node, an MCP server, an agent client, and an active grant.
        var user = await fixture.SeedUserAsync("grants-names-a@x.io", "A-GrantNames");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        // Publish a server with a recognisable name.
        var nodeId = NodeId.New();
        const string serverDisplayName = "my-fancy-server-020b";
        var server = (await grain.PublishMcpServerAsync(nodeId, serverDisplayName, "npx", "-y @mcp/server"))!;

        // Create an agent client on the same node.
        var agentClientId = ConsumerId.New();
        var agentGrain = fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value);
        await agentGrain.RegisterAsync(new SpaceId(user.SpaceId), nodeId, "cursor");

        // Seed a grant via access-request → approve.
        var accessRequest = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);
        await grain.ApproveAccessRequestAsync(accessRequest.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        // Act.
        var resp = await client.GetAsync("/api/grants");

        // Assert: 200 and serverName matches.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var grants = doc!.RootElement.EnumerateArray().ToList();
        // Find the grant for our server.
        var grant = grants.FirstOrDefault(g =>
            g.GetProperty("mcpServerId").GetString() == server.Id.Value);
        Assert.True(grant.ValueKind != JsonValueKind.Undefined, "Grant for the seeded server was not returned.");

        var serverName = grant.GetProperty("serverName").GetString();
        Assert.Equal(serverDisplayName, serverName);
    }

    [Fact]
    public async Task Grants_List_IncludesAgentName_ResolvedViaAgentClientGrain()
    {
        // Arrange: an agent client whose NodeId resolves to a known node DisplayName.
        var user = await fixture.SeedUserAsync("grants-agent-name@x.io", "A-GrantAgentName");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string nodeDisplayName = "cursor-node-020b";

        // Register the node so SpaceGrain has it in its node list.
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = nodeDisplayName,
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-agent-name-{Guid.NewGuid():N}", "echo", "x"))!;

        // Register agent client tied to that node.
        var agentClientId = ConsumerId.New();
        var agentGrain = fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value);
        await agentGrain.RegisterAsync(new SpaceId(user.SpaceId), nodeId, "cursor");

        var accessRequest = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);
        await grain.ApproveAccessRequestAsync(accessRequest.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        // Act.
        var resp = await client.GetAsync("/api/grants");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var grant = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(g => g.GetProperty("agentClientId").GetString() == agentClientId.Value);
        Assert.True(grant.ValueKind != JsonValueKind.Undefined, "Grant for the seeded agent was not returned.");

        // The agentName must be the node's DisplayName (resolved via Consumer → NodeId → Node).
        var agentName = grant.GetProperty("agentName").GetString();
        Assert.Equal(nodeDisplayName, agentName);
    }

    [Fact]
    public async Task Grants_List_IncludesRawIds_AlongsideNames()
    {
        // Spec: keep the raw ids in the payload (frontend shows them as secondary).
        var user = await fixture.SeedUserAsync("grants-raw-ids@x.io", "A-GrantRawIds");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        var server = (await grain.PublishMcpServerAsync(nodeId, $"srv-raw-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentGrain = fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value);
        await agentGrain.RegisterAsync(new SpaceId(user.SpaceId), nodeId, "raw-agent");

        var req = await grain.CreateAccessRequestAsync(agentClientId, server.Id, nodeId);
        await grain.ApproveAccessRequestAsync(req.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var resp = await client.GetAsync("/api/grants");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var grant = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(g => g.GetProperty("mcpServerId").GetString() == server.Id.Value);
        Assert.True(grant.ValueKind != JsonValueKind.Undefined);

        // Raw ids must be present.
        Assert.Equal(server.Id.Value, grant.GetProperty("mcpServerId").GetString());
        Assert.Equal(agentClientId.Value, grant.GetProperty("agentClientId").GetString());
        // Friendly names must also be present.
        Assert.True(grant.TryGetProperty("serverName", out _), "serverName must be present");
        Assert.True(grant.TryGetProperty("agentName", out _), "agentName must be present");
    }

    // ── GET /api/sessions — agentName + serverName ───────────────────────────

    [Fact]
    public async Task Sessions_List_IncludesServerName_AndAgentName()
    {
        // Arrange: seed a closed session with a known server and agent.
        var user = await fixture.SeedUserAsync("sessions-names@x.io", "A-SessionNames");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);

        var nodeId = NodeId.New();
        const string serverDisplayName = "session-server-020b";

        // Register node so it appears in node list.
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = "session-node-020b",
            Status = NodeStatus.Online,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var server = (await grain.PublishMcpServerAsync(nodeId, serverDisplayName, "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentGrain = fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value);
        await agentGrain.RegisterAsync(new SpaceId(user.SpaceId), nodeId, "session-agent");

        // Open + close a session directly via ISessionGrain.
        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(
            GrantId.New(),
            agentClientId,
            server.Id,
            nodeId,
            nodeId,
            GatewayId.New(),
            new SpaceId(user.SpaceId));
        await sessionGrain.CloseAsync(SessionCloseReason.Completed);

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);

        // Act.
        var resp = await client.GetAsync("/api/sessions");

        // Assert: 200 with serverName present and equal to the published display name.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);

        var sessions = doc!.RootElement.EnumerateArray().ToList();
        var session = sessions.FirstOrDefault(s =>
            s.GetProperty("mcpServerId").GetString() == server.Id.Value);
        Assert.True(session.ValueKind != JsonValueKind.Undefined,
            "Session for the seeded server was not returned.");

        Assert.Equal(serverDisplayName, session.GetProperty("serverName").GetString());
        // agentName should resolve to the node's DisplayName via Consumer grain.
        Assert.True(session.TryGetProperty("agentName", out var agentNameEl), "agentName must be present");
        Assert.NotNull(agentNameEl.GetString());
        Assert.True(agentNameEl.GetString()!.Length > 0);

        // Raw ids must be preserved.
        Assert.Equal(server.Id.Value, session.GetProperty("mcpServerId").GetString());
        Assert.Equal(agentClientId.Value, session.GetProperty("agentClientId").GetString());
    }

    // ── 025: GET /api/sessions — effectiveStatus derived from participant presence ────────────

    [Fact]
    public async Task Sessions_ActiveWithOnlineNodes_EffectiveStatusIsNotStale()
    {
        var (user, grain, nodeId) = await SeedNodeAsync("sess-live@x.io", "A-SessLive",
            lastSeen: DateTimeOffset.UtcNow); // fresh → Online
        var (server, agentClientId) = await SeedServerAndAgentAsync(grain, user.SpaceId, nodeId, "sess-live-srv");

        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(GrantId.New(), agentClientId, server.Id, nodeId, nodeId,
            GatewayId.New(), new SpaceId(user.SpaceId)); // left Active (not closed)

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var s = await GetSessionForServerAsync(client, server.Id.Value);

        var eff = s.GetProperty("effectiveStatus").GetString();
        Assert.NotEqual("Stale", eff);           // both nodes online → live
        Assert.Equal(s.GetProperty("status").GetString(), eff); // mirrors raw status
    }

    [Fact]
    public async Task Sessions_ActiveButPublisherStale_EffectiveStatusIsStale()
    {
        // Publisher node Online in stored Status but heartbeat is stale (> 90s) → presence Offline.
        var stale = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromMinutes(5);
        var (user, grain, nodeId) = await SeedNodeAsync("sess-ghost@x.io", "A-SessGhost", lastSeen: stale);
        var (server, agentClientId) = await SeedServerAndAgentAsync(grain, user.SpaceId, nodeId, "sess-ghost-srv");

        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(GrantId.New(), agentClientId, server.Id, nodeId, nodeId,
            GatewayId.New(), new SpaceId(user.SpaceId)); // Active, but the node is stale

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var s = await GetSessionForServerAsync(client, server.Id.Value);

        Assert.Equal("Stale", s.GetProperty("effectiveStatus").GetString());
        Assert.Equal("Active", s.GetProperty("status").GetString()); // raw status unchanged (no write-back)
    }

    [Fact]
    public async Task Sessions_ActiveButClientNodeStale_EffectiveStatusIsStale()
    {
        // Distinct client + publisher nodes: publisher fresh (online), CLIENT (agent) stale → Stale.
        // Exercises the client side of the `&&` independently of the publisher.
        var user = await fixture.SeedUserAsync("sess-client-ghost@x.io", "A-SessClientGhost");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var now = DateTimeOffset.UtcNow;
        var staleAt = now - NodePresenceRules.StaleThreshold - TimeSpan.FromMinutes(5);

        var publisherNode = NodeId.New();
        var clientNode = NodeId.New();
        await grain.RegisterNodeAsync(MakeNode(user.SpaceId, publisherNode, "pub-fresh", now));
        await grain.RegisterNodeAsync(MakeNode(user.SpaceId, clientNode, "client-stale", staleAt));

        var server = (await grain.PublishMcpServerAsync(publisherNode, $"sess-cg-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(user.SpaceId), clientNode, "sess-agent");

        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(GrantId.New(), agentClientId, server.Id, clientNode, publisherNode,
            GatewayId.New(), new SpaceId(user.SpaceId));

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var s = await GetSessionForServerAsync(client, server.Id.Value);
        Assert.Equal("Stale", s.GetProperty("effectiveStatus").GetString());
    }

    [Fact]
    public async Task Sessions_ActiveHttpCloudWithoutPublisherNode_IsNotStale()
    {
        var user = await fixture.SeedUserAsync("sess-http-cloud@x.io", "A-SessHttpCloud");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var clientNodeId = NodeId.New();
        await grain.RegisterNodeAsync(MakeNode(
            user.SpaceId, clientNodeId, "http-client-fresh", DateTimeOffset.UtcNow));

        var server = await grain.CreateHttpMcpServerAsync(
            $"sess-http-{Guid.NewGuid():N}",
            "https://example.test/mcp",
            McpServerAuthModes.None,
            authHeaderName: null,
            secretHint: null);
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(user.SpaceId), clientNodeId, "http-consumer");

        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(
            GrantId.New(),
            agentClientId,
            server.Id,
            clientNodeId,
            new NodeId(string.Empty),
            GatewayId.New(),
            new SpaceId(user.SpaceId));

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var session = await GetSessionForServerAsync(client, server.Id.Value);

        Assert.Equal("Active", session.GetProperty("effectiveStatus").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("publisherNodeId").ValueKind);
    }

    [Fact]
    public async Task Sessions_ActiveSpaceMcpWithoutSentinelNode_IsNotStale()
    {
        var user = await fixture.SeedUserAsync("sess-space-mcp@x.io", "A-SessSpaceMcp");
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var publisherNodeId = NodeId.New();
        await grain.RegisterNodeAsync(MakeNode(
            user.SpaceId, publisherNodeId, "space-mcp-publisher", DateTimeOffset.UtcNow));

        var server = (await grain.PublishMcpServerAsync(
            publisherNodeId, $"sess-space-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = new ConsumerId($"cagg_{Guid.NewGuid():N}"[..31]);
        var sentinelNodeId = new NodeId(WellKnownNodeIds.AggregatorSentinelNodeId);
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(user.SpaceId), sentinelNodeId, "Connected MCP client");

        var sessionGrain = fixture.ClusterClient.GetGrain<ISessionGrain>(SessionId.New().Value);
        await sessionGrain.OpenAsync(
            GrantId.New(),
            agentClientId,
            server.Id,
            sentinelNodeId,
            publisherNodeId,
            GatewayId.New(),
            new SpaceId(user.SpaceId));

        using var client = await fixture.CreateAuthenticatedClientAsync(user.UserId);
        var session = await GetSessionForServerAsync(client, server.Id.Value);

        Assert.Equal("Active", session.GetProperty("effectiveStatus").GetString());
        Assert.Equal("Connected MCP client", session.GetProperty("agentName").GetString());
    }

    private static Domain.Entities.Node MakeNode(string spaceId, NodeId id, string name, DateTimeOffset lastSeen) => new()
    {
        Id = id,
        SpaceId = new SpaceId(spaceId),
        DisplayName = name,
        Status = NodeStatus.Online,
        LastSeenAt = lastSeen,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<(KoratIntegrationFixture.SeededUser User, ISpaceGrain Grain, NodeId NodeId)> SeedNodeAsync(
        string email, string name, DateTimeOffset lastSeen)
    {
        var user = await fixture.SeedUserAsync(email, name);
        var grain = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var nodeId = NodeId.New();
        await grain.RegisterNodeAsync(new Domain.Entities.Node
        {
            Id = nodeId,
            SpaceId = new SpaceId(user.SpaceId),
            DisplayName = $"node-{name}",
            Status = NodeStatus.Online,
            LastSeenAt = lastSeen,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return (user, grain, nodeId);
    }

    private async Task<(Domain.Entities.McpServer Server, ConsumerId ConsumerId)> SeedServerAndAgentAsync(
        ISpaceGrain grain, string spaceId, NodeId nodeId, string serverName)
    {
        var server = (await grain.PublishMcpServerAsync(nodeId, $"{serverName}-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(spaceId), nodeId, "sess-agent");
        return (server, agentClientId);
    }

    private static async Task<JsonElement> GetSessionForServerAsync(HttpClient client, string serverId)
    {
        var resp = await client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);
        var s = doc!.RootElement.EnumerateArray()
            .FirstOrDefault(x => x.GetProperty("mcpServerId").GetString() == serverId);
        Assert.True(s.ValueKind != JsonValueKind.Undefined, "Session for the seeded server was not returned.");
        return s.Clone();
    }
}
