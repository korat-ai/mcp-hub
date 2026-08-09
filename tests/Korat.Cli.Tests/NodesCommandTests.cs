using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// node-visibility-doctor design (2026-07-02), Task B4: `korat nodes` (host metadata + effective
/// presence table) and `korat node note` (owner-editable label, set via PATCH /api/nodes/{id}).
/// </summary>
public sealed class NodesCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CredentialStore BuildStore() => new(_tempDir);

    private static CliCredentials MakeCreds(string url = "https://cloud.example.com") =>
        new("korat_cli_test_bearer_token", "full", DateTimeOffset.UtcNow.AddDays(90), url);

    // ──────────────────────────────────────────────────────────────────────────
    // NodesCommand — `korat nodes`
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Table_shows_host_os_and_note()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var now = DateTimeOffset.UtcNow;
        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                serverTime = now,
                presenceStaleSeconds = 90,
                nodes = new[]
                {
                    new
                    {
                        id = new { value = "node-1" },
                        displayName = "my-mac",
                        status = "Online",
                        lastSeenAt = now,
                        kind = "publisher",
                        hostname = "publisher-laptop",
                        os = "macos",
                        arch = "arm64",
                        cliVersion = "0.4.1",
                        note = "работает по вечерам",
                    },
                },
                mcpServers = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        var printed = output.ToString();
        Assert.DoesNotContain("KIND", printed.Split('\n')[0]);
        Assert.Contains("my-mac", printed);
        Assert.Contains("publisher-laptop", printed);
        Assert.Contains("macos", printed);
        Assert.Contains("работает по вечерам", printed);
        var dataLine = printed.Split('\n').First(l => l.StartsWith("my-mac"));
        Assert.Contains("Online", dataLine);
    }

    [Fact]
    public async Task Stale_lastSeenAt_derives_offline_status_despite_raw_Online()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var now = DateTimeOffset.UtcNow;
        var staleLastSeen = now.AddMinutes(-10); // way past a 90s stale threshold
        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                serverTime = now,
                presenceStaleSeconds = 90,
                nodes = new[]
                {
                    new
                    {
                        id = new { value = "node-2" },
                        displayName = "stale-node",
                        status = "Online", // raw status hasn't rolled over yet
                        lastSeenAt = staleLastSeen,
                        kind = "agent",
                        hostname = (string?)null,
                        os = (string?)null,
                        arch = (string?)null,
                        cliVersion = (string?)null,
                        note = (string?)null,
                    },
                },
                mcpServers = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default,
            includeConsumers: true);

        var printed = output.ToString();
        Assert.Contains("KIND", printed.Split('\n')[0]);
        var dataLine = printed.Split('\n').First(l => l.StartsWith("stale-node"));
        Assert.Contains("Offline", dataLine);
        // Host/OS were never advertised (legacy/never-connected) — shown as "-", not blank/null.
        Assert.Contains("-", dataLine);
    }

    [Fact]
    public async Task Json_output_includes_host_metadata_effective_status_and_note()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var now = DateTimeOffset.UtcNow;
        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                serverTime = now,
                presenceStaleSeconds = 90,
                nodes = new[]
                {
                    new
                    {
                        id = new { value = "node-3" },
                        displayName = "json-node",
                        status = "Online",
                        lastSeenAt = now,
                        kind = "publisher",
                        hostname = "box",
                        os = "linux",
                        arch = "x64",
                        cliVersion = "0.4.1",
                        note = "keep me",
                    },
                },
                mcpServers = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: handler, outputWriter: output,
            ct: default, outputJson: true);

        // NodeListJsonEntry follows the CLI's existing --json convention (McpListJsonEntry,
        // StatusDocument): the source-generated context has no naming policy, so property names
        // serialize as their exact C# identifiers (PascalCase).
        using var doc = JsonDocument.Parse(output.ToString());
        var entry = doc.RootElement[0];
        Assert.Equal("node-3", entry.GetProperty("Id").GetString());
        Assert.Equal("json-node", entry.GetProperty("Name").GetString());
        Assert.Equal("publisher", entry.GetProperty("Kind").GetString());
        Assert.Equal("box", entry.GetProperty("Host").GetString());
        Assert.Equal("linux", entry.GetProperty("Os").GetString());
        Assert.Equal("x64", entry.GetProperty("Arch").GetString());
        Assert.Equal("0.4.1", entry.GetProperty("CliVersion").GetString());
        Assert.Equal("Online", entry.GetProperty("Status").GetString());
        Assert.Equal("keep me", entry.GetProperty("Note").GetString());
    }

    [Fact]
    public async Task No_nodes_prints_message()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new { nodes = Array.Empty<object>(), mcpServers = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Contains("No publisher runtimes.", output.ToString());
    }

    [Fact]
    public async Task Default_view_hides_agent_kind_consumer_identities()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                nodes = new[]
                {
                    new
                    {
                        id = new { value = "consumer-node" },
                        displayName = "synthetic-consumer",
                        status = "Offline",
                        kind = "agent",
                    },
                },
                mcpServers = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Contains("No publisher runtimes.", output.ToString());
        Assert.DoesNotContain("synthetic-consumer", output.ToString());
    }

    [Fact]
    public async Task Missing_credentials_prints_error()
    {
        var credStore = BuildStore();

        var output = new StringWriter();
        await NodesCommand.ExecuteAsync(
            credentialStore: credStore, handlerOverride: null, outputWriter: output, ct: default);

        Assert.Contains("korat login", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // NodeNoteCommand — `korat node note`
    // ──────────────────────────────────────────────────────────────────────────

    private static HttpResponseMessage SpaceResponseWithNodes(params (string Id, string Name)[] nodes)
    {
        var body = JsonSerializer.Serialize(new
        {
            nodes = nodes.Select(n => new { id = new { value = n.Id }, displayName = n.Name, status = "Online", kind = "publisher" }),
            mcpServers = Array.Empty<object>(),
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Note_by_name_single_match_patches_correct_node()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        HttpRequestMessage? patchRequest = null;
        string? patchBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("node-1", "my-mac"));

            patchRequest = req;
            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            "my-mac", "work laptop", id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        Assert.NotNull(patchRequest);
        Assert.Equal(HttpMethod.Patch, patchRequest!.Method);
        Assert.EndsWith("/api/nodes/node-1", patchRequest.RequestUri!.AbsolutePath);
        Assert.Contains("work laptop", patchBody);
        Assert.Contains("Note set", output.ToString());
    }

    [Fact]
    public async Task Empty_text_clears_the_note()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? patchBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("node-1", "my-mac"));

            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            "my-mac", "", id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(patchBody!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Note").ValueKind);
        Assert.Contains("Note cleared.", output.ToString());
    }

    [Fact]
    public async Task Ambiguous_name_errors_and_lists_ids_without_patching()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var patchCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("id-a", "worker"), ("id-b", "worker"));

            patchCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            "worker", "hello", id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.False(patchCalled);
        var printed = output.ToString();
        Assert.Contains("id-a", printed);
        Assert.Contains("id-b", printed);
        Assert.Contains("--id", printed);
    }

    [Fact]
    public async Task Zero_matches_errors_without_patching()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var patchCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("id-a", "some-other-node"));

            patchCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            "does-not-exist", "hello", id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.False(patchCalled);
        Assert.Contains("no runtime named", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Id_option_bypasses_name_lookup()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var getCalled = false;
        HttpRequestMessage? patchRequest = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                getCalled = true;
                return SpaceResponseWithNodes(("id-a", "unused"));
            }

            patchRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            name: null, text: "direct", id: "explicit-node-id",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(getCalled);
        Assert.NotNull(patchRequest);
        Assert.EndsWith("/api/nodes/explicit-node-id", patchRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Foreign_or_unknown_node_reports_cloud_status_code()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteAsync(
            name: null, text: "hi", id: "someone-elses-node",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("404", output.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // NodeNoteCommand — ExecuteFromArgsAsync (the positional-argument resolution layer).
    //
    // BLOCKER regression coverage: System.CommandLine (beta4) used to bind two independently
    // declared positional Arguments (name, text) in DECLARATION ORDER regardless of --id, so
    // `korat node note --id X "hello"` bound "hello" to the name slot and left text empty —
    // silently CLEARING the note. The fix collects positional tokens into one array and
    // resolves name/text explicitly here, so these tests exercise exactly that resolution path
    // (not just the already-split-apart ExecuteAsync core the old tests above cover).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Args_IdOption_plus_one_positional_sets_note_PatchBodyContainsText()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var getCalled = false;
        HttpRequestMessage? patchRequest = null;
        string? patchBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                getCalled = true;
                return SpaceResponseWithNodes(("node-1", "unused"));
            }

            patchRequest = req;
            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: ["work laptop"], id: "node-1",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        Assert.False(getCalled); // --id bypasses name lookup entirely
        Assert.NotNull(patchRequest);
        Assert.EndsWith("/api/nodes/node-1", patchRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("work laptop", patchBody);
        Assert.Contains("Note set", output.ToString());
    }

    [Fact]
    public async Task Args_two_positionals_no_id_sets_note_by_name()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        HttpRequestMessage? patchRequest = null;
        string? patchBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("node-1", "my-mac"));

            patchRequest = req;
            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: ["my-mac", "work laptop"], id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        Assert.NotNull(patchRequest);
        Assert.EndsWith("/api/nodes/node-1", patchRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("work laptop", patchBody);
        Assert.Contains("Note set", output.ToString());
    }

    [Fact]
    public async Task Args_IdOption_plus_empty_string_clears_note()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? patchBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("node-1", "unused"));

            patchBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: [""], id: "node-1",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(patchBody!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Note").ValueKind);
        Assert.Contains("Note cleared.", output.ToString());
    }

    [Fact]
    public async Task Args_IdOption_with_no_positionals_is_usage_error_and_sends_no_request()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("must not call the cloud on a usage error"));

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: [], id: "node-1",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        var printed = output.ToString();
        Assert.Contains("Error", printed);
        Assert.Contains("--id", printed);
    }

    [Fact]
    public async Task Args_IdOption_with_two_positionals_is_usage_error_and_sends_no_request()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("must not call the cloud on a usage error"));

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: ["extra", "text"], id: "node-1",
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error", output.ToString());
    }

    [Fact]
    public async Task Args_no_id_with_single_positional_is_usage_error_and_sends_no_request()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("must not call the cloud on a usage error"));

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: ["my-mac"], id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        var printed = output.ToString();
        Assert.Contains("Error", printed);
        Assert.Contains("Usage", printed);
    }

    [Fact]
    public async Task Args_no_id_with_zero_positionals_is_usage_error_and_sends_no_request()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("must not call the cloud on a usage error"));

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: [], id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error", output.ToString());
    }

    [Fact]
    public async Task Args_ambiguous_name_still_errors_listing_ids_without_patching()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var patchCalled = false;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return SpaceResponseWithNodes(("id-a", "worker"), ("id-b", "worker"));

            patchCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var output = new StringWriter();
        var exitCode = await NodeNoteCommand.ExecuteFromArgsAsync(
            args: ["worker", "hello"], id: null,
            credentialStore: credStore, handlerOverride: handler, outputWriter: output, ct: default);

        Assert.Equal(1, exitCode);
        Assert.False(patchCalled);
        var printed = output.ToString();
        Assert.Contains("id-a", printed);
        Assert.Contains("id-b", printed);
        Assert.Contains("--id", printed);
    }
}
