using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1), Task 7: the real Streamable-HTTP responder —
/// <c>POST/GET/DELETE /mcp/{spaceSeg}</c> — driven purely over HTTP (no grain-direct calls,
/// unlike <c>SpaceMcpInitializeToolsTests</c>/<c>SpaceMcpToolCallTests</c>, which pre-date this
/// task's HTTP wiring).
///
/// Covers the Task-7 Step-1 acceptance cases from the plan: the initialize/session-mint
/// envelope, the per-request re-auth + binding check (SF-5, "session-id is not a credential"),
/// Origin allow-listing (S3), MCP-Protocol-Version gating, DELETE teardown, and N5 (a
/// client-supplied Mcp-Session-Id on initialize is ignored).
///
/// Every seeded Space here has ZERO published MCP servers — `tools/list` legitimately returns an
/// empty catalog (<see cref="Korat.Cloud.Mcp.Space.AggregateCatalog"/>'s own doc comment: "an
/// empty tools/list is a valid result"), so these tests only need
/// <see cref="KoratIntegrationFixture.SeedUserAsync"/> + a scoped token — no
/// <see cref="FakeMcpPublisher"/>, no granted server. Backend-routing coverage already lives in
/// <c>SpaceMcpToolCallTests</c>.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpResponderTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private const string InitializedNotificationBody = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        string spaceSeg,
        string? body = null,
        string? sessionId = null,
        string? origin = null,
        string? protocolVersion = "2025-06-18",
        bool includeEventStreamAccept = true,
        string? acceptOverride = null)
    {
        var request = new HttpRequestMessage(method, $"/mcp/{spaceSeg}");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        request.Headers.Accept.Clear();
        if (acceptOverride is not null)
        {
            // N5 (adversarial review, third pass) test support: drive an arbitrary raw Accept
            // value (e.g. the bare wildcard `*/*` a real `curl` default sends) instead of the
            // two-concrete-media-types default below.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptOverride));
        }
        else
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (includeEventStreamAccept)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (protocolVersion is not null)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        if (sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        return request;
    }

    /// <summary>Drives a full `initialize` over HTTP and returns the minted session id + the
    /// parsed JSON-RPC envelope, asserting the 200/header/content-type shape along the way so
    /// every OTHER test in this file can build on top of "initialize already works".</summary>
    private static async Task<(string SessionId, JsonNode Envelope)> InitializeSessionAsync(
        HttpClient client, string spaceSeg, string token)
    {
        var request = BuildRequest(HttpMethod.Post, spaceSeg, InitializeBody);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values),
            "Expected a Mcp-Session-Id response header on a successful initialize.");
        var sessionId = Assert.Single(values);

        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (sessionId, envelope);
    }

    // ── initialize: 200 + Mcp-Session-Id header + InitializeResult envelope ─────────────

    [Fact]
    public async Task Initialize_NoSessionId_Returns200_WithSessionHeader_AndInitializeResultEnvelope()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-init-{Guid.NewGuid():N}@example.com", "Space MCP Responder Init");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        var sessionId = Assert.Single(values);
        // >=128-bit CSPRNG (SF-5) — 16 bytes hex-encoded is 32 chars; the plan's own bar is
        // ">=22 hex chars ~128-bit", so assert generously against implementation drift.
        Assert.True(sessionId.Length >= 22, $"Expected a >=128-bit CSPRNG session id, got '{sessionId}' ({sessionId.Length} chars).");
        Assert.Matches("^[0-9A-Fa-f]+$", sessionId);

        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("2.0", envelope["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, envelope["id"]!.GetValue<int>());
        Assert.Equal("korat-space", envelope["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(envelope["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
    }

    // ── tools/list without a session id → 400 ───────────────────────────────────────────

    [Fact]
    public async Task ToolsList_WithoutSessionId_Returns400()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-nosess-{Guid.NewGuid():N}@example.com", "Space MCP Responder NoSession");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── tools/list against an unknown session id → 404 ──────────────────────────────────

    [Fact]
    public async Task ToolsList_UnknownSessionId_Returns404()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-unknown-{Guid.NewGuid():N}@example.com", "Space MCP Responder Unknown");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody,
            sessionId: Guid.NewGuid().ToString("N"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── tools/list against a valid session → 200 catalog ────────────────────────────────

    [Fact]
    public async Task ToolsList_ValidSession_Returns200_WithCatalog()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-toolslist-{Guid.NewGuid():N}@example.com", "Space MCP Responder ToolsList");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var (sessionId, _) = await InitializeSessionAsync(client, seeded.SpaceId, token);

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(2, envelope["id"]!.GetValue<int>());
        // No servers published in this fresh Space — an empty (not missing/null) tools array.
        Assert.Empty(envelope["result"]!["tools"]!.AsArray());
    }

    // ── a notification on a valid session → 202, no body ────────────────────────────────

    [Fact]
    public async Task Notification_ValidSession_Returns202_NoBody()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-notif-{Guid.NewGuid():N}@example.com", "Space MCP Responder Notification");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var (sessionId, _) = await InitializeSessionAsync(client, seeded.SpaceId, token);

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializedNotificationBody, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(content);
    }

    // ── SF-5 "session-id is not a credential": a DIFFERENT owner's own valid token, used ────
    // ── against their OWN Space, must not be able to ride someone else's session id ────────

    [Fact]
    public async Task DifferentOwnersOwnToken_RidingAnotherOwnersSessionId_Returns404()
    {
        var ownerA = await fixture.SeedUserAsync(
            $"space-mcp-resp-ownerA-{Guid.NewGuid():N}@example.com", "Space MCP Responder Owner A");
        var ownerB = await fixture.SeedUserAsync(
            $"space-mcp-resp-ownerB-{Guid.NewGuid():N}@example.com", "Space MCP Responder Owner B");

        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (tokenA, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, ownerA.UserId, ownerA.SpaceId);
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (tokenB, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, ownerB.UserId, ownerB.SpaceId);

        var client = fixture.Factory.CreateClient();
        var (sessionId, _) = await InitializeSessionAsync(client, ownerA.SpaceId, tokenA);

        // Owner B legitimately authenticates against THEIR OWN Space (their own token, their own
        // path segment — SpaceMcpAuth has no reason to reject this on its own) but presents Owner
        // A's session id. The binding check must catch this: session S is bound to
        // (consumerIdentity_A, SpaceA), not (consumerIdentity_B, SpaceB).
        var request = BuildRequest(HttpMethod.Post, ownerB.SpaceId, ToolsListBody, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_WithValidSessionId_Returns401()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-notoken-{Guid.NewGuid():N}@example.com", "Space MCP Responder NoToken");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var (sessionId, _) = await InitializeSessionAsync(client, seeded.SpaceId, token);

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, sessionId: sessionId);
        // Deliberately no Authorization header at all.

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Origin (S3): present-but-not-allowlisted → 403; absent → allowed ────────────────

    [Fact]
    public async Task PresentDisallowedOrigin_Returns403()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-origin403-{Guid.NewGuid():N}@example.com", "Space MCP Responder Origin403");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody, origin: "https://evil.example.com");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AbsentOrigin_Succeeds()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-noorigin-{Guid.NewGuid():N}@example.com", "Space MCP Responder NoOrigin");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody); // no Origin header at all
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── MCP-Protocol-Version: absent → default accepted; unsupported → 400 ──────────────

    [Fact]
    public async Task AbsentProtocolVersion_IsAcceptedAsDefault()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-noversion-{Guid.NewGuid():N}@example.com", "Space MCP Responder NoVersion");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody, protocolVersion: null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_Returns400()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-badversion-{Guid.NewGuid():N}@example.com", "Space MCP Responder BadVersion");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody, protocolVersion: "1999-01-01");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── DELETE: 204, then a subsequent POST against the same session id → 404 ───────────

    [Fact]
    public async Task Delete_ValidSession_Returns204_SubsequentPost_Returns404()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-delete-{Guid.NewGuid():N}@example.com", "Space MCP Responder Delete");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var (sessionId, _) = await InitializeSessionAsync(client, seeded.SpaceId, token);

        var deleteRequest = BuildRequest(HttpMethod.Delete, seeded.SpaceId, sessionId: sessionId);
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deleteResponse = await client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var postRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, sessionId: sessionId);
        postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var postResponse = await client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
    }

    // ── N5: a bare wildcard Accept (curl's own default) is tolerated, not rejected ──────────

    [Fact]
    public async Task Initialize_AcceptWildcardOnly_Succeeds()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-n5wild-{Guid.NewGuid():N}@example.com", "Space MCP Responder N5 Wildcard");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody, acceptOverride: "*/*");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── N9: `initialize` sent as a notification (no `id`) is malformed — 400, not a minted
    // ── session under a literal id:null envelope ────────────────────────────────────────

    [Fact]
    public async Task Initialize_NotificationShaped_NoId_Returns400()
    {
        const string notificationShapedInitialize = """
            {"jsonrpc":"2.0","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
            """;

        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-n9-{Guid.NewGuid():N}@example.com", "Space MCP Responder N9");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, notificationShapedInitialize);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"),
            "A rejected initialize must never mint/leak a session id.");
    }

    // ── N5: a client-supplied Mcp-Session-Id on initialize is ignored; a fresh id is minted ──

    [Fact]
    public async Task Initialize_ClientSuppliedSessionId_IsIgnored_FreshIdMinted()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-resp-n5-{Guid.NewGuid():N}@example.com", "Space MCP Responder N5");
        // Р25: the endpoint accepts OAuth only — bearer from the real flow.
        var (token, _) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var client = fixture.Factory.CreateClient();

        const string clientSuppliedId = "client-chosen-session-id-0000000000000000";

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody, sessionId: clientSuppliedId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        var mintedId = Assert.Single(values);
        Assert.NotEqual(clientSuppliedId, mintedId);
    }
}
