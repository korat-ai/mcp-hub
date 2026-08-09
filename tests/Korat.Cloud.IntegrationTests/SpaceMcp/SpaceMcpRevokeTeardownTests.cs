using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// Space-MCP (increment 1), Task 8 — the GET-SSE <c>list_changed</c> watch's REVOCATION side
/// (SF-6): a session with one GRANTED live backend has its grant revoked via the real
/// <c>SpaceGrain.RevokeGrantAsync</c> + <c>SessionTerminator.TerminateSessionAsync</c> path — the
/// exact chain <c>Endpoints.cs</c>'s revoke endpoint drives (mirrors
/// <c>SessionTeardownOnRevokeTests.RevokeEndpoint_path_closes_the_live_session</c>'s own
/// "replicate what the endpoint body does" precedent).
///
/// The revoke reaches <see cref="SpaceMcpAggregatorGrain.OnDeliveryAsync"/> SYNCHRONOUSLY: the
/// aggregator's synthetic <c>ConnectionId</c> is recorded as the session's own
/// <c>AgentConnectionId</c> at admission time (<c>SessionAdmission.AdmitAsync</c> passes
/// <c>principal.AgentConnectionId</c> straight into <c>SessionGrain.OpenAsync</c>), so
/// <c>SessionTerminator</c>'s <c>SendToConnectionAsync(session.AgentConnectionId, CloseSession)</c>
/// call delivers directly to the <see cref="Korat.Cloud.Gateways.CallbackServerStreamWriter"/>
/// registered for that ConnectionId — which calls
/// <see cref="ISpaceMcpAggregatorGrain.OnDeliveryAsync"/> on THIS grain, on its own scheduler turn,
/// with no timer/poll latency in between. Asserts all three points from the plan: the backend
/// faults + its tool disappears from <c>tools/list</c>, an in-flight/subsequent <c>tools/call</c>
/// against it fails fast ("Server unavailable"), and a GET-SSE stream emits
/// <c>notifications/tools/list_changed</c>.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpRevokeTeardownTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static readonly TimeSpan SseWaitTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task RevokeGrant_FaultsBackend_EvictsToolFromList_FailsFastToolCall_AndSseEmitsListChanged()
    {
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromSeconds(2);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-revoke-{Guid.NewGuid():N}@example.com", "Space MCP Revoke Teardown");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"revoke-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (token, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

            // Grant BEFORE initialize — the aggregator's own fan-out opens the backend at
            // InitializeAsync time (mirrors SpaceMcpInitializeToolsTests/SpaceMcpToolCallTests).
            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            var grant = await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            // N-f: the fake publisher connects as a real relay node — a "full"-scoped token,
            // never the space-mcp-scoped `token` (which node Hello correctly rejects).
            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);

            var client = fixture.Factory.CreateClient();
            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);

            var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
            var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "echo");

            // Sanity: the granted tool is visible before revoke.
            var toolsBefore = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.Contains(expectedToolName, toolsBefore);

            // Open the GET-SSE stream FIRST so it is already blocked inside
            // NextListChangedAsync's long-poll when the revoke lands — proving the SYNCHRONOUS
            // wake path, not a subsequent poll catching up.
            var sseTask = WaitForListChangedEventAsync(seeded.SpaceId, token, sessionId, SseWaitTimeout);
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // let the GET request reach the grain's long-poll.

            // ── Act: revoke via the REAL grant-revoke + terminator path (mirrors the revoke
            // endpoint body — Endpoints.cs:1131 -> RevokeGrantAsync -> TerminateSessionAsync) ────
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();
                var affected = await space.RevokeGrantAsync(grant.Id, seeded.UserId);
                foreach (var sid in affected)
                    await terminator.TerminateSessionAsync(sid, SessionCloseReason.Revoked, default);
            }

            // ── Assert (1): the GET-SSE stream emits notifications/tools/list_changed ──────────
            var sseFired = await sseTask;
            Assert.True(sseFired,
                "Expected the GET-SSE stream to emit notifications/tools/list_changed once the " +
                "revoke's synchronous CloseSession reached OnDeliveryAsync.");

            // ── Assert (2): the tool is evicted from tools/list ────────────────────────────────
            var toolsAfter = await ListToolNamesAsync(client, seeded.SpaceId, token, sessionId);
            Assert.DoesNotContain(expectedToolName, toolsAfter);

            // ── Assert (3): a subsequent tools/call against the revoked backend fails fast ──────
            var callRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = expectedToolName, ["arguments"] = new JsonObject { ["text"] = "hi" } },
            }.ToJsonString();
            var callRequestMessage = BuildRequest(HttpMethod.Post, seeded.SpaceId, callRequest, sessionId: sessionId);
            callRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var callResponse = await client.SendAsync(callRequestMessage);
            Assert.Equal(HttpStatusCode.OK, callResponse.StatusCode);
            var callEnvelope = JsonNode.Parse(await callResponse.Content.ReadAsStringAsync())!;
            // The tool is unknown now (evicted from the catalog) — the grain's own DispatchAsync
            // returns -32601 for an unresolved name; if a race meant it was still routable at the
            // instant of the call, HandleToolRouteAsync's own dead-backend guard returns -32000
            // "Server unavailable." Either way this must never silently succeed.
            var errorCode = callEnvelope["error"]?["code"]?.GetValue<int>();
            Assert.True(errorCode is -32601 or -32000,
                $"Expected the post-revoke tools/call to fail fast (-32601 unknown tool or -32000 " +
                $"server unavailable), got: {callEnvelope.ToJsonString()}");
        }
        finally
        {
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

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
