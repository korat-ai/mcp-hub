using System.Text.Json.Nodes;
using Korat.Cloud.Mcp.Space;

namespace Korat.Auth.Tests.SpaceMcp;

public sealed class McpContentCatalogTests
{
    [Fact]
    public void EmptyCatalog_StillListsTheBuiltInGuide()
    {
        var catalog = new McpContentCatalog();

        var resources = Parse(catalog.ResourcesListJson())["resources"]!.AsArray();
        var templates = Parse(catalog.ResourceTemplatesListJson())["resourceTemplates"]!.AsArray();
        var prompts = Parse(catalog.PromptsListJson())["prompts"]!.AsArray();

        Assert.Single(resources);
        Assert.Equal(KoratGuide.ResourceUri, resources[0]!["uri"]!.GetValue<string>());
        Assert.Empty(templates);
        Assert.Single(prompts);
        Assert.Equal(KoratGuide.PromptName, prompts[0]!["name"]!.GetValue<string>());
        Assert.False(catalog.TryResolveResource(KoratGuide.ResourceUri, out var resourceRoute));
        Assert.Null(resourceRoute);
        Assert.False(catalog.TryResolvePrompt(KoratGuide.PromptName, out var promptRoute));
        Assert.Null(promptRoute);
    }

    [Fact]
    public void SameResourceUri_OnTwoServers_RoutesToTheSelectedServerAndRoundTripsExactly()
    {
        const string original = "https://example.test/r%C3%A9sum%C3%A9?q=100%25#caf%C3%A9";
        var catalog = new McpContentCatalog();
        var firstResource = Resource(original, "One");
        catalog.SetServer("server/one", "one", [firstResource], [], []);
        catalog.SetServer("server-雪", "two", [Resource(original, "Two")], [], []);
        firstResource["title"] = "Mutated after registration";

        var firstUri = McpContentCatalog.WrapResourceUri("server/one", original);
        var secondUri = McpContentCatalog.WrapResourceUri("server-雪", original);
        var resources = Parse(catalog.ResourcesListJson())["resources"]!.AsArray();

        Assert.Contains(resources, item => item?["uri"]?.GetValue<string>() == firstUri);
        Assert.Contains(resources, item => item?["uri"]?.GetValue<string>() == secondUri);
        var firstDescriptor = resources.Single(item => item?["uri"]?.GetValue<string>() == firstUri);
        Assert.Equal("One title", firstDescriptor!["title"]!.GetValue<string>());
        Assert.Equal("text/plain", firstDescriptor["mimeType"]!.GetValue<string>());
        Assert.True(firstDescriptor["unknown"]!["kept"]!.GetValue<bool>());
        Assert.True(catalog.TryResolveResource(firstUri, out var first));
        Assert.Equal(new McpContentRoute("server/one", original), first);
        Assert.True(catalog.TryResolveResource(secondUri, out var second));
        Assert.Equal(new McpContentRoute("server-雪", original), second);

        const string unlisted = "custom+docs://host/a%2Fb?ключ=значение#часть";
        Assert.True(catalog.TryResolveResource(
            McpContentCatalog.WrapResourceUri("server/one", unlisted), out var arbitrary));
        Assert.Equal(new McpContentRoute("server/one", unlisted), arbitrary);
        Assert.False(catalog.TryResolveResource(original, out _));
        Assert.False(catalog.TryResolveResource(
            McpContentCatalog.WrapResourceUri("not-registered", original), out _));
        Assert.False(catalog.TryResolveResource(
            McpContentCatalog.WrapResourceUri("server/one", "relative/path"), out _));
    }

    [Fact]
    public void ResourceTemplates_PreserveRfc6570ExpressionsQueryFragmentPercentAndUnicode()
    {
        const string template = "https://example.test/{path}{?q,language}#résumé%25";
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv-templates", "docs", [],
        [
            new JsonObject
            {
                ["uriTemplate"] = template,
                ["name"] = "Search",
                ["description"] = "Search documentation",
                ["custom"] = new JsonObject { ["kept"] = true },
            },
        ], []);

