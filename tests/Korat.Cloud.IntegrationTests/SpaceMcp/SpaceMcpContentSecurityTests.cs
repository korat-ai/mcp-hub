using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Gateways;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpContentSecurityTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task SameResourceUriAndPromptName_OnTwoBackends_RouteIndependently()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-routes-{Guid.NewGuid():N}@example.com", "Space MCP Content Routes");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var nodeA = NodeId.New();
        var nodeB = NodeId.New();
        var serverA = (await space.PublishMcpServerAsync(nodeA, "Docs Alpha", "stdio", "demo"))!;
        var serverB = (await space.PublishMcpServerAsync(nodeB, "Docs Beta", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);

        var requestA = await space.CreateAccessRequestAsync(consumer, serverA.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(requestA.Id, seeded.UserId);
        var requestB = await space.CreateAccessRequestAsync(consumer, serverB.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(requestB.Id, seeded.UserId);

        var forwardedA = new ConcurrentQueue<(string Method, JsonObject? Params)>();
        var forwardedB = new ConcurrentQueue<(string Method, JsonObject? Params)>();
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisherA = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, nodeA.Value, publisherToken, tools: []);
        await using var publisherB = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, nodeB.Value, publisherToken, tools: []);
        ConfigureContentPublisher(publisherA, "alpha", forwardedA);
        ConfigureContentPublisher(publisherB, "beta", forwardedB);

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        try
        {
            var resources = (await RequestAsync(client, seeded.SpaceId, "resources/list"))["result"]!["resources"]!.AsArray();
            var prompts = (await RequestAsync(client, seeded.SpaceId, "prompts/list"))["result"]!["prompts"]!.AsArray();
            var prefixA = ServerPrefix(serverA.Id.Value);
            var prefixB = ServerPrefix(serverB.Id.Value);
            var wrappedA = prefixA + SharedResourceUri;
            var wrappedB = prefixB + SharedResourceUri;
            var promptA = ToolNamespacer.Slug(serverA.DisplayName, serverA.Id.Value) + "__review";
            var promptB = ToolNamespacer.Slug(serverB.DisplayName, serverB.Id.Value) + "__review";

            Assert.Contains(resources, item => item!["uri"]!.GetValue<string>() == wrappedA);
            Assert.Contains(resources, item => item!["uri"]!.GetValue<string>() == wrappedB);
            Assert.Contains(prompts, item => item!["name"]!.GetValue<string>() == promptA);
            Assert.Contains(prompts, item => item!["name"]!.GetValue<string>() == promptB);

            var readA = await RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedA });
            var readB = await RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedB });
            Assert.Equal("alpha", readA["result"]!["contents"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("beta", readB["result"]!["contents"]![0]!["text"]!.GetValue<string>());

            var getA = await RequestAsync(client, seeded.SpaceId, "prompts/get",
                new JsonObject { ["name"] = promptA, ["arguments"] = new JsonObject { ["goal"] = "A" } });
            var getB = await RequestAsync(client, seeded.SpaceId, "prompts/get",
                new JsonObject { ["name"] = promptB, ["arguments"] = new JsonObject { ["goal"] = "B" } });
            Assert.Equal("alpha", getA["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());
            Assert.Equal("beta", getB["result"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());

            Assert.Contains(forwardedA, call =>
                call.Method == "resources/read" && call.Params?["uri"]?.GetValue<string>() == SharedResourceUri);
            Assert.Contains(forwardedB, call =>
                call.Method == "resources/read" && call.Params?["uri"]?.GetValue<string>() == SharedResourceUri);
            Assert.Contains(forwardedA, call =>
                call.Method == "prompts/get" && call.Params?["name"]?.GetValue<string>() == "review");
            Assert.Contains(forwardedB, call =>
                call.Method == "prompts/get" && call.Params?["name"]?.GetValue<string>() == "review");
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    [Fact]
    public async Task UngrantedAndForeignServerWrappers_AreDeniedWithoutDialingPublishers()
    {
        var local = await fixture.SeedUserAsync(
            $"space-mcp-content-local-{Guid.NewGuid():N}@example.com", "Space MCP Content Local");
        var foreign = await fixture.SeedUserAsync(
            $"space-mcp-content-foreign-{Guid.NewGuid():N}@example.com", "Space MCP Content Foreign");
        var localSpace = fixture.ClusterClient.GetGrain<ISpaceGrain>(local.SpaceId);
        var foreignSpace = fixture.ClusterClient.GetGrain<ISpaceGrain>(foreign.SpaceId);
        var ungrantedNode = NodeId.New();
        var foreignNode = NodeId.New();
        var ungrantedServer = (await localSpace.PublishMcpServerAsync(
            ungrantedNode, "Ungranted Docs", "stdio", "demo"))!;
        var foreignServer = (await foreignSpace.PublishMcpServerAsync(
            foreignNode, "Foreign Docs", "stdio", "demo"))!;
        var (token, _) = await SpaceMcpOAuthTestAccess.IssueAsync(fixture, local.UserId, local.SpaceId);

        var ungrantedToken = await fixture.IssueCliTokenAsync(local.UserId);
        var foreignToken = await fixture.IssueCliTokenAsync(foreign.UserId);
        await using var ungrantedPublisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, ungrantedNode.Value, ungrantedToken, tools: []);
        await using var foreignPublisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, foreignNode.Value, foreignToken, tools: []);
        ConfigureContentPublisher(ungrantedPublisher, "ungranted");
        ConfigureContentPublisher(foreignPublisher, "foreign");

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, local.SpaceId);

        try
        {
            Assert.Empty(ungrantedPublisher.SeenSessionIds);
            Assert.Empty(foreignPublisher.SeenSessionIds);

            var ungranted = await RequestAsync(client, local.SpaceId, "resources/read", new JsonObject
            {
                ["uri"] = ServerPrefix(ungrantedServer.Id.Value) + "file:///private",
            });
            var foreignResult = await RequestAsync(client, local.SpaceId, "resources/read", new JsonObject
            {
                ["uri"] = ServerPrefix(foreignServer.Id.Value) + "file:///private",
            });

            Assert.Equal(-32002, ungranted["error"]!["code"]!.GetValue<int>());
            Assert.Equal(-32002, foreignResult["error"]!["code"]!.GetValue<int>());
            Assert.Empty(ungrantedPublisher.SeenSessionIds);
            Assert.Empty(foreignPublisher.SeenSessionIds);
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{local.SpaceId}");
        }
    }

    [Fact]
    public async Task GrantRevokedAfterInitialize_RemovesResourcesAndPromptsAndRejectsOldRoutes()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-revoke-{Guid.NewGuid():N}@example.com", "Space MCP Content Revoke");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Revocable Docs", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        var grant = await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var forwarded = new ConcurrentQueue<(string Method, JsonObject? Params)>();
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, node.Value, publisherToken, tools: []);
        ConfigureContentPublisher(publisher, "revocable", forwarded);

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        var prefix = ServerPrefix(server.Id.Value);
        var wrappedUri = prefix + SharedResourceUri;
        var promptName = ToolNamespacer.Slug(server.DisplayName, server.Id.Value) + "__review";

        try
        {
            var resourcesBefore = (await RequestAsync(client, seeded.SpaceId, "resources/list"))["result"]!["resources"]!.AsArray();
            var promptsBefore = (await RequestAsync(client, seeded.SpaceId, "prompts/list"))["result"]!["prompts"]!.AsArray();
            Assert.Contains(resourcesBefore, item => item!["uri"]!.GetValue<string>() == wrappedUri);
            Assert.Contains(promptsBefore, item => item!["name"]!.GetValue<string>() == promptName);

            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();
                var affected = await space.RevokeGrantAsync(grant.Id, seeded.UserId);
                foreach (var sessionId in affected)
                    await terminator.TerminateSessionAsync(sessionId, SessionCloseReason.Revoked, default);
            }

            var backendCallsAfterRevoke = forwarded.Count;
            var resourcesAfter = (await RequestAsync(client, seeded.SpaceId, "resources/list"))["result"]!["resources"]!.AsArray();
            var promptsAfter = (await RequestAsync(client, seeded.SpaceId, "prompts/list"))["result"]!["prompts"]!.AsArray();
            var readAfter = await RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedUri });
            var getAfter = await RequestAsync(client, seeded.SpaceId, "prompts/get",
                new JsonObject { ["name"] = promptName });

            Assert.DoesNotContain(resourcesAfter, item => item!["uri"]!.GetValue<string>() == wrappedUri);
            Assert.DoesNotContain(promptsAfter, item => item!["name"]!.GetValue<string>() == promptName);
            Assert.Equal(-32002, readAfter["error"]!["code"]!.GetValue<int>());
            Assert.Equal(-32602, getAfter["error"]!["code"]!.GetValue<int>());
            Assert.Equal(backendCallsAfterRevoke, forwarded.Count);
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    [Fact]
    public async Task GrantRevokedWhileResourceReadIsInFlight_DiscardsBackendSuccessWithoutCloseEvent()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-inflight-revoke-{Guid.NewGuid():N}@example.com", "Space MCP In-Flight Revoke");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Slow Docs", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        var grant = await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var readEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, node.Value, publisherToken, tools: []);
        ConfigureContentPublisher(publisher, "slow-success");
        publisher.AsyncRequestHandler = async (method, parameters) =>
        {
            if (method != "resources/read") return null;
            readEntered.TrySetResult(true);
            await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = parameters!["uri"]!.GetValue<string>(),
                            ["text"] = "must not escape after revoke",
                        },
                    },
                },
            };
        };

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        try
        {
            var wrappedUri = ServerPrefix(server.Id.Value) + SharedResourceUri;
            var readTask = RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedUri });
            await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Intentionally do not invoke SessionTerminator. The backend remains alive and its
            // successful response must be rejected solely by the post-response grant recheck.
            await space.RevokeGrantAsync(grant.Id, seeded.UserId);
            var revoked = (await space.ListGrantsAsync()).Single(item => item.Id == grant.Id);
            Assert.Equal(GrantStatus.Revoked, revoked.Status);

            releaseRead.TrySetResult(true);
            var response = await readTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(-32000, response["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["result"]);
        }
        finally
        {
            releaseRead.TrySetResult(true);
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    [Fact]
    public async Task DefinitionDigestMismatchBeforeResourceRead_RejectsWithoutBackendRequest()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-stale-digest-{Guid.NewGuid():N}@example.com", "Space MCP Stale Digest");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Mutable Docs", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        var grant = await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var forwarded = new ConcurrentQueue<(string Method, JsonObject? Params)>();
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, node.Value, publisherToken, tools: []);
        ConfigureContentPublisher(publisher, "stale-digest", forwarded);

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        try
        {
            var wrappedUri = ServerPrefix(server.Id.Value) + SharedResourceUri;

            // Bypass SpaceGrain's normal redefinition path so the grant deliberately remains
            // Active while its approved digest becomes stale. This isolates the read-time guard.
            await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value)
                .UpdateCommandAsync("bash", "-c whoami");

            var stillActive = (await space.ListGrantsAsync()).Single(item => item.Id == grant.Id);
            var currentServer = await space.GetMcpServerAsync(server.Id);
            Assert.Equal(GrantStatus.Active, stillActive.Status);
            Assert.NotNull(currentServer);
            Assert.NotEqual(stillActive.ApprovedDefinitionDigest, McpServerDefinition.Digest(currentServer));
            Assert.DoesNotContain(forwarded, call => call.Method == "resources/read");

            var response = await RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedUri });

            Assert.Equal(-32000, response["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["result"]);
            Assert.DoesNotContain(forwarded, call => call.Method == "resources/read");
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    [Fact]
    public async Task BackendContentErrors_PreserveCodeMessageAndData()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-errors-{Guid.NewGuid():N}@example.com", "Space MCP Content Errors");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Error Docs", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, node.Value, publisherToken, tools: []);
        ConfigureContentPublisher(publisher, "unused", contentError: true);

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        try
        {
            var wrappedUri = ServerPrefix(server.Id.Value) + SharedResourceUri;
            var promptName = ToolNamespacer.Slug(server.DisplayName, server.Id.Value) + "__review";
            var read = await RequestAsync(client, seeded.SpaceId, "resources/read",
                new JsonObject { ["uri"] = wrappedUri });
            var get = await RequestAsync(client, seeded.SpaceId, "prompts/get",
                new JsonObject { ["name"] = promptName });

            AssertBackendError(read);
            AssertBackendError(get);
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    [Fact]
    public async Task ToolsOnlySource_PreservesDirectHttpsResourceLinkAndRewritesEmbeddedResource()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-content-tool-result-{Guid.NewGuid():N}@example.com", "Space MCP Tool Result");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var node = NodeId.New();
        var server = (await space.PublishMcpServerAsync(node, "Inspector", "stdio", "demo"))!;
        var (token, consumer) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);
        var accessRequest = await space.CreateAccessRequestAsync(consumer, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, node.Value, publisherToken, tools: [("inspect", "Inspects content", null)]);
        publisher.Capabilities = new JsonObject { ["tools"] = new JsonObject() };
        publisher.ToolCallHandler = (_, _) => new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "resource",
                    ["resource"] = new JsonObject
                    {
                        ["uri"] = "file:///embedded?q=1#part",
                        ["text"] = "embedded",
                        ["_meta"] = new JsonObject { ["uri"] = "opaque-resource-meta" },
                    },
                },
                new JsonObject
                {
                    ["type"] = "resource_link",
                    ["uri"] = "https://example.test/item?q=1#part",
                    ["name"] = "linked",
                    ["_meta"] = new JsonObject { ["uri"] = "opaque-link-meta" },
                },
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "file:///plain-text",
                    ["uri"] = "opaque-content-uri",
                },
            },
            ["structuredContent"] = new JsonObject { ["uri"] = "opaque-structured-uri" },
            ["_meta"] = new JsonObject { ["uri"] = "opaque-result-meta" },
        }.ToJsonString();

        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await InitializeAsync(client, seeded.SpaceId);

        try
        {
            var toolName = ToolNamespacer.Namespaced(
                ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "inspect");
            var response = await RequestAsync(client, seeded.SpaceId, "tools/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = new JsonObject(),
            });
            var result = response["result"]!.AsObject();
            var content = result["content"]!.AsArray();
            var prefix = ServerPrefix(server.Id.Value);

            Assert.Equal(prefix + "file:///embedded?q=1#part",
                content[0]!["resource"]!["uri"]!.GetValue<string>());
            Assert.Equal("https://example.test/item?q=1#part", content[1]!["uri"]!.GetValue<string>());
            Assert.Equal("opaque-resource-meta",
                content[0]!["resource"]!["_meta"]!["uri"]!.GetValue<string>());
            Assert.Equal("opaque-link-meta", content[1]!["_meta"]!["uri"]!.GetValue<string>());
            Assert.Equal("opaque-content-uri", content[2]!["uri"]!.GetValue<string>());
            Assert.Equal("file:///plain-text", content[2]!["text"]!.GetValue<string>());
            Assert.Equal("opaque-structured-uri", result["structuredContent"]!["uri"]!.GetValue<string>());
            Assert.Equal("opaque-result-meta", result["_meta"]!["uri"]!.GetValue<string>());
        }
        finally
        {
            await client.DeleteAsync($"/mcp/{seeded.SpaceId}");
        }
    }

    private const string SharedResourceUri = "file:///shared?q=100%25&name=%E2%98%83#section";

    private static void ConfigureContentPublisher(
        FakeMcpPublisher publisher,
        string marker,
        ConcurrentQueue<(string Method, JsonObject? Params)>? forwarded = null,
        bool contentError = false)
    {
        publisher.Capabilities = new JsonObject
        {
            ["resources"] = new JsonObject(),
            ["prompts"] = new JsonObject(),
        };
        publisher.RequestHandler = (method, parameters) =>
        {
            forwarded?.Enqueue((method, parameters?.DeepClone().AsObject()));
            JsonObject? result = method switch
            {
                "resources/list" => new JsonObject
                {
                    ["resources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = SharedResourceUri,
                            ["name"] = "shared",
                            ["title"] = $"Shared {marker}",
                        },
                    },
                },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => new JsonObject
                {
                    ["prompts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "review",
                            ["title"] = $"Review {marker}",
                            ["arguments"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "goal", ["required"] = false },
                            },
                        },
                    },
                },
                "resources/read" when contentError => null,
                "prompts/get" when contentError => null,
                "resources/read" => new JsonObject
                {
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = parameters!["uri"]!.GetValue<string>(),
                            ["text"] = marker,
                        },
                    },
                },
                "prompts/get" => new JsonObject
                {
                    ["messages"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = marker },
                        },
                    },
                },
                _ => null,
            };

            if (contentError && method is ("resources/read" or "prompts/get"))
            {
                return new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32042,
                        ["message"] = "Backend refused the content request",
                        ["data"] = new JsonObject
                        {
                            ["reason"] = "classified",
                            ["retryable"] = false,
                        },
                    },
                };
            }

            return result is null ? null : new JsonObject { ["result"] = result };
        };
    }

    private static void AssertBackendError(JsonObject envelope)
    {
        var error = envelope["error"]!.AsObject();
        Assert.Equal(-32042, error["code"]!.GetValue<int>());
        Assert.Equal("Backend refused the content request", error["message"]!.GetValue<string>());
        Assert.Equal("classified", error["data"]!["reason"]!.GetValue<string>());
        Assert.False(error["data"]!["retryable"]!.GetValue<bool>());
    }

    private static string ServerPrefix(string serverId) =>
        $"korat://servers/{Uri.EscapeDataString(serverId)}/";

    private static async Task InitializeAsync(HttpClient client, string spaceId)
    {
        var response = await PostAsync(client, spaceId, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "security-tests", ["version"] = "1.0" },
            },
        });
        response.EnsureSuccessStatusCode();
        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionId);
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
    }

    private static async Task<JsonObject> RequestAsync(
        HttpClient client,
        string spaceId,
        string method,
        JsonObject? parameters = null)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 22,
            ["method"] = method,
        };
        if (parameters is not null)
            message["params"] = parameters;

        var response = await PostAsync(client, spaceId, message);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string spaceId, JsonObject message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceId}")
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client.SendAsync(request);
    }
}
