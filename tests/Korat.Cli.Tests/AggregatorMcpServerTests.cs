using System.Net;
using System.Text.Json.Nodes;
using Korat.Cli.Auth;
using Korat.Cli.Commands;
using Korat.Cli.Mcp.Aggregation;
using Xunit;
using Korat.Mcp;

public class AggregatorMcpServerTests
{
    private sealed class FakeSessions : IBackendSessions
    {
        public List<string> Calls { get; } = new();
        public List<string> CallArguments { get; } = new();
        public List<string> AccessRequests { get; } = new();
        public string CallReturn = """{"jsonrpc":"2.0","id":99,"result":{"content":[{"type":"text","text":"ok"}]}}""";
        public AccessRequestResult AccessResult = new(false, "ar-1");

        public Task<string> CallAsync(string namespacedName, string argsJson, JsonNode idNode, CancellationToken ct)
        {
            Calls.Add(namespacedName);
            CallArguments.Add(argsJson);
            return Task.FromResult(CallReturn);
        }

        public Task<AccessRequestResult> RequestAccessAsync(string serverId, CancellationToken ct)
        {
            AccessRequests.Add(serverId);
            return Task.FromResult(AccessResult);
        }
    }

    private static AggregateCatalog CatalogWithGithub()
    {
        var cat = new AggregateCatalog();
        cat.SetGranted("s1", "github", "GitHub", new[] {
            new ToolInfo("github__create_issue", "create_issue", "github",
                (JsonObject)JsonNode.Parse("""{"type":"object"}""")!, "Create an issue") });
        cat.SetUngranted(new[] { new ServerDescriptor("s2", "Postgres", true) });
        return cat;
    }

    [Fact]
    public async Task Initialize_advertises_listChanged_and_tools_list_returns_catalog_and_call_routes()
    {
        var output = new StringWriter();
        var sessions = new FakeSessions();
        var server = new AggregatorMcpServer(CatalogWithGithub(), sessions, output, "test");

        var input = new StringReader(string.Join("\n", new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"github__create_issue","arguments":{"title":"x"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"does__not_exist","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"request-access__postgres","arguments":{}}}""",
        }) + "\n");

        await server.RunAsync(input, default);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        JsonNode ById(int id) => lines.First(o => o["id"]?.GetValue<int>() == id);

        // initialize
        var init = ById(1);
        Assert.True(init["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());

        // tools/list
        var list = ById(2);
        Assert.Contains(list["result"]!["tools"]!.AsArray(),
            t => t!["name"]!.GetValue<string>() == "github__create_issue");

        // tools/call → routed
        var call = ById(3);
        Assert.NotNull(call["result"]);
        Assert.Contains("github__create_issue", sessions.Calls);

        // unknown tool → error -32601
        var err = ById(4);
        Assert.Equal(-32601, err["error"]!["code"]!.GetValue<int>());

        // request-access → text result (not an error)
        var ra = ById(5);
        Assert.NotNull(ra["result"]);
        Assert.Contains("postgres", ra["result"]!["content"]![0]!["text"]!.GetValue<string>());

        // notifications/initialized produced NO reply (no envelope with no id besides nothing)
    }

    [Fact]
    public async Task EmitToolsListChanged_writes_notification_without_id()
    {
        var output = new StringWriter();
        var server = new AggregatorMcpServer(new AggregateCatalog(), new FakeSessions(), output, "test");
        await server.EmitToolsListChangedAsync();
        var s = output.ToString();
        Assert.Contains("notifications/tools/list_changed", s);
        Assert.DoesNotContain("\"id\"", s);
    }

    [Fact]
    public async Task Control_list_tools_filters_by_prefix_and_hides_control_tools_by_default()
    {
        var output = new StringWriter();
        var server = new AggregatorMcpServer(CatalogWithGithub(), new FakeSessions(), output, "test");
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"korat_space_list_tools","arguments":{"prefix":"github__"}}}""" + "\n");

        await server.RunAsync(input, default);

        var response = JsonNode.Parse(output.ToString().Trim())!;
        var listed = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!["tools"]!.AsArray();
        Assert.Single(listed);
        Assert.Equal("github__create_issue", listed[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(listed,
            tool => AggregateCatalog.IsControlToolName(tool!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Control_call_tool_routes_namespaced_tool_with_nested_arguments()
    {
        var output = new StringWriter();
        var sessions = new FakeSessions();
        var server = new AggregatorMcpServer(CatalogWithGithub(), sessions, output, "test");
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"korat_space_call_tool","arguments":{"name":"github__create_issue","arguments":{"title":"bug"}}}}""" + "\n");

        await server.RunAsync(input, default);

