using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using static Korat.Cloud.IntegrationTests.SpaceMcp.SpaceMcpContentTests;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpContentNotificationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ResourceUpdates_ReachOnlySubscriber_OverHttpSse_AndStopAfterUnsubscribe()
    {
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromMilliseconds(150);
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMilliseconds(150);
        try
        {
            var user = await fixture.SeedUserAsync($"subscription-{Guid.NewGuid():N}@example.com", "Subscriber");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject { ["subscribe"] = true } };
            publisher.ResourceUpdateWithSubscribeReply = true;
            var subscriptions = 0;
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.RequestHandler = (method, _) =>
            {
                if (method == "resources/subscribe" && Interlocked.Increment(ref subscriptions) == 2)
                    restored.TrySetResult();
                return method switch
                {
                    "resources/list" => Result("""{"resources":[{"uri":"file:///live","name":"Live"}]}"""),
                    "resources/templates/list" => Result("""{"resourceTemplates":[]}"""),
                    "resources/subscribe" or "resources/unsubscribe" => Result("{}"),
                    _ => null,
                };
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            var sourceSession = Assert.Single(publisher.SeenSessionIds);
            using var secondClient = fixture.Factory.CreateClient();
            secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (secondSession, _) = await InitializeAsync(secondClient, user.SpaceId);
            var secondSourceSession = Assert.Single(publisher.SeenSessionIds, s => s != sourceSession);
            var uri = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///live");
            try
            {
                var sse = WaitForNotificationAsync(client, user.SpaceId, "notifications/resources/updated");
                var subscribed = await RequestAsync(client, user.SpaceId, session, "resources/subscribe", new() { ["uri"] = uri });
                Assert.Null(subscribed["error"]);
                var notification = await sse;
                Assert.Equal(uri, notification["params"]!["uri"]!.GetValue<string>());

                // The backend may send an unsolicited update to its other consumer's session.
                await SendNotificationAsync(publisher, secondSourceSession, "notifications/resources/updated", "file:///live");
                var other = await fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(secondSession).NextNotificationsAsync(0);
                Assert.Empty(other.Messages);

                var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(session);
                var beforeReconnect = await grain.NextNotificationsAsync(0);
                using (var scope = fixture.Factory.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<SessionTerminator>()
                        .TerminateSessionAsync(new SessionId(sourceSession), SessionCloseReason.ServerUnavailable, default);
                await restored.Task.WaitAsync(TimeSpan.FromSeconds(8));
                // Lost updates are invalidated after the new backend accepts the restored subscription.
                using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                SpaceMcpNotifications restoredEvents;
                do { restoredEvents = await grain.NextNotificationsAsync(beforeReconnect.Cursor).WaitAsync(restoreTimeout.Token); }
                while (!restoredEvents.Messages.Any(m => JsonNode.Parse(m)?["params"]?["uri"]?.GetValue<string>() == uri));
                Assert.Contains(restoredEvents.Messages, m => JsonNode.Parse(m)?["params"]?["uri"]?.GetValue<string>() == uri);
                sourceSession = Assert.Single(publisher.SeenSessionIds, s => s != sourceSession && s != secondSourceSession);

                var unsubscribed = await RequestAsync(client, user.SpaceId, session, "resources/unsubscribe", new() { ["uri"] = uri });
                Assert.Null(unsubscribed["error"]);
                await SendNotificationAsync(publisher, sourceSession, "notifications/resources/updated", "file:///live");
                var after = await fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(session).NextNotificationsAsync(0);
                Assert.DoesNotContain(after.Messages, m => m.Contains("notifications/resources/updated", StringComparison.Ordinal));
            }
            finally
            {
                await client.DeleteAsync($"/mcp/{user.SpaceId}");
                await secondClient.DeleteAsync($"/mcp/{user.SpaceId}");
            }
        }
        finally
        {
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
            SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval;
        }
    }

    [Fact]
    public async Task BackendCatalogNotifications_RefreshResourcesTemplatesAndPrompts_WithoutReconnect()
    {
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMilliseconds(150);
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromSeconds(1);
        try
        {
            var user = await fixture.SeedUserAsync($"content-change-{Guid.NewGuid():N}@example.com", "Content");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject { ["listChanged"] = true }, ["prompts"] = new JsonObject { ["listChanged"] = true } };
            var version = 1;
            publisher.RequestHandler = (method, _) => method switch
            {
                "resources/list" => Result($$"""{"resources":[{"uri":"file:///v{{version}}","name":"Version"}]}"""),
                "resources/templates/list" => Result($$"""{"resourceTemplates":[{"uriTemplate":"file:///v{{version}}/{path}","name":"Version"}]}"""),
                "prompts/list" => Result($$"""{"prompts":[{"name":"v{{version}}"}]}"""),
                _ => null,
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                var sourceSession = Assert.Single(publisher.SeenSessionIds);
                var resourcesChanged = WaitForNotificationsAsync(client, user.SpaceId,
                    "notifications/resources/list_changed", "notifications/prompts/list_changed");
                version = 2;
                await SendNotificationAsync(publisher, sourceSession, "notifications/resources/list_changed");
                await resourcesChanged;
                var resources = await RequestAsync(client, user.SpaceId, session, "resources/list");
                Assert.Contains(resources["result"]!["resources"]!.AsArray(), r => r?["uri"]?.GetValue<string>() == McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///v2"));
                var templates = await RequestAsync(client, user.SpaceId, session, "resources/templates/list");
                Assert.Equal(McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///v2/{path}"), templates["result"]!["resourceTemplates"]![0]!["uriTemplate"]!.GetValue<string>());
                var prompts = await RequestAsync(client, user.SpaceId, session, "prompts/list");
                Assert.Contains(prompts["result"]!["prompts"]!.AsArray(), p => p?["name"]?.GetValue<string>() == "docs__v2");
                var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(session);
                var baseline = await grain.NextNotificationsAsync(0);
                using var cancelled = new CancellationTokenSource();
                var abandoned = grain.NextNotificationsAsync(baseline.Cursor, cancelled.Token);
                await Task.Delay(100); // Let the abandoned reader enter the remote wait.
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(3)));
                version = 3;
                var readerA = grain.NextNotificationsAsync(baseline.Cursor);
                var readerB = grain.NextNotificationsAsync(baseline.Cursor);
                await SendNotificationAsync(publisher, sourceSession, "notifications/resources/list_changed");
                var batches = await Task.WhenAll(readerA, readerB);
                Assert.Single(batches.SelectMany(b => b.Messages), m => m.Contains("notifications/resources/list_changed", StringComparison.Ordinal));
                Assert.Single(batches.SelectMany(b => b.Messages), m => m.Contains("notifications/prompts/list_changed", StringComparison.Ordinal));
                Assert.Single(publisher.SeenSessionIds);
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval;
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostBackend_WithMissingSubscribedResource_DoesNotBlockOtherContentOrLocalUnsubscribe(bool unsubscribeOffline)
    {
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromHours(1); // Exercise lazy reopen deterministically.
        try
        {
            var user = await fixture.SeedUserAsync($"stale-subscription-{Guid.NewGuid():N}@example.com", "Subscriber");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject { ["subscribe"] = true } };
            var resourceDeleted = false;
            publisher.RequestHandler = (method, _) => method switch
            {
                "resources/list" => Result("""{"resources":[{"uri":"file:///other","name":"Other"}]}"""),
                "resources/templates/list" => Result("""{"resourceTemplates":[]}"""),
                "resources/subscribe" when resourceDeleted => JsonNode.Parse("""{"error":{"code":-32002,"message":"Resource not found"}}""")!.AsObject(),
                "resources/subscribe" or "resources/unsubscribe" => Result("{}"),
                "resources/read" => Result("""{"contents":[{"uri":"file:///other","text":"still works"}]}"""),
                _ => null,
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            var missingUri = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///deleted");
            try
            {
                Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/subscribe", new() { ["uri"] = missingUri }))["error"]);
                resourceDeleted = true;
                using (var scope = fixture.Factory.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<SessionTerminator>()
                        .TerminateSessionAsync(new SessionId(Assert.Single(publisher.SeenSessionIds)), SessionCloseReason.ServerUnavailable, default);
                if (unsubscribeOffline)
                {
                    var unsubscribed = await RequestAsync(client, user.SpaceId, session, "resources/unsubscribe", new() { ["uri"] = missingUri });
                    Assert.Null(unsubscribed["error"]);
                    Assert.Single(publisher.SeenSessionIds); // Local cancellation must not wake/open a backend.
                }
                var other = await RequestAsync(client, user.SpaceId, session, "resources/read",
                    new() { ["uri"] = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///other") });
                Assert.Null(other["error"]);
                Assert.Equal("still works", other["result"]!["contents"]![0]!["text"]!.GetValue<string>());
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally { SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval; }
    }

    [Fact]
    public async Task SlowSubscriptionRestoration_DoesNotConsumeTheBackendOpenBudget()
    {
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        var originalTimeout = SpaceMcpAggregatorGrain.PerBackendTimeout;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromHours(1);
        try
        {
            var user = await fixture.SeedUserAsync($"slow-restore-{Guid.NewGuid():N}@example.com", "Subscriber");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject { ["subscribe"] = true } };
            publisher.RequestHandler = (method, _) => method switch
            {
                "resources/list" => Result("""{"resources":[{"uri":"file:///other","name":"Other"}]}"""),
                "resources/templates/list" => Result("""{"resourceTemplates":[]}"""),
                "resources/subscribe" or "resources/unsubscribe" => Result("{}"),
                "resources/read" => Result("""{"contents":[{"uri":"file:///other","text":"available"}]}"""),
                _ => null,
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                for (var i = 0; i < 4; i++)
                    Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/subscribe",
                        new() { ["uri"] = McpContentCatalog.WrapResourceUri(server.Id.Value, $"file:///watch/{i}") }))["error"]);
                var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var count = 0;
                publisher.AsyncRequestHandler = async (method, _) =>
                {
                    if (method == "resources/subscribe")
                    {
                        await Task.Delay(200);
                        if (Interlocked.Increment(ref count) == 4) restored.TrySetResult();
                    }
                    return null;
                };
                using (var scope = fixture.Factory.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<SessionTerminator>()
                        .TerminateSessionAsync(new SessionId(Assert.Single(publisher.SeenSessionIds)), SessionCloseReason.ServerUnavailable, default);
                // Each reply fits the request deadline, but all four exceed the OPEN budget.
                SpaceMcpAggregatorGrain.PerBackendTimeout = TimeSpan.FromMilliseconds(450);
                var read = await RequestAsync(client, user.SpaceId, session, "resources/read",
                    new() { ["uri"] = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///other") });
                Assert.Null(read["error"]);
                Assert.Equal("available", read["result"]!["contents"]![0]!["text"]!.GetValue<string>());
                await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, publisher.SeenSessionIds.Count); // No reconnect loop.
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval;
            SpaceMcpAggregatorGrain.PerBackendTimeout = originalTimeout;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsubscribeWhileRestoreReplyIsPending_PreservesTheLatestIntent(bool resubscribe)
    {
        var originalInterval = SpaceMcpAggregatorGrain.ReconcileInterval;
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromHours(1);
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromMilliseconds(150);
        try
        {
            var user = await fixture.SeedUserAsync($"restore-intent-{Guid.NewGuid():N}@example.com", "Subscriber");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var node = NodeId.New();
            var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
                await fixture.IssueCliTokenAsync(user.UserId), []);
            publisher.Capabilities = new() { ["resources"] = new JsonObject { ["subscribe"] = true } };
            var unsubscribes = 0;
            var undone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            publisher.RequestHandler = (method, _) =>
            {
                if (method == "resources/unsubscribe" && Interlocked.Increment(ref unsubscribes) == 2) undone.TrySetResult();
                return method switch
                {
                    "resources/list" => Result("""{"resources":[{"uri":"file:///live","name":"Live"}]}"""),
                    "resources/templates/list" => Result("""{"resourceTemplates":[]}"""),
                    "resources/subscribe" or "resources/unsubscribe" => Result("{}"),
                    "resources/read" => Result("""{"contents":[{"uri":"file:///live","text":"available"}]}"""),
                    _ => null,
                };
            };
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            var uri = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///live");
            try
            {
                Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/subscribe", new() { ["uri"] = uri }))["error"]);
                var held = new TaskCompletionSource<(string Session, JsonNode Id)>(TaskCreationOptions.RunContinuationsAsynchronously);
                publisher.HoldRequest = (sourceSession, request) => request["method"]?.GetValue<string>() == "resources/subscribe"
                    && held.TrySetResult((sourceSession, request["id"]!.DeepClone()));
                using (var scope = fixture.Factory.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<SessionTerminator>()
                        .TerminateSessionAsync(new SessionId(Assert.Single(publisher.SeenSessionIds)), SessionCloseReason.ServerUnavailable, default);
                // The source is usable even while its restore response has not arrived.
                Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/read", new() { ["uri"] = uri }))["error"]);
                var pending = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/unsubscribe", new() { ["uri"] = uri }))["error"]);
                if (resubscribe)
                    Assert.Null((await RequestAsync(client, user.SpaceId, session, "resources/subscribe", new() { ["uri"] = uri }))["error"]);
                await publisher.SendRawFrameAsync(pending.Session, Encoding.UTF8.GetBytes(new JsonObject
                    { ["jsonrpc"] = "2.0", ["id"] = pending.Id, ["result"] = new JsonObject() }.ToJsonString() + "\n"), 0);
                if (!resubscribe) await undone.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await SendNotificationAsync(publisher, pending.Session, "notifications/resources/updated", "file:///live");
                var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(session);
                var messages = new List<string>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                do
                {
                    var batch = await grain.NextNotificationsAsync(0, timeout.Token);
                    messages.AddRange(batch.Messages);
                }
                while (resubscribe && !messages.Any(m => JsonNode.Parse(m)?["params"]?["uri"]?.GetValue<string>() == uri));
                Assert.Equal(resubscribe, messages.Any(m => JsonNode.Parse(m)?["params"]?["uri"]?.GetValue<string>() == uri));
                Assert.Equal(resubscribe ? 1 : 2, Volatile.Read(ref unsubscribes));
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
        }
        finally
        {
            SpaceMcpAggregatorGrain.ReconcileInterval = originalInterval;
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
        }
    }

    private static JsonObject Result(string json) => new() { ["result"] = JsonNode.Parse(json) };

    private static Task SendNotificationAsync(FakeMcpPublisher publisher, string session, string method, string? uri = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (uri is not null) message["params"] = new JsonObject { ["uri"] = uri };
        return publisher.SendRawFrameAsync(session, Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), 0);
    }

    private static async Task<JsonObject> WaitForNotificationAsync(HttpClient client, string space, string method) =>
        Assert.Single(await WaitForNotificationsAsync(client, space, method));

    private static async Task<IReadOnlyList<JsonObject>> WaitForNotificationsAsync(HttpClient client, string space, params string[] methods)
    {
        var pending = methods.ToHashSet(StringComparer.Ordinal);
        var received = new List<JsonObject>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/mcp/{space}");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var notification = JsonNode.Parse(line[6..])!.AsObject();
            if (notification["method"]?.GetValue<string>() is { } method && pending.Remove(method))
                received.Add(notification);
            if (pending.Count == 0) return received;
        }
        throw new InvalidOperationException("The event stream ended before the expected notification.");
    }
}
