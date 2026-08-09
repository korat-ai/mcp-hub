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
/// MUST-FIX 1 (adversarial review, Space-MCP increment 1 Tasks 7-8, BLOCKER): a `tools/call`
/// driven all the way through <c>SpaceMcpDispatcher.HandlePostAsync</c> (the real HTTP path, not
/// a grain-direct call like <c>SpaceMcpToolCallTests</c>) against a backend that takes a few
/// real seconds to answer must round-trip successfully — never a 500 — proving the
/// <c>[ResponseTimeout]</c> override on <see cref="ISpaceMcpAggregatorGrain.DispatchAsync"/>
/// (<c>SpaceMcpResponseTimeoutAttributeTests</c>) actually widens the boundary the DISPATCHER's
/// own <c>await grain.DispatchAsync(...)</c> call is subject to, not just the grain interface's
/// declared surface.
///
/// Deliberately only a few real seconds (not >30s) — actually waiting out Orleans' un-overridden
/// 30s default in a test would be slow and, per the adversarial review's own call-out,
/// impractical to run reliably. This is a regression test for the ordinary "the whole plumbing
/// still works for a not-instant backend" case; <c>SpaceMcpResponseTimeoutAttributeTests</c> is
/// what actually proves the timeout budget itself is wide enough for a legitimately slow (up to
/// 300s) tool call.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpSlowToolCallTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private static readonly TimeSpan BackendDelay = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ToolsCall_SlowBackend_RoundTripsThroughDispatcher_NoServerError()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-slowcall-{Guid.NewGuid():N}@example.com", "Space MCP Slow ToolCall");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"slowcall-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (token, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f: the fake publisher connects as a real relay node — a "full"-scoped token, never
        // the space-mcp-scoped `token` (which node Hello correctly rejects).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
        publisher.ToolCallDelay = BackendDelay;
        publisher.ToolCallHandler = (toolName, args) =>
        {
            var text = args?["text"]?.GetValue<string>() ?? "";
            return $$"""{"content":[{"type":"text","text":"echo:{{text}}"}]}""";
        };

        var client = fixture.Factory.CreateClient();
        // The HttpClient's own request timeout defaults to 100s (HttpClient.Timeout) — plenty of
        // margin over BackendDelay; no override needed.
        var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);

        var namespacedName = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");

        var callRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 42,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = namespacedName, ["arguments"] = new JsonObject { ["text"] = "hi" } },
        }.ToJsonString();

        var request = BuildRequest(HttpMethod.Post, seeded.SpaceId, callRequest, sessionId: sessionId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(42, envelope["id"]!.GetValue<int>());
        Assert.Null(envelope["error"]);
        var text = envelope["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("echo:hi", text);
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
