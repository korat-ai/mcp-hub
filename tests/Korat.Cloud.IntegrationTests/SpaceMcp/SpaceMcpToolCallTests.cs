using System.Text.Json.Nodes;
using Korat.Cloud.Gateways;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1), Task 6: <c>SpaceMcpAggregatorGrain.DispatchAsync</c>'s
/// <c>tools/call</c> routing — a granted backend's namespaced tool routes through and the
/// backend's response is reframed under the EXTERNAL client's own id; a
/// <c>request-access__&lt;slug&gt;</c> call against an ungranted server creates an access
/// request (idempotent on a second call — N1 catches the already-granted race); an unknown
/// tool name is <c>-32601</c>.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpToolCallTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string ClientInitializeJson = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    [Fact]
    public async Task ToolsCall_GrantedTool_RoutesToBackendAndReframesUnderClientId()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-call-{Guid.NewGuid():N}@example.com", "Space MCP Call");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"call-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f (adversarial review): the fake publisher connects as a real relay node — a
        // "full"-scoped token, never the space-mcp-scoped `cliToken` (which node Hello now
        // correctly rejects).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
        publisher.ToolCallHandler = (toolName, args) =>
        {
            Assert.Equal("echo", toolName);
            var text = args?["text"]?.GetValue<string>() ?? "";
            return $$"""{"content":[{"type":"text","text":"echo:{{text}}"}]}""";
        };

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
        await grain.InitializeAsync(ctx, ClientInitializeJson);

        var namespacedName = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");

        // Use a distinctive, non-sequential external id to prove reframing uses THIS id, not the
        // backend's own private id space (which starts at 1 for every SpaceBackendSession).
        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 999,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = namespacedName, ["arguments"] = new JsonObject { ["text"] = "hi" } },
        }.ToJsonString();

        var responseJson = await grain.DispatchAsync(callRequest);
        Assert.NotNull(responseJson);
        var response = JsonNode.Parse(responseJson!)!;

        Assert.Equal(999, response["id"]!.GetValue<int>());
        Assert.True(response["error"] is null, response.ToJsonString());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("echo:hi", text);
    }

    [Fact]
    public async Task ToolsCall_UndeliveredFrame_ReopensAndRetriesExactlyOnce()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-undelivered-{Guid.NewGuid():N}@example.com", "Space MCP Undelivered");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"undelivered-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
        var executionCount = 0;
        publisher.ToolCallHandler = (_, _) =>
        {
            Interlocked.Increment(ref executionCount);
            return """{"content":[{"type":"text","text":"reopened"}]}""";
        };

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        await grain.InitializeAsync(
            new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId),
            ClientInitializeJson);

        var oldRelaySessionId = Assert.Single(publisher.SeenSessionIds);

        // Remove both the live route and its persisted fallback without notifying the aggregator.
        // It still considers this backend alive, so its first tools/call send must fail with the
        // explicit "not delivered" signal before opening a fresh session and retrying safely.
        await fixture.ClusterClient.GetGrain<ISessionGrain>(oldRelaySessionId)
            .CloseAsync(SessionCloseReason.Completed);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SessionRoutingTable>()
                .CloseSession(new SessionId(oldRelaySessionId));
        }

        var namespacedName = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");
        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 77,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = namespacedName, ["arguments"] = new JsonObject() },
        }.ToJsonString();

        var response = JsonNode.Parse((await grain.DispatchAsync(callRequest))!)!;

        Assert.True(response["error"] is null,
            $"{response.ToJsonString()} seen=[{string.Join(',', publisher.SeenSessionIds)}] executions={executionCount}");
        Assert.Equal("reopened", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, executionCount);
        Assert.Contains(publisher.SeenSessionIds, id => id != oldRelaySessionId);
    }

    [Fact]
    public async Task ToolsCall_CachedRouteAfterGrantRevoked_DoesNotReopenOrExecute()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-stale-grant-{Guid.NewGuid():N}@example.com", "Space MCP Stale Grant");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"stale-grant-srv-{Guid.NewGuid():N}", "echo", "demo"))!;
        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        var grant = await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
        var executionCount = 0;
        publisher.ToolCallHandler = (_, _) =>
        {
            Interlocked.Increment(ref executionCount);
            return """{"content":[{"type":"text","text":"must-not-run"}]}""";
        };

        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>($"test-session-{Guid.NewGuid():N}");
        await grain.InitializeAsync(
            new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId),
            ClientInitializeJson);

        // Kill only the relay backend so its cached route remains, then revoke authorization
        // before the next call. Lazy reopen must rediscover the missing grant and stop there.
        var oldRelaySessionId = Assert.Single(publisher.SeenSessionIds);
        await publisher.SendRawFrameAsync(oldRelaySessionId, payload: [0x01], enc: 1);
        Assert.True(await publisher.WaitForCloseSessionAsync(oldRelaySessionId, TimeSpan.FromSeconds(10)));
        await space.RevokeGrantAsync(grant.Id, seeded.UserId);

        var namespacedName = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");
        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 88,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = namespacedName, ["arguments"] = new JsonObject() },
        }.ToJsonString();

        var response = JsonNode.Parse((await grain.DispatchAsync(callRequest))!)!;

        Assert.Equal(-32000, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, executionCount);
        Assert.Single(publisher.SeenSessionIds);
    }

    [Fact]
    public async Task ToolsCall_RequestAccessStub_CreatesAccessRequest_AndIsIdempotentOnSecondCall()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-reqaccess-{Guid.NewGuid():N}@example.com", "Space MCP Request Access");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        // Ungranted Published server — no publisher needs to connect; the aggregator never
        // opens a backend session for it, only a request-access__<slug> catalog stub.
        var ungrantedServer = (await space.PublishMcpServerAsync(
            NodeId.New(), $"reqaccess-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
        await grain.InitializeAsync(ctx, ClientInitializeJson);

        var requestAccessToolName = ToolNamespacer.RequestAccessTool(
            ToolNamespacer.Slug(ungrantedServer.DisplayName, ungrantedServer.Id.Value));

        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = requestAccessToolName, ["arguments"] = new JsonObject() },
        }.ToJsonString();

        var firstResponseJson = await grain.DispatchAsync(callRequest);
        var firstResponse = JsonNode.Parse(firstResponseJson!)!;
        Assert.Null(firstResponse["error"]);
        var firstText = firstResponse["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Access request created", firstText);

        // Verify a REAL access request now exists (not just an ack).
        var pendingRequests = await space.ListAccessRequestsAsync();
        Assert.Contains(pendingRequests, ar => ar.McpServerId == ungrantedServer.Id && ar.ConsumerId == consumerIdentity);

        // N1: approve the grant "out of band" — mirrors the race between the tools/list snapshot
        // and a second tools/call after the owner already approved it. The stub must catch
        // AccessDenied (already-active-grant) and return a normal tool result, not a 500.
        var approved = pendingRequests.First(ar => ar.McpServerId == ungrantedServer.Id && ar.ConsumerId == consumerIdentity);
        await space.ApproveAccessRequestAsync(approved.Id, seeded.UserId);

        var secondResponseJson = await grain.DispatchAsync(callRequest);
        var secondResponse = JsonNode.Parse(secondResponseJson!)!;
        Assert.Null(secondResponse["error"]); // N1: never a raw error/500-shape for the already-granted race.
        var secondText = secondResponse["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("already granted", secondText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolsCall_UnknownToolName_ReturnsMethodNotFound()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-unknown-{Guid.NewGuid():N}@example.com", "Space MCP Unknown Tool");
        var spaceId = new SpaceId(seeded.SpaceId);

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
        await grain.InitializeAsync(ctx, ClientInitializeJson);

        var callRequest = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"does_not_exist","arguments":{}}}""";
        var responseJson = await grain.DispatchAsync(callRequest);
        var response = JsonNode.Parse(responseJson!)!;

        Assert.Equal(5, response["id"]!.GetValue<int>());
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
    }

    private async Task<Guid> GetTokenIdAsync(string rawToken)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var id = await cliTokens.GetTokenIdAsync(rawToken, default);
        Assert.NotNull(id);
        return id!.Value;
    }
}