        var response = JsonNode.Parse(output.ToString().Trim())!;
        Assert.NotNull(response["result"]);
        Assert.Equal(["github__create_issue"], sessions.Calls);
        Assert.Equal("bug", JsonNode.Parse(Assert.Single(sessions.CallArguments))!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Control_call_tool_rejects_recursive_control_call()
    {
        var output = new StringWriter();
        var sessions = new FakeSessions();
        var server = new AggregatorMcpServer(CatalogWithGithub(), sessions, output, "test");
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"korat_space_call_tool","arguments":{"name":"korat_space_list_tools","arguments":{}}}}""" + "\n");

        await server.RunAsync(input, default);

        var response = JsonNode.Parse(output.ToString().Trim())!;
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Empty(sessions.Calls);
    }

    [Fact]
    public async Task Control_request_access_accepts_slug_and_routes_to_server()
    {
        var output = new StringWriter();
        var sessions = new FakeSessions();
        var server = new AggregatorMcpServer(CatalogWithGithub(), sessions, output, "test");
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"korat_space_request_access","arguments":{"target":"postgres"}}}""" + "\n");

        await server.RunAsync(input, default);

        var response = JsonNode.Parse(output.ToString().Trim())!;
        Assert.NotNull(response["result"]);
        Assert.Equal(["s2"], sessions.AccessRequests);
    }

    // Custom TextReader that throws on ReadLineAsync — simulates the cancel-wake path
    // where the underlying stdin stream is disposed (or an IOException occurs) after
    // the CancellationToken fires. Both overloads are covered so whichever one RunAsync
    // calls (the ct overload: input.ReadLineAsync(ct)) is exercised.
    private sealed class ThrowOnReadReader : TextReader
    {
        private readonly Exception _ex;
        public ThrowOnReadReader(Exception ex) => _ex = ex;
        public override Task<string?> ReadLineAsync() => throw _ex;
        // RunAsync calls input.ReadLineAsync(ct) — this is the overload that matters.
        public override ValueTask<string?> ReadLineAsync(CancellationToken ct) => throw _ex;
    }

    [Fact]
    public async Task RunAsync_treats_stdin_dispose_wake_as_clean_exit()
    {
        var output = new StringWriter();
        var server = new AggregatorMcpServer(new AggregateCatalog(), new FakeSessions(), output, "test");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // simulate Ctrl+C already fired
        var reader = new ThrowOnReadReader(new ObjectDisposedException("stdin"));
        // Should NOT throw — clean return.
        await server.RunAsync(reader, cts.Token);
    }

    [Fact]
    public async Task RunAsync_treats_io_exception_on_cancel_as_clean_exit()
    {
        var output = new StringWriter();
        var server = new AggregatorMcpServer(new AggregateCatalog(), new FakeSessions(), output, "test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new ThrowOnReadReader(new IOException("pipe closed"));
        await server.RunAsync(reader, cts.Token);
    }

    [Fact]
    public async Task Backend_error_is_forwarded_as_jsonrpc_error()
    {
        var output = new StringWriter();
        var sessions = new FakeSessions {
            CallReturn = """{"jsonrpc":"2.0","id":99,"error":{"code":-32000,"message":"boom"}}""" };
        var server = new AggregatorMcpServer(CatalogWithGithub(), sessions, output, "test");
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"github__create_issue","arguments":{}}}""" + "\n");

        await server.RunAsync(input, default);

        var line = JsonNode.Parse(output.ToString().Trim())!.AsObject();
        Assert.Equal(-32000, line["error"]!["code"]!.GetValue<int>());
    }

    private static AggregateCatalog CatalogWithUpgrade()
    {
        var cat = new AggregateCatalog();
        cat.SetUpgradeAvailable(true, "0.3.0", "0.2.8");
        return cat;
    }

    [Fact]
    public async Task Update_korat_tool_invokes_runner_and_returns_reconnect_text()
    {
        var runnerInvoked = false;
        Func<CancellationToken, Task> runner = _ => { runnerInvoked = true; return Task.CompletedTask; };

        var output = new StringWriter();
        var server = new AggregatorMcpServer(CatalogWithUpgrade(), new FakeSessions(), output, "test", runner);
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"update_korat","arguments":{}}}""" + "\n");

        await server.RunAsync(input, default);

        Assert.True(runnerInvoked, "upgrade runner should have been called");
        var line = JsonNode.Parse(output.ToString().Trim())!.AsObject();
        Assert.NotNull(line["result"]);
        var text = line["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Reconnect", text);
    }

    [Fact]
    public async Task Update_korat_tool_returns_failure_text_and_does_not_throw_when_runner_throws()
    {
        Func<CancellationToken, Task> failingRunner = _ => throw new InvalidOperationException("network error");

        var output = new StringWriter();
        var server = new AggregatorMcpServer(CatalogWithUpgrade(), new FakeSessions(), output, "test", failingRunner);
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"update_korat","arguments":{}}}""" + "\n");

        // Must not throw
        await server.RunAsync(input, default);

        var line = JsonNode.Parse(output.ToString().Trim())!.AsObject();
        Assert.NotNull(line["result"]);
        var text = line["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Upgrade failed", text);
        Assert.Contains("korat upgrade", text);
    }

}
