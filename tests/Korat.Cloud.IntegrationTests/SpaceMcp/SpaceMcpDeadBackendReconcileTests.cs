using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Regression coverage for a temporarily unavailable granted backend. Availability is session
/// state, not catalog state: losing a relay session must keep the last known tool definitions so
/// an agent can still select the tool. The first <c>tools/call</c> then re-enters the shared
/// admission path, opens a fresh relay session, and executes the call without waiting for the
/// periodic reconcile timer.
///
/// This test feeds a granted, live backend a single raw relay frame with a nonzero <c>Enc</c> —
/// simulating the fail-closed guard without needing a real cipher (Space-MCP backend sessions are
/// always forced plaintext) — and asserts the full lazy-healing round trip: the old relay session
/// is terminated, the tool stays visible, and the next call opens a fresh session and succeeds.
///
/// <see cref="SpaceMcpAggregatorGrain.ReconcileInterval"/> is made deliberately long for the
/// duration of the test, proving the tool call itself performs the reopen.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpDeadBackendReconcileTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static readonly TimeSpan CloseSessionWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DeadBackend_EncNonZeroFrame_PreservesTool_AndToolsCallReopensOnDemand()
    {
        var originalReconcileInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMinutes(5);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-deadbackend-{Guid.NewGuid():N}@example.com", "Space MCP Dead Backend");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"deadbackend-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (token, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
            publisher.ToolCallHandler = (toolName, args) =>
            {
                Assert.Equal("echo", toolName);
                var text = args?["text"]?.GetValue<string>() ?? "";
                return $$"""{"content":[{"type":"text","text":"echo:{{text}}"}]}""";
            };

            var client = fixture.Factory.CreateClient();
            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);

            var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
            var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "echo");

            var toolsBefore = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Contains(expectedToolName, toolsBefore);

            var oldRelaySessionId = Assert.Single(publisher.SeenSessionIds);
            await publisher.SendRawFrameAsync(oldRelaySessionId, payload: [0x01, 0x02, 0x03], enc: 1);

            // Wait for local eviction to finish by observing its best-effort relay teardown.
            var receivedClose = await publisher.WaitForCloseSessionAsync(oldRelaySessionId, CloseSessionWait);
            Assert.True(receivedClose,
                "Expected the publisher to receive a CloseSession control frame for the dead " +
                "backend's old relay session.");

            // Availability does not change the catalog: the agent can still discover/select the
            // tool while its backend relay session is temporarily absent.
            var toolsAfterKill = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Contains(expectedToolName, toolsAfterKill);

            // The next tools/call itself must open a fresh backend and execute the call. The
            // reconcile interval is five minutes, so success here cannot come from the timer.
            var callBody = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = expectedToolName,
                    ["arguments"] = new JsonObject { ["text"] = "hi" },
                },
            }.ToJsonString();
            var callRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, callBody, sessionId);
            callRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var callResponse = await client.SendAsync(callRequest);

            Assert.Equal(HttpStatusCode.OK, callResponse.StatusCode);
            var callEnvelope = JsonNode.Parse(await callResponse.Content.ReadAsStringAsync())!;
            Assert.Null(callEnvelope["error"]);
            Assert.Equal("echo:hi", callEnvelope["result"]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Contains(publisher.SeenSessionIds, id => id != oldRelaySessionId);
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalReconcileInterval;
        }
    }

    [Fact]
    public async Task SameNameBackends_KeepDistinctToolNames_WhenReopenedInReverseOrder()
    {
        var originalReconcileInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMinutes(5);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-slug-reservation-{Guid.NewGuid():N}@example.com", "Space MCP Slug Reservation");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
            var publisherNodeId = NodeId.New().Value;
            var servers = new[]
            {
                (await space.PublishMcpServerAsync(
                    new NodeId(publisherNodeId), $"same-name-a-{Guid.NewGuid():N}", "Same Name", "demo"))!,
                (await space.PublishMcpServerAsync(
                    new NodeId(publisherNodeId), $"same-name-b-{Guid.NewGuid():N}", "Same Name", "demo"))!,
            };

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (token, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
            foreach (var server in servers)
            {
                var request = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
                await space.ApproveAccessRequestAsync(request.Id, seeded.UserId);
            }

            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echo", null)]);

            var client = fixture.Factory.CreateClient();
            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);
            var initialToolNames = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Equal(2, initialToolNames.Count);
            Assert.Equal(2, initialToolNames.Distinct(StringComparer.Ordinal).Count());

            var oldRelaySessionIds = publisher.SeenSessionIds.ToList();
            Assert.Equal(2, oldRelaySessionIds.Count);
            foreach (var relaySessionId in oldRelaySessionIds)
                await publisher.SendRawFrameAsync(relaySessionId, payload: [0x01], enc: 1);
            foreach (var relaySessionId in oldRelaySessionIds)
                Assert.True(await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait));

            foreach (var toolName in initialToolNames.AsEnumerable().Reverse())
            {
                var body = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = toolName,
                        ["arguments"] = new JsonObject(),
                    },
                }.ToJsonString();
                var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, body, sessionId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Assert.True(envelope["error"] is null, envelope.ToJsonString());
            }

            var finalToolNames = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Equal(
                initialToolNames.Order(StringComparer.Ordinal),
                finalToolNames.Order(StringComparer.Ordinal));
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalReconcileInterval;
        }
    }

    [Fact]
    public async Task PublisherDisconnect_PreservesCatalog_AndReconnectAllowsLazyToolCall()
    {
        var originalReconcileInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMinutes(5);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-publisher-reconnect-{Guid.NewGuid():N}@example.com", "Space MCP Publisher Reconnect");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"publisher-reconnect-{Guid.NewGuid():N}", "Reconnect", "demo"))!;

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (token, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echo", null)]);

            var client = fixture.Factory.CreateClient();
            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);
            var toolName = Assert.Single(
                await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId));

            await publisher.DisposeAsync();

            var publisherWentOffline = await WaitUntilAsync(async () =>
            {
                var node = await fixture.ClusterClient.GetGrain<INodeGrain>(publisherNodeId).GetAsync();
                return node.Status == NodeStatus.Offline;
            }, TimeSpan.FromSeconds(10));
            Assert.True(publisherWentOffline, "Publisher teardown did not complete before reconnect.");

            // Reconnect the same publisher node. The prior stream teardown must already have sent
            // CloseSession to the aggregator; the catalog stays, but the old relay backend is gone.
            await using var reconnectedPublisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory,
                publisherNodeId,
                publisherCliToken,
                tools: [("echo", "Echo", null), ("new_tool", "Added while offline", null)]);

            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 4,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = new JsonObject(),
                },
            }.ToJsonString();
            var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, body, sessionId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.True(envelope["error"] is null, envelope.ToJsonString());
            Assert.NotEmpty(reconnectedPublisher.SeenSessionIds);
            var refreshedTools = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Contains(toolName, refreshedTools);
            Assert.Contains(refreshedTools, name => name.EndsWith("__new_tool", StringComparison.Ordinal));
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalReconcileInterval;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (await predicate())
                return true;
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static async Task<string> InitializeSessionAsync(HttpClient client, string spaceSeg, string token)
    {
        var request = BuildRequest(HttpMethod.Post, spaceSeg, InitializeBody);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        return Assert.Single(values);
    }

    private static async Task<List<string>> ListToolNamesAsync(
        HttpClient client, string spaceSeg, string token, string sessionId)
    {
        var request = BuildRequest(HttpMethod.Post, spaceSeg, ToolsListBody, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return envelope["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string spaceSeg, string? body = null, string? sessionId = null)
    {
        var request = new HttpRequestMessage(method, $"/mcp/{spaceSeg}");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        return request;
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
