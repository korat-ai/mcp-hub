using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Gateways;
using Korat.Cloud.Gateways.Admission;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1), Task 9 — the increment's final green bar: a single comprehensive
/// end-to-end test that drives the WHOLE feature through the REAL gRPC relay with the exact
/// client shape Cursor/Codex/Claude use in an <c>mcp.json</c> entry: a raw <see cref="HttpClient"/>
/// (no cookie, no <c>KoratIntegrationFixture.CreateAuthenticatedClientAsync</c>) carrying a manual
/// <c>Authorization: Bearer &lt;space-mcp-token&gt;</c> header on every request, against a real
/// gRPC-connected <see cref="FakeMcpPublisher"/> backend granted to the aggregator's derived
/// identity.
///
/// Every OTHER SpaceMcp integration test either drives the grain directly (Tasks 4-6:
/// <c>SpaceMcpInitializeToolsTests</c>/<c>SpaceMcpToolCallTests</c>/<c>SpaceMcpTeardownTests</c>),
/// or drives HTTP against a Space with ZERO published servers (Task 7's
/// <c>SpaceMcpResponderTests</c>), or a synthetic revoke path (Task 8's
/// <c>SpaceMcpRevokeTeardownTests</c>). This is the only test that exercises the FULL wire path in
/// one place, purely over HTTP: agent(sentinel) → relay → publisher → relay → delivery-leg →
/// aggregator → HTTP, and back.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpEndToEndRelayTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"cursor","version":"1.0"}}}
        """;

    private const string InitializedNotificationBody = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static readonly TimeSpan CloseSessionWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FullRelayRoundTrip_ManualBearerNoCookie_InitializeToolsCallDeleteAndE2eForcedFalse()
    {
        // ── Arrange: seed owner Space + Published server + grant to the aggregator's derived
        // identity ─────────────────────────────────────────────────────────────────────────────
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-e2e-{Guid.NewGuid():N}@example.com", "Space MCP E2E Relay");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"e2e-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // The ONLY bearer /mcp/{spaceSeg} ever accepts (Global Constraint, S5/Task 1) — a
        // Space-pinned "space-mcp:{spaceId}"-scoped token. This IS the exact Cursor/Codex/Claude
        // mcp.json header shape this test proves end to end: a bare Authorization: Bearer header,
        // never a cookie.
        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (scopedToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f (adversarial review, prior tasks): the fake publisher connects as a real relay NODE
        // over the REAL gRPC relay — a "full"-scoped token, never the space-mcp-scoped bearer
        // above (node Hello correctly rejects a space-mcp token).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
        publisher.ToolCallHandler = (toolName, args) =>
        {
            Assert.Equal("echo", toolName);
            var text = args?["text"]?.GetValue<string>() ?? "";
            return $$"""{"content":[{"type":"text","text":"{{text}}"}]}""";
        };

        // Raw HttpClient, NO cookie — fixture.Factory.CreateClient() (not
        // CreateAuthenticatedClientAsync) is the manual-bearer, Cursor/Codex/Claude mcp.json shape
        // this whole test exists to prove. A manual Authorization: Bearer header is the ONLY
        // credential on every request below.
        var client = fixture.Factory.CreateClient();

        // ── Step 1: POST initialize (no session id) ───────────────────────────────────────────
        var initRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody);
        initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var initResponse = await client.SendAsync(initRequest);

        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        Assert.Equal("application/json", initResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(initResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIdValues),
            "Expected a Mcp-Session-Id response header on a successful initialize.");
        var mcpSessionId = Assert.Single(sessionIdValues);

        var initEnvelope = JsonNode.Parse(await initResponse.Content.ReadAsStringAsync())!;
        Assert.Equal("2.0", initEnvelope["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, initEnvelope["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", initEnvelope["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("korat-space", initEnvelope["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(initEnvelope["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());

        // ── Step 2: POST notifications/initialized ────────────────────────────────────────────
        var notifRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializedNotificationBody, sessionId: mcpSessionId);
        notifRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var notifResponse = await client.SendAsync(notifRequest);

        Assert.Equal(HttpStatusCode.Accepted, notifResponse.StatusCode);
        Assert.Empty(await notifResponse.Content.ReadAsByteArrayAsync());

        // ── Step 3: POST tools/list — the REAL backend's namespaced tool is present ───────────
        var toolsListRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, sessionId: mcpSessionId);
        toolsListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var toolsListResponse = await client.SendAsync(toolsListRequest);

        Assert.Equal(HttpStatusCode.OK, toolsListResponse.StatusCode);
        var toolsEnvelope = JsonNode.Parse(await toolsListResponse.Content.ReadAsStringAsync())!;
        var toolNames = toolsEnvelope["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();

        var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
        var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "echo");
        Assert.Contains(expectedToolName, toolNames);

        // ── Step 4: POST tools/call — round-trips agent(sentinel) → relay → publisher → relay →
        // delivery-leg → aggregator → HTTP, and the echoed value comes back ───────────────────
        var callRequestBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 42,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = expectedToolName, ["arguments"] = new JsonObject { ["text"] = "hi" } },
        }.ToJsonString();
        var callHttpRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, callRequestBody, sessionId: mcpSessionId);
        callHttpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var callResponse = await client.SendAsync(callHttpRequest);

        Assert.Equal(HttpStatusCode.OK, callResponse.StatusCode);
        var callEnvelope = JsonNode.Parse(await callResponse.Content.ReadAsStringAsync())!;
        Assert.Equal(42, callEnvelope["id"]!.GetValue<int>());
        Assert.Null(callEnvelope["error"]);
        var echoedText = callEnvelope["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("hi", echoedText);

        // ── Step 5: DELETE — the backend's relay session is ACTUALLY torn down (not just the
        // aggregator's own local grain state) ─────────────────────────────────────────────────
        var relaySessionId = Assert.Single(publisher.SeenSessionIds);

        var deleteRequest = BuildRequest(HttpMethod.Delete, seeded.SpaceId, sessionId: mcpSessionId);
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var deleteResponse = await client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var receivedClose = await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait);
        Assert.True(receivedClose,
            "Expected the publisher to receive a real CloseSession control frame for the backend " +
            "relay session after DELETE — the whole point of driving this through the REAL relay " +
            "is proving teardown reaches the publisher, not just the aggregator's own grain state.");

        var subsequentPostRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, sessionId: mcpSessionId);
        subsequentPostRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var subsequentPostResponse = await client.SendAsync(subsequentPostRequest);
        Assert.Equal(HttpStatusCode.NotFound, subsequentPostResponse.StatusCode);

        // ── Step 6 (SF-8): peer_supports_e2e is forced false for every ServerMinted
        // (aggregator-opened) backend session — the cloud is the plaintext terminus. There is no
        // persisted flag on RelaySession/SessionGrain (or anywhere ISpaceMcpAggregatorGrain exposes) to
        // read back after the fact, so this re-derives the EXACT ConsumerPrincipal
        // SpaceMcpAggregatorGrain.OpenBackendAsync constructed for this session's own backend open
        // (same consumerIdentity, same AggregatorSentinelNodeId, same ServerMinted policy — only
        // the synthetic ConnectionId is a fresh, disjoint one so this ad-hoc admission can never
        // collide with the real grain's own delivery-leg registration) and calls the SAME
        // production ISessionAdmission the grain used (resolved from the web host container,
        // exactly as NodeGatewayService itself does — see KoratTestHost's Program.cs registration),
        // observing the AdmissionResult.Opened record directly. This mints a second real relay
        // session against the SAME granted server/publisher — terminated immediately below so
        // nothing outlives this assertion.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var admission = scope.ServiceProvider.GetRequiredService<ISessionAdmission>();
            var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();
            var principal = new ConsumerPrincipal(
                consumerIdentity,
                spaceId,
                SpaceMcpConsumerIdentity.SyntheticConnectionId(Guid.NewGuid().ToString("N")),
                SessionAdmission.AggregatorSentinelNodeId,
                null,
                ConsumerBindPolicy.ServerMinted);

            var result = await admission.AdmitAsync(server.Id, principal, default);
            var opened = Assert.IsType<AdmissionResult.Opened>(result);
            Assert.False(opened.PeerSupportsE2e,
                "SF-8: a ServerMinted (aggregator-opened) backend session must always force " +
                "peer_supports_e2e=false — the cloud is the plaintext terminus.");

            await terminator.TerminateSessionAsync(opened.SessionId, SessionCloseReason.Completed, default);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string spaceSeg, string? body = null, string? sessionId = null)
    {
        var request = new HttpRequestMessage(method, $"/mcp/{spaceSeg}");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        // Deliberately no Origin header — Cursor/Codex/Claude MCP clients send none (S3).

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
