using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpContentTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ResourceOnlyBackend_ListsAllPages_ReadsTemplates_AndRoutesPromptArguments()
    {
        var user = await fixture.SeedUserAsync($"content-relay-{Guid.NewGuid():N}@example.com", "Content");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Docs", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
        var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(access.Id, user.UserId);
        var publisherToken = await fixture.IssueCliTokenAsync(user.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value, publisherToken, []);
        publisher.Capabilities = new() { ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = true }, ["prompts"] = new JsonObject { ["listChanged"] = true }, ["completions"] = new JsonObject() };
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Method, string Params)>();
        publisher.RequestHandler = (method, p) =>
        {
            seen.Enqueue((method, p?.ToJsonString() ?? "{}"));
            JsonNode? result = method switch
            {
                "resources/list" when p?["cursor"] is null => JsonNode.Parse("""{"resources":[{"uri":"file:///one","name":"one","mimeType":"text/plain","annotations":{"priority":0.8}}],"nextCursor":"page2"}"""),
                "resources/list" => JsonNode.Parse("""{"resources":[{"uri":"file:///two","name":"two"}]}"""),
                "resources/templates/list" => JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"file:///docs/{path}{?lang}","name":"Docs"},{"uriTemplate":"{+base}/docs/{path}","name":"Dynamic root"}]}"""),
                "prompts/list" => JsonNode.Parse("""{"prompts":[{"name":"review","description":"Review","arguments":[{"name":"code","required":true}]}]}"""),
                "resources/read" => new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = p!["uri"]!.DeepClone(), ["text"] = "local contents" }) },
                "prompts/get" => new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "resource", ["resource"] = new JsonObject { ["uri"] = "file:///one", ["text"] = p!["arguments"]!["code"]!.DeepClone() } } }) },
                "completion/complete" => JsonNode.Parse("""{"completion":{"values":["intro"],"total":1,"hasMore":false}}"""),
                _ => null
            };
            return result is null ? null : new JsonObject { ["result"] = result };
        };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var (session, _) = await InitializeAsync(client, user.SpaceId);
        try
        {
            var prefix = $"korat://servers/{server.Id.Value}/";
            var listed = await RequestAsync(client, user.SpaceId, session, "resources/list");
            var resources = listed["result"]!["resources"]!.AsArray();
            Assert.Contains(resources, r => r?["uri"]?.GetValue<string>() == prefix + "file:///one");
            Assert.Contains(resources, r => r?["uri"]?.GetValue<string>() == prefix + "file:///two");
            Assert.Contains(seen, r => r.Method == "resources/list" && JsonNode.Parse(r.Params)?["cursor"]?.GetValue<string>() == "page2");
            Assert.DoesNotContain(seen, r => r.Method == "tools/list");
            var templates = await RequestAsync(client, user.SpaceId, session, "resources/templates/list");
            Assert.Equal(prefix + "file:///docs/{path}{?lang}", templates["result"]!["resourceTemplates"]![0]!["uriTemplate"]!.GetValue<string>());
            var read = await RequestAsync(client, user.SpaceId, session, "resources/read", new() { ["uri"] = prefix + "file:///docs/intro?lang=en" });
            Assert.Equal("local contents", read["result"]!["contents"]![0]!["text"]!.GetValue<string>());
            Assert.Equal(prefix + "file:///docs/intro?lang=en", read["result"]!["contents"]![0]!["uri"]!.GetValue<string>());
            Assert.Contains(seen, r => r.Method == "resources/read" && JsonNode.Parse(r.Params)?["uri"]?.GetValue<string>() == "file:///docs/intro?lang=en");
            var prompt = await RequestAsync(client, user.SpaceId, session, "prompts/get", new() { ["name"] = "docs__review", ["arguments"] = new JsonObject { ["code"] = "hello" } });
            Assert.Equal("hello", prompt["result"]!["messages"]![0]!["content"]!["resource"]!["text"]!.GetValue<string>());
            Assert.Equal(prefix + "file:///one", prompt["result"]!["messages"]![0]!["content"]!["resource"]!["uri"]!.GetValue<string>());
            var completion = await RequestAsync(client, user.SpaceId, session, "completion/complete", new() { ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = prefix + "file:///docs/{path}{?lang}" }, ["argument"] = new JsonObject { ["name"] = "path", ["value"] = "in" } });
            Assert.Equal("intro", completion["result"]!["completion"]!["values"]![0]!.GetValue<string>());
            var dynamicCompletion = await RequestAsync(client, user.SpaceId, session, "completion/complete", new() { ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = prefix + "{+base}/docs/{path}" }, ["argument"] = new JsonObject { ["name"] = "path", ["value"] = "in" } });
            Assert.Null(dynamicCompletion["error"]);
            Assert.Equal("intro", dynamicCompletion["result"]!["completion"]!["values"]![0]!.GetValue<string>());
            var resourceCompletion = await RequestAsync(client, user.SpaceId, session, "completion/complete", new() { ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = prefix + "file:///one" }, ["argument"] = new JsonObject { ["name"] = "path", ["value"] = "in" } });
            Assert.Null(resourceCompletion["error"]);
            Assert.Equal("intro", resourceCompletion["result"]!["completion"]!["values"]![0]!.GetValue<string>());
            Assert.Contains(seen, r => r.Method == "completion/complete"
                && JsonNode.Parse(r.Params)?["ref"]?["uri"]?.GetValue<string>() == "file:///one");
        }
        finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
    }

    [Fact]
    public async Task EmptySpace_OffersReadableGuideAndUsablePromptThroughHttp()
    {
        var user = await fixture.SeedUserAsync($"content-{Guid.NewGuid():N}@example.com", "Content");
        var (token, _) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var (session, init) = await InitializeAsync(client, user.SpaceId);
        try
        {
            Assert.NotNull(init["result"]?["capabilities"]?["resources"]);
            Assert.NotNull(init["result"]?["capabilities"]?["prompts"]);
            var resources = await RequestAsync(client, user.SpaceId, session, "resources/list");
            Assert.Contains(resources["result"]!["resources"]!.AsArray(), r => r?["uri"]?.GetValue<string>() == "korat://help/usage");
            var read = await RequestAsync(client, user.SpaceId, session, "resources/read", new() { ["uri"] = "korat://help/usage" });
            Assert.NotEmpty(read["result"]!["contents"]![0]!["text"]!.GetValue<string>());
            var prompts = await RequestAsync(client, user.SpaceId, session, "prompts/list");
            Assert.Contains(prompts["result"]!["prompts"]!.AsArray(), p => p?["name"]?.GetValue<string>() == "korat-guide");
            var prompt = await RequestAsync(client, user.SpaceId, session, "prompts/get", new() { ["name"] = "korat-guide", ["arguments"] = new JsonObject { ["goal"] = "Find project docs" } });
            Assert.NotEmpty(prompt["result"]!["messages"]!.AsArray());
            Assert.Equal("user", prompt["result"]!["messages"]![0]!["role"]!.GetValue<string>());
            var missing = await RequestAsync(client, user.SpaceId, session, "resources/read", new() { ["uri"] = "file:///not-on-the-hub" });
            Assert.NotNull(missing["error"]);
        }
        finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
    }

    [Fact]
    public async Task RepeatedBackendCursor_DoesNotHangOrExposeAPartialCatalog()
    {
        var user = await fixture.SeedUserAsync($"bad-cursor-{Guid.NewGuid():N}@example.com", "Content");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Loop", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
        var access = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(access.Id, user.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(fixture.Factory, node.Value,
            await fixture.IssueCliTokenAsync(user.UserId), []);
        publisher.Capabilities = new() { ["resources"] = new JsonObject() };
        var pages = 0;
        publisher.RequestHandler = (method, _) =>
        {
            if (method != "resources/list") return null;
            Interlocked.Increment(ref pages);
            return JsonNode.Parse("""{"result":{"resources":[{"uri":"file:///partial","name":"Partial"}],"nextCursor":"loop"}}""")!.AsObject();
        };
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var (session, _) = await InitializeAsync(client, user.SpaceId).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(2, pages);
            var list = await RequestAsync(client, user.SpaceId, session, "resources/list");
            Assert.Single(list["result"]!["resources"]!.AsArray()); // Only Korat's guide, no partial page.
            Assert.True(await publisher.WaitForCloseSessionAsync(Assert.Single(publisher.SeenSessionIds), TimeSpan.FromSeconds(3)));
        }
        finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
    }

    [Theory]
    [InlineData("resources/read", "{\"uri\":123}")]
    [InlineData("prompts/get", "{\"name\":\"korat-guide\",\"arguments\":{\"goal\":false}}")]
    [InlineData("completion/complete", "{\"ref\":{\"type\":\"bad\"},\"argument\":{\"name\":\"x\",\"value\":\"a\"}}")]
    [InlineData("completion/complete", "{\"ref\":{\"type\":\"ref/prompt\",\"name\":\"korat-guide\"},\"argument\":{\"name\":\"goal\",\"value\":\"a\"},\"context\":false}")]
    [InlineData("resources/subscribe", "{\"uri\":false}")]
    [InlineData("resources/list", "{\"cursor\":\"foreign\"}")]
    [InlineData("prompts/list", "{\"cursor\":42}")]
    public async Task InvalidContentParameters_ReturnInvalidParams_NotInternalError(string method, string parameters)
    {
        var user = await fixture.SeedUserAsync($"content-params-{Guid.NewGuid():N}@example.com", "Content");
        var (token, _) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, user.UserId, user.SpaceId);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var (session, _) = await InitializeAsync(client, user.SpaceId);
        try
        {
            var response = await RequestAsync(client, user.SpaceId, session, method, JsonNode.Parse(parameters)!.AsObject());
            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        }
        finally { await client.DeleteAsync($"/mcp/{user.SpaceId}"); }
    }

    internal static async Task<(string Session, JsonObject Result)> InitializeAsync(HttpClient client, string space)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{space}")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"content-test","version":"1"}}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var session = response.Headers.GetValues("Mcp-Session-Id").Single();
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", session);
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
        return (session, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    internal static async Task<JsonObject> RequestAsync(HttpClient client, string space, string session, string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 22, ["method"] = method, ["params"] = parameters };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{space}")
        { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
}
