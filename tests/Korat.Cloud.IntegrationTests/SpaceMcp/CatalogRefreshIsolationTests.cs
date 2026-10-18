using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.GrainInterfaces;
using static Korat.Cloud.IntegrationTests.SpaceMcp.SpaceMcpContentTests;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

[Trait("Category", "SpaceMcp")]
public sealed class CatalogRefreshIsolationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task HungRefresh_DoesNotBlockLists_HealthyUpdates_OrRevocation()
    {
        var oldInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            var user = await fixture.SeedUserAsync($"refresh-isolation-{Guid.NewGuid():N}@test.local", "Owner");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var hungNode = NodeId.New();
            var healthyNode = NodeId.New();
            var hungServer = (await space.PublishMcpServerAsync(hungNode, "Slow", "echo", ""))!;
            var healthyServer = (await space.PublishMcpServerAsync(healthyNode, "Healthy", "echo", ""))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var hungAccess = await space.CreateAccessRequestAsync(consumer, hungServer.Id, NodeId.New());
            var hungGrant = await space.ApproveAccessRequestAsync(hungAccess.Id, user.UserId);
            var healthyAccess = await space.CreateAccessRequestAsync(consumer, healthyServer.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(healthyAccess.Id, user.UserId);
            var cliToken = await fixture.IssueCliTokenAsync(user.UserId);
            await using var hung = await FakeMcpPublisher.ConnectAsync(fixture.Factory, hungNode.Value, cliToken, []);
            await using var healthy = await FakeMcpPublisher.ConnectAsync(fixture.Factory, healthyNode.Value, cliToken, []);
            hung.Capabilities = new() { ["resources"] = new JsonObject() };
            healthy.Capabilities = new() { ["resources"] = new JsonObject() };
            var changed = false;
            hung.RequestHandler = (method, _) => Catalog(method, "file:///slow");
            healthy.RequestHandler = (method, _) => Catalog(method, Volatile.Read(ref changed) ? "file:///after" : "file:///before");
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hung.HoldRequest = (_, message) =>
                {
                    if (message["method"]?.GetValue<string>() != "resources/list") return false;
                    held.TrySetResult();
                    return true;
                };
                await NotifyAsync(hung, Assert.Single(hung.SeenSessionIds));
                await held.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // No upstream answer is released: the read must use authorized cached content.
                foreach (var method in new[] { "tools/list", "resources/list", "resources/templates/list", "prompts/list" })
                    Assert.Null((await RequestAsync(client, user.SpaceId, session, method).WaitAsync(TimeSpan.FromSeconds(3)))["error"]);

                var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(session);
                var baseline = await grain.NextNotificationsAsync(0);
                var notification = grain.NextNotificationsAsync(baseline.Cursor);
                Volatile.Write(ref changed, true);
                await NotifyAsync(healthy, Assert.Single(healthy.SeenSessionIds));
                Assert.Contains((await notification.WaitAsync(TimeSpan.FromSeconds(3))).Messages,
                    m => m.Contains("notifications/resources/list_changed", StringComparison.Ordinal));
                var expected = McpContentCatalog.WrapResourceUri(healthyServer.Id.Value, "file:///after");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var list = await RequestAsync(client, user.SpaceId, session, "resources/list").WaitAsync(timeout.Token);
                    if (list["result"]!["resources"]!.AsArray().Any(r => r?["uri"]?.GetValue<string>() == expected)) break;
                    await Task.Delay(25, timeout.Token);
                }

                await space.RevokeGrantAsync(hungGrant.Id, user.UserId);
                var afterRevoke = await RequestAsync(client, user.SpaceId, session, "resources/list").WaitAsync(TimeSpan.FromSeconds(3));
                Assert.DoesNotContain(afterRevoke["result"]!["resources"]!.AsArray(), r => r?["uri"]?.GetValue<string>() == McpContentCatalog.WrapResourceUri(hungServer.Id.Value, "file:///slow"));
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally { SpaceMcpAggregatorGrain.ReconcileInterval = oldInterval; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InflightRefresh_IsSingleFlight_AndRetainsLaterInvalidationOrFailure(bool failFirst)
    {
        var oldInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            var user = await fixture.SeedUserAsync($"refresh-retry-{Guid.NewGuid():N}@test.local", "Owner");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "echo", ""))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject() };
            publisher.RequestHandler = (method, _) => Catalog(method, "file:///latest");
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                var held = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                publisher.HoldRequest = (_, message) =>
                {
                    if (message["method"]?.GetValue<string>() != "resources/list") return false;
                    if (Interlocked.Increment(ref requests) != 1) return false;
                    held.TrySetResult(message["id"]!.DeepClone());
                    return true;
                };
                var sourceSession = Assert.Single(publisher.SeenSessionIds);
                await NotifyAsync(publisher, sourceSession);
                var heldId = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var i = 0; i < 3; i++)
                {
                    // On success, exercise invalidations arriving during the held request;
                    // on failure there is no second invalidation, so retry must be automatic.
                    if (!failFirst) await NotifyAsync(publisher, sourceSession);
                    await RequestAsync(client, user.SpaceId, session, "resources/list").WaitAsync(TimeSpan.FromSeconds(3));
                    await Task.Delay(100);
                }
                Assert.Equal(1, Volatile.Read(ref requests));
                JsonObject reply = failFirst
                    ? new() { ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "Transient failure" } }
                    : Catalog("resources/list", "file:///stale")!;
                reply["jsonrpc"] = "2.0";
                reply["id"] = heldId;
                await publisher.SendRawFrameAsync(sourceSession, Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n"), 0);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var expected = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///latest");
                while (true)
                {
                    var list = await RequestAsync(client, user.SpaceId, session, "resources/list").WaitAsync(timeout.Token);
                    if (Volatile.Read(ref requests) >= 2
                        && list["result"]!["resources"]!.AsArray().Any(r => r?["uri"]?.GetValue<string>() == expected)) break;
                    await Task.Delay(25, timeout.Token);
                }
                Assert.Equal(2, Volatile.Read(ref requests));
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally { SpaceMcpAggregatorGrain.ReconcileInterval = oldInterval; }
    }

    private static JsonObject? Catalog(string method, string uri) => method switch
    {
        "resources/list" => new() { ["result"] = new JsonObject { ["resources"] = new JsonArray(new JsonObject { ["uri"] = uri, ["name"] = "Document" }) } },
        "resources/templates/list" => new() { ["result"] = new JsonObject { ["resourceTemplates"] = new JsonArray() } },
        _ => null,
    };

    private static Task NotifyAsync(FakeMcpPublisher publisher, string session) => publisher.SendRawFrameAsync(session,
        Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/list_changed\"}\n"), 0);
}
