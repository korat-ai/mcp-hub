using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using static Korat.Cloud.IntegrationTests.SpaceMcp.SpaceMcpContentTests;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>Real upstream HTTP → proxy relay → Space aggregator → downstream HTTP/SSE.</summary>
[Trait("Category", "SpaceMcp")]
public sealed class HttpMcpContentNotificationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpBackend_ContentAndSubscriptionsReachDownstreamSse(bool postInterim)
    {
        var oldHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        var oldReconcile = SpaceMcpAggregatorGrain.ReconcileInterval;
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromMilliseconds(100);
        SpaceMcpAggregatorGrain.ReconcileInterval = TimeSpan.FromMilliseconds(100);
        var updates = Channel.CreateUnbounded<string>();
        var getStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = false;
        var catalogVersion = 1;
        const string updated = """{"jsonrpc":"2.0","method":"notifications/resources/updated","params":{"uri":"file:///live"}}""";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var stub = builder.Build();
        stub.MapPost("/", async ctx =>
        {
            var request = (await JsonNode.ParseAsync(ctx.Request.Body))!.AsObject();
            if (!request.ContainsKey("id")) { ctx.Response.StatusCode = 202; return; }
            var method = request["method"]!.GetValue<string>();
            if (method == "resources/subscribe") subscribed = true;
            if (method == "resources/unsubscribe") subscribed = false;
            JsonNode result = method switch
            {
                "initialize" => JsonNode.Parse("""{"protocolVersion":"2025-06-18","serverInfo":{"name":"http-docs","version":"1"},"capabilities":{"resources":{"subscribe":true,"listChanged":true},"prompts":{"listChanged":true},"completions":{}}}""")!,
                "resources/list" => JsonNode.Parse(catalogVersion == 1
                    ? """{"resources":[{"uri":"file:///live","name":"Live","mimeType":"text/plain"}]}"""
                    : """{"resources":[{"uri":"file:///live","name":"Live"},{"uri":"file:///new","name":"New"}]}""")!,
                "resources/templates/list" => JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"file:///docs/{path}","name":"Docs"}]}""")!,
                "resources/read" => new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = request["params"]!["uri"]!.DeepClone(), ["text"] = "HTTP contents" }) },
                "prompts/list" => JsonNode.Parse("""{"prompts":[{"name":"review","arguments":[{"name":"code","required":true}]}]}""")!,
                "prompts/get" => new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = request["params"]!["arguments"]!["code"]!.DeepClone() } }) },
                "completion/complete" => JsonNode.Parse("""{"completion":{"values":["intro"],"total":1,"hasMore":false}}""")!,
                _ => new JsonObject()
            };
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result };
            if (postInterim && method == "resources/read" && subscribed)
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync($"data: {updated}\n\ndata: {response.ToJsonString()}\n\n");
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(response.ToJsonString());
            }
        });
        stub.MapGet("/", async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.StartAsync();
            getStarted.TrySetResult();
            try
            {
                await foreach (var notification in updates.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"data: {notification}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { getStopped.TrySetResult(); }
        });
        try
        {
            await stub.StartAsync();
            var user = await fixture.SeedUserAsync($"http-content-{Guid.NewGuid():N}@example.com", "HTTP Content");
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
            var server = await space.CreateHttpMcpServerAsync("HttpDocs", stub.Urls.Single(), McpServerAuthModes.None, null, null);
            var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
            var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(access.Id, user.UserId);
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (session, _) = await InitializeAsync(client, user.SpaceId);
            try
            {
                await getStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var uri = McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///live");
                var list = await RequestAsync(client, user.SpaceId, session, "resources/list");
                Assert.Contains(list["result"]!["resources"]!.AsArray(), r => r?["uri"]?.GetValue<string>() == uri && r["mimeType"]?.GetValue<string>() == "text/plain");
                var templates = await RequestAsync(client, user.SpaceId, session, "resources/templates/list");
                Assert.Equal(McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///docs/{path}"), templates["result"]!["resourceTemplates"]![0]!["uriTemplate"]!.GetValue<string>());
                var promptName = ToolNamespacer.Namespaced(ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "review");
                var prompts = await RequestAsync(client, user.SpaceId, session, "prompts/list");
                Assert.Contains(prompts["result"]!["prompts"]!.AsArray(), p => p?["name"]?.GetValue<string>() == promptName);
                var prompt = await RequestAsync(client, user.SpaceId, session, "prompts/get", new() { ["name"] = promptName, ["arguments"] = new JsonObject { ["code"] = "HTTP prompt" } });
                Assert.Equal("HTTP prompt", prompt["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());

                // Keep one downstream stream for the whole sequence, as a normal MCP client
                // does. Replacing it between events would race the old in-flight long poll.
                using var sseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var sseRequest = new HttpRequestMessage(HttpMethod.Get, $"/mcp/{user.SpaceId}");
                sseRequest.Headers.Accept.ParseAdd("text/event-stream");
                using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, sseTimeout.Token);
                sseResponse.EnsureSuccessStatusCode();
                await using var sseStream = await sseResponse.Content.ReadAsStreamAsync(sseTimeout.Token);
                using var sseReader = new StreamReader(sseStream);

                var subscribe = await RequestAsync(client, user.SpaceId, session, "resources/subscribe", new() { ["uri"] = uri });
                Assert.Null(subscribe["error"]);
                var updateTask = WaitForNotificationAsync(sseReader, "notifications/resources/updated", sseTimeout.Token);
                if (!postInterim) await updates.Writer.WriteAsync(updated);
                var read = await RequestAsync(client, user.SpaceId, session, "resources/read", new() { ["uri"] = uri });
                Assert.Equal("HTTP contents", read["result"]!["contents"]![0]!["text"]!.GetValue<string>());
                Assert.Equal(uri, (await updateTask)["params"]!["uri"]!.GetValue<string>());

                var changeTask = WaitForNotificationAsync(sseReader, "notifications/resources/list_changed", sseTimeout.Token);
                catalogVersion = 2;
                await updates.Writer.WriteAsync("""{"jsonrpc":"2.0","method":"notifications/resources/list_changed"}""");
                await changeTask;
                list = await RequestAsync(client, user.SpaceId, session, "resources/list");
                Assert.Contains(list["result"]!["resources"]!.AsArray(), r => r?["uri"]?.GetValue<string>() == McpContentCatalog.WrapResourceUri(server.Id.Value, "file:///new"));
                var unsubscribe = await RequestAsync(client, user.SpaceId, session, "resources/unsubscribe", new() { ["uri"] = uri });
                Assert.Null(unsubscribe["error"]);
                Assert.False(subscribed);
            }
            finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
            await getStopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            updates.Writer.TryComplete();
            await stub.StopAsync();
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = oldHeartbeat;
            SpaceMcpAggregatorGrain.ReconcileInterval = oldReconcile;
        }
    }

    private static async Task<JsonObject> WaitForNotificationAsync(StreamReader reader, string method, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var message = JsonNode.Parse(line[6..])!.AsObject();
            if (message["method"]?.GetValue<string>() == method) return message;
        }
        throw new InvalidOperationException("Downstream SSE ended before the notification.");
    }
}