        var descriptor = Parse(catalog.ResourceTemplatesListJson())["resourceTemplates"]![0]!;

        Assert.Equal(McpContentCatalog.WrapResourceUri("srv-templates", template),
            descriptor["uriTemplate"]!.GetValue<string>());
        Assert.True(descriptor["custom"]!["kept"]!.GetValue<bool>());
    }

    [Fact]
    public void RemoveRevokeAndClear_EvictBackendRoutesButKeepBuiltIns()
    {
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv-1", "one", [Resource("file:///one.txt", "One")], [], [Prompt("ask")]);
        catalog.SetServer("srv-2", "two", [Resource("file:///two.txt", "Two")], [], [Prompt("ask")]);
        var oneUri = McpContentCatalog.WrapResourceUri("srv-1", "file:///one.txt");
        var twoUri = McpContentCatalog.WrapResourceUri("srv-2", "file:///two.txt");

        catalog.RemoveServer("srv-1");

        Assert.False(catalog.TryResolveResource(oneUri, out _));
        Assert.False(catalog.TryResolvePrompt("one__ask", out _));
        Assert.True(catalog.TryResolveResource(twoUri, out _));
        Assert.True(catalog.TryResolvePrompt("two__ask", out _));

        catalog.RemoveExcept(new HashSet<string>(StringComparer.Ordinal));
        Assert.False(catalog.TryResolveResource(twoUri, out _));
        Assert.False(catalog.TryResolvePrompt("two__ask", out _));

        catalog.SetServer("srv-3", "three", [Resource("file:///three.txt", "Three")], [], [Prompt("ask")]);
        catalog.Clear();

        Assert.Single(Parse(catalog.ResourcesListJson())["resources"]!.AsArray());
        Assert.Single(Parse(catalog.PromptsListJson())["prompts"]!.AsArray());
    }

    [Fact]
    public void DynamicBaseTemplate_RoutesCompletionButNotAnUnexpandedRead_AndIsRevoked()
    {
        const string template = "{+base}/docs/{path}";
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv", "docs", [], [new JsonObject { ["uriTemplate"] = template, ["name"] = "Docs" }], []);
        var wrapped = McpContentCatalog.WrapResourceUri("srv", template);
        Assert.True(catalog.TryResolveResourceTemplate(wrapped, out var route));
        Assert.Equal(new McpContentRoute("srv", template), route);
        Assert.False(catalog.TryResolveResource(wrapped, out _));
        Assert.False(catalog.TryResolveResourceTemplate(McpContentCatalog.WrapResourceUri("other", template), out _));
        catalog.RemoveServer("srv");
        Assert.False(catalog.TryResolveResourceTemplate(wrapped, out _));
    }

    [Fact]
    public void BinaryResourcesAndPromptMedia_ArePreservedAlongsideRewrittenUris()
    {
        var original = Parse("""
            {"contents":[{"uri":"file:///image.png","mimeType":"image/png","blob":"AAECAwQ=","_meta":{"source":"original"}}],
             "messages":[{"role":"user","content":{"type":"image","mimeType":"image/png","data":"AAECAwQ="}},
                         {"role":"assistant","content":{"type":"audio","mimeType":"audio/wav","data":"BQYHCA=="}},
                         {"role":"user","content":{"type":"resource","resource":{"uri":"custom://binary","mimeType":"application/octet-stream","blob":"CQoL"}}}]}
            """);
        var result = McpContentCatalog.RewriteResult("source", original);
        Assert.Equal(McpContentCatalog.WrapResourceUri("source", "file:///image.png"), result["contents"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("AAECAwQ=", result["contents"]![0]!["blob"]!.GetValue<string>());
        Assert.Equal("image/png", result["contents"]![0]!["mimeType"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original["contents"]![0]!["_meta"], result["contents"]![0]!["_meta"]));
        Assert.True(JsonNode.DeepEquals(original["messages"]![0], result["messages"]![0]));
        Assert.True(JsonNode.DeepEquals(original["messages"]![1], result["messages"]![1]));
        Assert.Equal(McpContentCatalog.WrapResourceUri("source", "custom://binary"), result["messages"]![2]!["content"]!["resource"]!["uri"]!.GetValue<string>());
        Assert.Equal("CQoL", result["messages"]![2]!["content"]!["resource"]!["blob"]!.GetValue<string>());
    }

    [Fact]
    public void ReservedGuideIdentifiers_CannotBeReplacedByBackendContent()
    {
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv-guide", "backend",
            [Resource(KoratGuide.ResourceUri, "Backend guide")], [], [Prompt(KoratGuide.PromptName)]);

        var resources = Parse(catalog.ResourcesListJson())["resources"]!.AsArray();
        var prompts = Parse(catalog.PromptsListJson())["prompts"]!.AsArray();

        Assert.Single(resources, item => item?["uri"]?.GetValue<string>() == KoratGuide.ResourceUri);
        Assert.Contains(resources, item => item?["uri"]?.GetValue<string>() ==
            McpContentCatalog.WrapResourceUri("srv-guide", KoratGuide.ResourceUri));
        Assert.Single(prompts, item => item?["name"]?.GetValue<string>() == KoratGuide.PromptName);
        Assert.Contains(prompts, item => item?["name"]?.GetValue<string>() == "backend__korat-guide");
        Assert.True(catalog.TryResolvePrompt("backend__korat-guide", out var route));
        Assert.Equal(new McpContentRoute("srv-guide", KoratGuide.PromptName), route);
    }

    [Fact]
    public void PromptDescriptors_AreNamespacedClonedAndPreserveAllMetadata()
    {
        var source = new JsonObject
        {
            ["name"] = "review",
            ["title"] = "Review a document",
            ["description"] = "Review content",
            ["arguments"] = new JsonArray
            {
                new JsonObject { ["name"] = "topic", ["required"] = true, ["unknown"] = 7 },
            },
            ["icons"] = new JsonArray
            {
                new JsonObject { ["src"] = "data:image/svg+xml,icon", ["mimeType"] = "image/svg+xml" },
            },
            ["annotations"] = new JsonObject { ["audience"] = new JsonArray("user") },
            ["vendorExtension"] = new JsonObject { ["enabled"] = true },
        };
        var expected = (JsonObject)source.DeepClone();
        expected["name"] = "docs__review";
        var catalog = new McpContentCatalog();

        catalog.SetServer("srv-docs", "docs", [], [], [source]);
        source["title"] = "Mutated after registration";

        var descriptor = Parse(catalog.PromptsListJson())["prompts"]!.AsArray()
            .Single(item => item?["name"]?.GetValue<string>() == "docs__review");
        Assert.True(JsonNode.DeepEquals(expected, descriptor));
        Assert.True(catalog.TryResolvePrompt("docs__review", out var route));
        Assert.Equal(new McpContentRoute("srv-docs", "review"), route);
    }

    [Fact]
    public void DuplicatePromptRoute_FailsClearlyAndLeavesExistingCatalogUnchanged()
    {
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv-1", "same", [], [], [Prompt("review")]);
        var before = catalog.PromptsListJson();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            catalog.SetServer("srv-2", "same", [], [], [Prompt("review")]));

        Assert.Contains("same__review", exception.Message);
        Assert.Equal(before, catalog.PromptsListJson());
        Assert.True(catalog.TryResolvePrompt("same__review", out var route));
        Assert.Equal("srv-1", route!.ServerId);
    }

    [Fact]
    public void SetServer_ReplacesThatServersDescriptorsAndPromptRoutes()
    {
        var catalog = new McpContentCatalog();
        catalog.SetServer("srv", "old", [Resource("file:///old.txt", "Old")], [], [Prompt("first")]);

        catalog.SetServer("srv", "new", [Resource("file:///new.txt", "New")], [], [Prompt("second")]);

        var resources = Parse(catalog.ResourcesListJson())["resources"]!.AsArray();
        Assert.DoesNotContain(resources, item => item?["name"]?.GetValue<string>() == "Old");
        Assert.Contains(resources, item => item?["name"]?.GetValue<string>() == "New");
        Assert.False(catalog.TryResolvePrompt("old__first", out _));
        Assert.True(catalog.TryResolvePrompt("new__second", out var route));
        Assert.Equal(new McpContentRoute("srv", "second"), route);
    }

    [Fact]
    public void ListOutputs_AreDeterministicRegardlessOfServerRegistrationOrder()
    {
        var forward = new McpContentCatalog();
        var reverse = new McpContentCatalog();

        forward.SetServer("srv-a", "alpha", [Resource("file:///z", "Z")],
            [Template("file:///{name}", "Template Z")], [Prompt("z")]);
        forward.SetServer("srv-b", "beta", [Resource("file:///a", "A")],
            [Template("https://example.test/{id}", "Template A")], [Prompt("a")]);
        reverse.SetServer("srv-b", "beta", [Resource("file:///a", "A")],
            [Template("https://example.test/{id}", "Template A")], [Prompt("a")]);
        reverse.SetServer("srv-a", "alpha", [Resource("file:///z", "Z")],
            [Template("file:///{name}", "Template Z")], [Prompt("z")]);

        Assert.Equal(forward.ResourcesListJson(), reverse.ResourcesListJson());
        Assert.Equal(forward.ResourceTemplatesListJson(), reverse.ResourceTemplatesListJson());
        Assert.Equal(forward.PromptsListJson(), reverse.PromptsListJson());
    }

    [Fact]
    public void RewriteResult_RewritesOnlyProtocolDefinedResourceLocationsWithoutMutatingInput()
    {
        var input = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject { ["uri"] = "file:///read.txt", ["text"] = "read" },
            },
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "resource",
                    ["resource"] = new JsonObject
                    {
                        ["uri"] = "file:///embedded.txt",
                        ["text"] = "embedded",
                        ["_meta"] = new JsonObject { ["uri"] = "leave-meta-alone" },
                    },
                },
                new JsonObject
                {
                    ["type"] = "resource_link",
                    ["uri"] = "https://example.test/linked?q=1#part",
                    ["name"] = "Linked",
                    ["_meta"] = new JsonObject { ["uri"] = "leave-link-meta-alone" },
                },
                new JsonObject { ["type"] = "text", ["text"] = "uri=file:///not-a-resource", ["uri"] = "leave-text-alone" },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "resource",
                        ["resource"] = new JsonObject { ["uri"] = "file:///prompt.txt", ["text"] = "prompt" },
                    },
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "resource_link", ["uri"] = "https://example.test/prompt-link" },
                    },
                },
            },
            ["structuredContent"] = new JsonObject { ["uri"] = "leave-structured-alone" },
            ["_meta"] = new JsonObject { ["uri"] = "leave-root-meta-alone" },
        };

        var rewritten = McpContentCatalog.RewriteResult("srv/rewrite", input);
        var wrap = (string uri) => McpContentCatalog.WrapResourceUri("srv/rewrite", uri);

        Assert.NotSame(input, rewritten);
        Assert.Equal("file:///read.txt", input["contents"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal(wrap("file:///read.txt"), rewritten["contents"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal(wrap("file:///embedded.txt"), rewritten["content"]![0]!["resource"]!["uri"]!.GetValue<string>());
        Assert.Equal("https://example.test/linked?q=1#part", rewritten["content"]![1]!["uri"]!.GetValue<string>());
        Assert.Equal(wrap("file:///prompt.txt"), rewritten["messages"]![0]!["content"]!["resource"]!["uri"]!.GetValue<string>());
        Assert.Equal("https://example.test/prompt-link", rewritten["messages"]![1]!["content"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("leave-text-alone", rewritten["content"]![2]!["uri"]!.GetValue<string>());
        Assert.Equal("leave-meta-alone", rewritten["content"]![0]!["resource"]!["_meta"]!["uri"]!.GetValue<string>());
        Assert.Equal("leave-link-meta-alone", rewritten["content"]![1]!["_meta"]!["uri"]!.GetValue<string>());
        Assert.Equal("leave-structured-alone", rewritten["structuredContent"]!["uri"]!.GetValue<string>());
        Assert.Equal("leave-root-meta-alone", rewritten["_meta"]!["uri"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://example.test/download")]
    [InlineData("https://example.test/r%C3%A9sum%C3%A9?q=a%2Fb%20c#caf%C3%A9")]
    [InlineData("HtTp://example.test/mixed-case")]
    [InlineData("HTTPS://example.test/upper-case")]
    public void RewriteResult_PreservesDirectHttpResourceLinksVerbatim(string uri)
    {
        var input = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "resource_link", ["uri"] = uri, ["name"] = "Download" },
            },
        };

        var rewritten = McpContentCatalog.RewriteResult("srv", input);

        Assert.Equal(uri, rewritten["content"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal(uri, input["content"]![0]!["uri"]!.GetValue<string>());
    }

    [Fact]
    public void RewriteResult_WrapsCustomResourceLinksWithoutReplacingArbitraryText()
    {
        const string customUri = "custom+docs://host/a%2Fb?q=100%25#part";
        const string text = "Download https://example.test/direct?q=1#part";
        var input = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "resource_link", ["uri"] = customUri, ["name"] = "Managed" },
                new JsonObject { ["type"] = "text", ["text"] = text, ["uri"] = "https://example.test/opaque" },
            },
        };

        var rewritten = McpContentCatalog.RewriteResult("srv", input);

        Assert.Equal(McpContentCatalog.WrapResourceUri("srv", customUri),
            rewritten["content"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal(text, rewritten["content"]![1]!["text"]!.GetValue<string>());
        Assert.Equal("https://example.test/opaque", rewritten["content"]![1]!["uri"]!.GetValue<string>());
    }

    [Fact]
    public void GuideHelpers_ReturnUsableEnglishMcpPayloadsAndValidateGoal()
    {
        var descriptor = KoratGuide.ResourceDescriptor();
        var promptDescriptor = KoratGuide.PromptDescriptor();
        var read = KoratGuide.ReadResource();
        var prompt = KoratGuide.GetPrompt(new JsonObject { ["goal"] = "Find project docs" });

        Assert.Equal(KoratGuide.ResourceUri, descriptor["uri"]!.GetValue<string>());
        Assert.Equal(KoratGuide.PromptName, promptDescriptor["name"]!.GetValue<string>());
        Assert.Equal(KoratGuide.ResourceUri, read["contents"]![0]!["uri"]!.GetValue<string>());
        var guide = read["contents"]![0]!["text"]!.GetValue<string>().ToLowerInvariant();
        Assert.Contains("oauth", guide);
        Assert.Contains("grant", guide);
        Assert.Contains("resources/templates/list", guide);
        Assert.Contains("request-access", guide);
        Assert.Contains("local filesystem", guide);
        Assert.Contains("os sandbox", guide);

        var messages = prompt["messages"]!.AsArray();
        Assert.All(messages, message => Assert.Equal("user", message!["role"]!.GetValue<string>()));
        Assert.Contains("Find project docs", messages[0]!["content"]!["text"]!.GetValue<string>());
        Assert.Contains(messages, message => message?["content"]?["type"]?.GetValue<string>() == "resource");
        Assert.Throws<ArgumentException>(() =>
            KoratGuide.GetPrompt(new JsonObject { ["goal"] = new JsonArray("not", "a", "string") }));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Resource(string uri, string name) => new()
    {
        ["uri"] = uri,
        ["name"] = name,
        ["title"] = $"{name} title",
        ["mimeType"] = "text/plain",
        ["annotations"] = new JsonObject { ["audience"] = new JsonArray("user") },
        ["icons"] = new JsonArray(new JsonObject { ["src"] = "data:image/svg+xml,icon" }),
        ["unknown"] = new JsonObject { ["kept"] = true },
    };

    private static JsonObject Template(string uriTemplate, string name) => new()
    {
        ["uriTemplate"] = uriTemplate,
        ["name"] = name,
    };

    private static JsonObject Prompt(string name) => new()
    {
        ["name"] = name,
        ["description"] = $"Prompt {name}",
    };
}
