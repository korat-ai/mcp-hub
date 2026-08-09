using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1), Task 4: <c>SpaceMcpAggregatorGrain.InitializeAsync</c> opens a
/// granted backend via the shared admission gauntlet + in-process delivery leg, and
/// <c>DispatchAsync("tools/list")</c> returns that backend's tools namespaced <c>slug__tool</c>.
///
/// Drives the grain DIRECTLY (not through the Task 7 HTTP responder, which doesn't exist yet) —
/// mirrors how <c>SessionAdmissionCharacterizationTests</c>/<c>ConnectAccessRequestTests</c>
/// exercise their subjects at the grain/gRPC layer without an HTTP surface.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpInitializeToolsTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string ClientInitializeJson = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    [Fact]
    public async Task Initialize_GrantedServer_ReturnsNamespacedTools()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-init-{Guid.NewGuid():N}@example.com", "Space MCP Init");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"init-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        // Grant the aggregator's own durable identity access to the server — mirrors
        // RelayFrameForwardingTests' in-process create+approve (bypasses the HTTP approval
        // endpoint; the ConsumerGrain TOFU/ServerMinted bind happens lazily inside
        // SessionAdmission.AdmitAsync the first time the aggregator actually opens the session).
        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f (adversarial review): the space-mcp-scoped `cliToken` above is now correctly
        // REJECTED at node Hello — it must never also work as a relay-node credential. The fake
        // publisher connects as a real relay node, so it needs its own separately-issued
        // "full"-scoped token, exactly like a real publisher would.
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken,
            tools: [("echo", "Echoes input back", null)]);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);

        var initResultJson = await grain.InitializeAsync(ctx, ClientInitializeJson);
        var initResult = JsonNode.Parse(initResultJson)!;

        Assert.Equal("korat-space", initResult["serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(initResult["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
        // N4: echoes the client's own requested protocolVersion rather than hard-pinning one.
        Assert.Equal("2025-06-18", initResult["protocolVersion"]!.GetValue<string>());

        var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
        var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "echo");

        const string toolsListRequest = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
        var toolsResponseJson = await grain.DispatchAsync(toolsListRequest);
        Assert.NotNull(toolsResponseJson);
        var toolsResponse = JsonNode.Parse(toolsResponseJson!)!;
        var toolNames = toolsResponse["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains(expectedToolName, toolNames);
        Assert.Single(toolNames); // no other tools — nothing ungranted/unexpected leaks in.

        var binding = await grain.GetBindingAsync();
        Assert.NotNull(binding);
        Assert.Equal(consumerIdentity.Value, binding!.ConsumerId);
        Assert.Equal(spaceId.Value, binding.SpaceId);
    }

    /// <summary>
    /// Task 5: a granted server's tools appear namespaced AND an ungranted (but Published)
    /// server gets a synthetic <c>request-access__&lt;slug&gt;</c> stub — no CLI-only synthetic
    /// tools (submit_feedback/update_korat, B2/Task 4) ever appear.
    /// </summary>
    [Fact]
    public async Task Initialize_GrantedAndUngrantedServers_ToolsListIncludesBothAndExcludesCliOnlyTools()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-ungranted-{Guid.NewGuid():N}@example.com", "Space MCP Ungranted");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var grantedPublisherNodeId = NodeId.New().Value;
        var grantedServer = (await space.PublishMcpServerAsync(
            new NodeId(grantedPublisherNodeId), $"granted-srv-{Guid.NewGuid():N}", "echo", "demo"))!;
        // Ungranted server: Published (default status), never approved — no publisher needs to
        // actually connect for it since the aggregator never opens a backend session for it.
        var ungrantedServer = (await space.PublishMcpServerAsync(
            NodeId.New(), $"ungranted-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, grantedServer.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f: the fake publisher connects as a real relay node — a "full"-scoped token, never
        // the space-mcp-scoped `cliToken` (which node Hello now correctly rejects).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, grantedPublisherNodeId, publisherCliToken,
            tools: [("echo", "Echoes input back", null)]);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
        await grain.InitializeAsync(ctx, ClientInitializeJson);

        const string toolsListRequest = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
        var toolsResponseJson = await grain.DispatchAsync(toolsListRequest);
        var toolNames = JsonNode.Parse(toolsResponseJson!)!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();

        var grantedToolName = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(grantedServer.DisplayName, grantedServer.Id.Value), "echo");
        var ungrantedSlug = ToolNamespacer.Slug(ungrantedServer.DisplayName, ungrantedServer.Id.Value);
        var requestAccessToolName = ToolNamespacer.RequestAccessTool(ungrantedSlug);

        Assert.Contains(grantedToolName, toolNames);
        Assert.Contains(requestAccessToolName, toolNames);
        Assert.DoesNotContain("submit_feedback", toolNames);
        Assert.DoesNotContain("update_korat", toolNames);
        Assert.Equal(2, toolNames.Count);
    }

    /// <summary>
    /// Task 5 (S9): a granted backend that never answers "tools/list" does not stall the
    /// aggregate — its tools are simply absent once PerBackendTimeout elapses, while the OTHER,
    /// well-behaved concurrent backend's tools list normally. Shrinks PerBackendTimeout so this
    /// doesn't burn the real 40s default per run (mirrors SessionAdmissionCharacterizationTests'
    /// own wakeWaitSeconds-shrinking precedent).
    /// </summary>
    [Fact]
    public async Task Initialize_HungBackend_DoesNotStallTheOtherConcurrentBackend()
    {
        var originalTimeout = SpaceMcpAggregatorGrain.PerBackendTimeout;
        SpaceMcpAggregatorGrain.PerBackendTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-hung-{Guid.NewGuid():N}@example.com", "Space MCP Hung");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

            var fastPublisherNodeId = NodeId.New().Value;
            var fastServer = (await space.PublishMcpServerAsync(
                new NodeId(fastPublisherNodeId), $"fast-srv-{Guid.NewGuid():N}", "echo", "demo"))!;
            var hungPublisherNodeId = NodeId.New().Value;
            var hungServer = (await space.PublishMcpServerAsync(
                new NodeId(hungPublisherNodeId), $"hung-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (cliToken, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

            foreach (var server in new[] { fastServer, hungServer })
            {
                var ar = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
                await space.ApproveAccessRequestAsync(ar.Id, seeded.UserId);
            }

            // N-f: the fake publishers connect as real relay nodes — a "full"-scoped token,
            // never the space-mcp-scoped `cliToken` (which node Hello now correctly rejects).
            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var fastPublisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, fastPublisherNodeId, publisherCliToken, tools: [("echo", "fast", null)]);
            await using var hungPublisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, hungPublisherNodeId, publisherCliToken, tools: [("echo", "hung", null)]);
            hungPublisher.HangOnToolsList = true;

            var sessionKey = $"test-session-{Guid.NewGuid():N}";
            var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
            var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
            await grain.InitializeAsync(ctx, ClientInitializeJson);

            const string toolsListRequest = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
            var toolsResponseJson = await grain.DispatchAsync(toolsListRequest);
            var toolNames = JsonNode.Parse(toolsResponseJson!)!["result"]!["tools"]!.AsArray()
                .Select(t => t!["name"]!.GetValue<string>())
                .ToList();

            var fastToolName = ToolNamespacer.Namespaced(
                ToolNamespacer.Slug(fastServer.DisplayName, fastServer.Id.Value), "echo");
            var hungToolName = ToolNamespacer.Namespaced(
                ToolNamespacer.Slug(hungServer.DisplayName, hungServer.Id.Value), "echo");

            Assert.Contains(fastToolName, toolNames);
            Assert.DoesNotContain(hungToolName, toolNames);
        }
        finally
        {
            SpaceMcpAggregatorGrain.PerBackendTimeout = originalTimeout;
        }
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
