using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Korat.Cloud.Gateways;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using static Korat.Cloud.IntegrationTests.SpaceMcp.SpaceMcpContentTests;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpTargetedRoutingTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutedRequests_DoNotScanTheSpace_EvenAfterReconnect(bool reconnect)
    {
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromHours(1);
        try
        {
            var user = await fixture.SeedUserAsync($"targeted-routing-{Guid.NewGuid():N}@example.com", "Routing");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            // A sibling is not authorized or reachable. Its existence must not add work to a
            // request for Docs; all-space discovery is deliberately unavailable after init.
            await space.PublishMcpServerAsync(NodeId.New(), "Unrelated", "stdio", "other");
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            var grant = await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), [("inspect", "Inspect", null)]);
            publisher.Capabilities = new()
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject { ["subscribe"] = true },
                ["prompts"] = new JsonObject(),
                ["completions"] = new JsonObject(),
            };
            var received = new ConcurrentQueue<string>();
            publisher.ToolCallHandler = (_, _) =>
            {
                received.Enqueue("tools/call");
                return """{"content":[{"type":"text","text":"target reached"}]}""";
            };
            publisher.RequestHandler = (method, _) =>
            {
                received.Enqueue(method);
                var result = method switch
                {
                    "resources/list" => """{"resources":[{"uri":"file:///doc","name":"Doc"}]}""",
                    "resources/templates/list" => """{"resourceTemplates":[]}""",
                    "prompts/list" => """{"prompts":[{"name":"review"}]}""",
                    "resources/read" => """{"contents":[{"uri":"file:///doc","text":"target reached"}]}""",
                    "prompts/get" => """{"messages":[{"role":"user","content":{"type":"text","text":"target reached"}}]}""",
                    "completion/complete" => """{"completion":{"values":["target reached"]}}""",
                    "resources/subscribe" or "resources/unsubscribe" => "{}",
                    _ => null,
                };
                return result is null ? null : new JsonObject { ["result"] = JsonNode.Parse(result) };
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                if (reconnect)
                {
                    using var scope = fixture.Factory.Services.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<SessionTerminator>()
                        .TerminateSessionAsync(new SessionId(Assert.Single(publisher.SeenSessionIds)),
                            SessionCloseReason.ServerUnavailable, default);
                }

                using var probe = SpaceMcpDiscoveryProbe.Block(user.SpaceId);
                // Positive control: a scan really fails, rather than passing because the probe
                // never observes Orleans calls. The routed operations below must add zero scans.
                await Assert.ThrowsAsync<KoratDomainException>(() => space.ListMcpServersAsync());
                Assert.Equal(1, probe.Calls);
                var uri = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///doc");
                (string Method, JsonObject Parameters)[] requests =
                [
                    ("tools/call", new() { ["name"] = "docs__inspect" }),
                    ("resources/read", new() { ["uri"] = uri }),
                    ("prompts/get", new() { ["name"] = "docs__review" }),
                    ("completion/complete", new()
                    {
                        ["ref"] = new JsonObject { ["type"] = "ref/prompt", ["name"] = "docs__review" },
                        ["argument"] = new JsonObject { ["name"] = "goal", ["value"] = "t" },
                    }),
                    ("resources/subscribe", new() { ["uri"] = uri }),
                    ("resources/unsubscribe", new() { ["uri"] = uri }),
                ];
                foreach (var (method, parameters) in requests)
                {
                    var response = await RequestAsync(client, user.SpaceId, session, method, parameters);
                    Assert.True(response["error"] is null, response.ToJsonString());
                    Assert.NotNull(response["result"]);
                    Assert.Contains(method, received);
                }
                Assert.Equal(1, probe.Calls);
                Assert.Equal(reconnect ? 2 : 1, publisher.SeenSessionIds.Count);

                // Keep the backend alive and revoke only the durable grant. The targeted path
                // must still fail closed without depending on a close event or the full scan.
                await space.RevokeGrantAsync(grant.Id, user.UserId);
                var callsBeforeRevoke = received.Count;
                var denied = await RequestAsync(client, user.SpaceId, session, "tools/call",
                    new() { ["name"] = "docs__inspect" });
                Assert.Equal(-32000, denied["error"]!["code"]!.GetValue<int>());
                Assert.Equal(callsBeforeRevoke, received.Count);
                Assert.Equal(1, probe.Calls);
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally { SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval; }
    }
}
