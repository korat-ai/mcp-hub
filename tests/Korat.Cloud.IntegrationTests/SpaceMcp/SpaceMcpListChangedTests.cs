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
/// Space-MCP (increment 1), Task 8 — the GET-SSE <c>list_changed</c> watch's APPROVAL side: a
/// session initialized against one UNGRANTED Published server (only a
/// <c>request-access__&lt;slug&gt;</c> catalog stub visible) later has that server's access
/// request approved out of band (mirrors the console's own approve action). Since an approval
/// sends no frame of its own — unlike revoke/close, which reach the aggregator synchronously —
/// this can ONLY be observed by <see cref="SpaceMcpAggregatorGrain"/>'s backstop reconcile timer
/// (<c>ReconcileAsync</c>), which re-runs <see cref="SpaceServerDiscovery.DiscoverAsync"/> on
/// <see cref="SpaceMcpAggregatorGrain.ReconcileInterval"/>, opens the newly-granted backend, and
/// bumps the <c>list_changed</c> cursor. Asserts the FULL round trip: a GET-SSE stream already
/// blocked in <see cref="ISpaceMcpAggregatorGrain.NextListChangedAsync"/>'s long-poll wakes and
/// emits <c>notifications/tools/list_changed</c>, and a subsequent <c>tools/list</c> now includes
/// the newly-granted server's namespaced tools.
///
/// <see cref="SpaceMcpAggregatorGrain.ReconcileInterval"/> and
/// <see cref="SpaceMcpAggregatorGrain.ListChangedHeartbeat"/> are shrunk for the duration of each
/// test (mirrors <c>SpaceMcpTeardownTests</c>' own <c>PerBackendTimeout</c> test-shrink precedent
/// — mutable internal statics, restored in a <c>finally</c> block) so this doesn't wait real
/// production-sized seconds per run.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpListChangedTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static readonly TimeSpan SseWaitTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ApprovedGrant_ReconcileTimerOpensBackend_SseEmitsListChanged_AndToolsListIncludesNewTool()
    {
        var originalReconcileInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromSeconds(1);
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromSeconds(2);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-listchanged-{Guid.NewGuid():N}@example.com", "Space MCP ListChanged");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

            // Published but NOT yet granted — InitializeAsync will only see a request-access
            // stub for it (mirrors ToolsCall_RequestAccessStub_... in SpaceMcpToolCallTests).
            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"listchanged-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

            // Bring the publisher node Online BEFORE the grant lands, so the reconcile timer's
            // open (once approved) never needs to wait on node-wake — mirrors every other
            // SpaceMcp test's publisher-connects-first ordering.
            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (token, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

            var client = fixture.Factory.CreateClient();
            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);

            // Sanity: before approval, tools/list has no "echo"-shaped tool for this server yet
            // (only the request-access stub the ungranted-discovery path registers).
            var initialTools = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
            var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "echo");
            Assert.DoesNotContain(expectedToolName, initialTools);

            // Open the GET-SSE stream FIRST (it will be blocked inside NextListChangedAsync's
            // long-poll) so the approval below wakes an ALREADY-WAITING caller, not one that
            // merely polls in afterward.
            var sseTask = WaitForListChangedEventAsync(seeded.SpaceId, token, sessionId, SseWaitTimeout);
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // let the GET request actually reach the grain's long-poll.

            // ── Act: approve the grant out of band (mirrors the console's own approve action —
            // SpaceGrain.ApproveAccessRequestAsync, the same grain call the console uses) ────────
            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            // ── Assert (1): the GET-SSE stream emits notifications/tools/list_changed ──────────
            var sseFired = await sseTask;
            Assert.True(sseFired,
                "Expected the GET-SSE stream to emit notifications/tools/list_changed once the " +
                "backstop reconcile timer observed the newly-approved grant and opened the backend.");

            // ── Assert (2): tools/list now includes the newly-granted server's namespaced tool ──
            var toolNamesAfter = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Contains(expectedToolName, toolNamesAfter);
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalReconcileInterval;
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static async Task<string> InitializeSessionAsync(HttpClient client, string spaceSeg, string token)
    {
        var request = BuildRequest(HttpMethod.Post, spaceSeg, InitializeBody);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        return Assert.Single(values);
    }

    private static async Task<List<string>> ListToolNamesAsync(
        HttpClient client, string spaceSeg, string token, string sessionId)
    {
        var request = BuildRequest(HttpMethod.Post, spaceSeg, ToolsListBody, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
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

    /// <summary>Opens a GET-SSE stream against <c>/mcp/{spaceSeg}</c> and waits (bounded by
    /// <paramref name="timeout"/>) for a line containing
    /// <c>notifications/tools/list_changed</c>. Returns false on timeout or an early stream
    /// close — never throws.</summary>
    private async Task<bool> WaitForListChangedEventAsync(
        string spaceSeg, string token, string sessionId, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/mcp/{spaceSeg}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var client = fixture.Factory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                    return false; // stream closed before the expected event arrived.
                if (line.Contains("notifications/tools/list_changed", StringComparison.Ordinal))
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
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
