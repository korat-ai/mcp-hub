using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Reproduction / coverage gap (2026-07-12): the Space-MCP aggregator was only ever exercised
/// end-to-end against a <c>stdio_node</c> relay backend (<see cref="SpaceMcpEndToEndRelayTests"/>
/// uses <c>FakeMcpPublisher</c>). A <c>http_cloud</c> (cloud-terminated, <c>HttpMcpProxyGrain</c>)
/// backend — the transport a "Miro (inc2 test)"-style server uses — was NEVER driven THROUGH the
/// aggregator. In production a granted http_cloud backend never surfaced its tools: the
/// aggregator's backend handshake (initialize/tools/list) hung for the full PerBackendTimeout and
/// the open silently failed, so the server vanished from the consumer's catalog (a ~20s-spaced
/// open/close retry storm, zero exceptions).
///
/// This drives the SAME wire path <see cref="SpaceMcpEndToEndRelayTests"/> does — but with a
/// <c>http_cloud</c> backend pointing at an in-process stub upstream instead of a relay publisher:
/// agent(sentinel) → ForwardFrameAsync → HttpMcpProxyGrain(DispatchFrameAsync) → stub upstream →
/// PushHttpCloudResponseAsync → delivery-leg (CallbackServerStreamWriter → OnDeliveryAsync) →
/// aggregator. If the delivery-leg for http_cloud is wired, tools/list returns the backend's tool;
/// if not, this test reproduces the production hang (tools/list has no backend tool / the request
/// times out).
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpHttpCloudBackendTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"cursor","version":"1.0"}}}
        """;

    private const string InitializedNotificationBody = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    [Fact]
    public async Task GrantedHttpCloudBackend_SurfacesItsToolsThroughAggregator()
    {
        // ── Arrange: in-process stub upstream that speaks JSON-RPC MCP (the "remote Miro") ──────
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var stub = builder.Build();
        stub.MapPost("/", async ctx =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
            // Notifications (no id) get no JSON-RPC response body.
            if (!root.TryGetProperty("id", out var idEl))
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
            var id = idEl.GetRawText();
            ctx.Response.ContentType = "application/json";
            // Raw string literals (no '$') so the JSON braces are never treated as interpolation;
            // the request id is spliced in via a placeholder to avoid brace-escaping entirely.
            var template = method switch
            {
                "initialize" =>
                    """{"jsonrpc":"2.0","id":__ID__,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"stub-miro","version":"1.0"}}}""",
                "tools/list" =>
                    """{"jsonrpc":"2.0","id":__ID__,"result":{"tools":[{"name":"ping","description":"Ping the stub","inputSchema":{"type":"object","properties":{}}}]}}""",
                "tools/call" =>
                    """{"jsonrpc":"2.0","id":__ID__,"result":{"content":[{"type":"text","text":"pong"}]}}""",
                _ =>
                    """{"jsonrpc":"2.0","id":__ID__,"error":{"code":-32601,"message":"unknown"}}"""
            };
            await ctx.Response.WriteAsync(template.Replace("__ID__", id));
        });
        await stub.StartAsync();
        await using var stubDisposable = stub;
        var stubUrl = stub.Urls.First();

        // ── Seed owner + a http_cloud server + grant to the aggregator's derived identity ───────
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-httpcloud-{Guid.NewGuid():N}@example.com", "Space MCP HttpCloud");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var server = await space.CreateHttpMcpServerAsync(
            $"httpcloud-srv-{Guid.NewGuid():N}", stubUrl, McpServerAuthModes.None, null, null);

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (scopedToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var client = fixture.Factory.CreateClient();

        // ── initialize ──────────────────────────────────────────────────────────────────────────
        var initRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializeBody);
        initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var initResponse = await client.SendAsync(initRequest);
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var mcpSessionId = Assert.Single(initResponse.Headers.GetValues("Mcp-Session-Id"));

        // ── notifications/initialized ──────────────────────────────────────────────────────────
        var notifRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, InitializedNotificationBody, mcpSessionId);
        notifRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var notifResponse = await client.SendAsync(notifRequest);
        Assert.Equal(HttpStatusCode.Accepted, notifResponse.StatusCode);

        // ── tools/list — the http_cloud backend's namespaced tool MUST be present ───────────────
        var toolsListRequest = BuildRequest(HttpMethod.Post, seeded.SpaceId, ToolsListBody, mcpSessionId);
        toolsListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);
        var toolsListResponse = await client.SendAsync(toolsListRequest);

        Assert.Equal(HttpStatusCode.OK, toolsListResponse.StatusCode);
        var toolsEnvelope = JsonNode.Parse(await toolsListResponse.Content.ReadAsStringAsync())!;
        var toolNames = toolsEnvelope["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();

        var expectedSlug = ToolNamespacer.Slug(server.DisplayName, server.Id.Value);
        var expectedToolName = ToolNamespacer.Namespaced(expectedSlug, "ping");
        Assert.Contains(expectedToolName, toolNames);
    }

    // ── helpers (mirror SpaceMcpEndToEndRelayTests) ──────────────────────────────────────────
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
