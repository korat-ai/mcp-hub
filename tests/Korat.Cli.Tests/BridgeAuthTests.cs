using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Task 10: Bridge + REST commands migrate X-Korat-Owner-Token to Bearer.
/// Verifies that StatusCommand, McpListCommand, ConnectCommand REST calls
/// attach Authorization: Bearer from CredentialStore, and that missing
/// credentials produce a clear "run korat login first" error.
/// </summary>
public class BridgeAuthTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CredentialStore BuildStore() => new(_tempDir);

    private static CliCredentials MakeCreds(string url = "https://cloud.example.com") =>
        new("korat_cli_test_bearer_token", "full",
            DateTimeOffset.UtcNow.AddDays(90), url);

    // ──────────────────────────────────────────────────────────────────────────
    // StatusCommand
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusCommand_sends_Bearer_not_owner_token()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedAuth = null;
        string? capturedOwnerToken = null;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedOwnerToken = req.Headers.TryGetValues("X-Korat-Owner-Token", out var v)
                ? string.Join(",", v) : null;
            // Return minimal space JSON so ShowStatusAsync doesn't fail.
            var body = JsonSerializer.Serialize(new
            {
                displayName = "Test Space",
                nodes = Array.Empty<object>(),
                mcpServers = Array.Empty<object>(),
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        var err = new StringWriter();
        await StatusCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            errorWriter: err,
            ct: default);

        Assert.StartsWith("Bearer korat_cli_test_bearer_token", capturedAuth);
        Assert.Null(capturedOwnerToken);
    }

    [Fact]
    public async Task StatusCommand_missing_credentials_writes_error_and_returns()
    {
        var credStore = BuildStore(); // empty — no credentials

        var err = new StringWriter();
        await StatusCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: null,   // handler must never be called
            errorWriter: err,
            ct: default);

        var printed = err.ToString();
        Assert.Contains("korat login", printed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatusCommand_UsesHeartbeatPresence_AndExcludesConsumerIdentities()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());
        var now = DateTimeOffset.UtcNow;

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                displayName = "Test Space",
                serverTime = now,
                presenceStaleSeconds = 90,
                nodes = new object[]
                {
                    new
                    {
                        displayName = "stale-runtime",
                        kind = "publisher",
                        status = "Online",
                        lastSeenAt = now.AddMinutes(-10),
                    },
                    new
                    {
                        displayName = "consumer-identity",
                        kind = "agent",
                        status = "Online",
                        lastSeenAt = now,
                    },
                },
                mcpServers = new object[]
                {
                    new
                    {
                        displayName = "stale-server",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeStatus = "Online",
                        publisherNodeLastSeenAt = now.AddMinutes(-10),
                        transport = "Stdio",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        var output = new StringWriter();
        await StatusCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            errorWriter: new StringWriter(),
            ct: default,
            outputWriter: output);

        Assert.Contains("Runtimes : 0/1 online", output.ToString());
        Assert.Contains("MCP      : 0/1 available", output.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // McpListCommand
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task McpListCommand_sends_Bearer_not_owner_token()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedAuth = null;
        string? capturedOwnerToken = null;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedOwnerToken = req.Headers.TryGetValues("X-Korat-Owner-Token", out var v)
                ? string.Join(",", v) : null;
            var body = JsonSerializer.Serialize(new { mcpServers = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            // Inject empty local view so the test stays hermetic (no real config / launchd / systemctl).
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default);

        Assert.StartsWith("Bearer korat_cli_test_bearer_token", capturedAuth);
        Assert.Null(capturedOwnerToken);
    }

    [Fact]
    public async Task McpListCommand_missing_credentials_writes_error_and_returns()
    {
        var credStore = BuildStore();

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: null,
            outputWriter: out_,
            ct: default);

        var printed = out_.ToString();
        Assert.Contains("korat login", printed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task McpListCommand_merges_local_and_cloud_status_glyphs()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        // Cloud (camelCase) returns one server local to this machine + one remote, both available.
        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new { displayName = "everything", status = "Published", isAsserted = true, publisherNodeStatus = "Online" },
                    new { displayName = "remote-fs", status = "Published", isAsserted = true, publisherNodeStatus = "Online" },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var local = new Korat.Cli.Commands.LocalIdentity();
        local.McpServers.Add(new Korat.Cli.Commands.LocalMcpServer { DisplayName = "everything" });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            localIdentity: local,
            serviceStatus: new Korat.Cli.Service.ServiceStatus(true, true, null),
            ct: default);

        var printed = out_.ToString();
        var everythingLine = printed.Split('\n').First(l => l.StartsWith("everything"));
        var remoteLine = printed.Split('\n').First(l => l.StartsWith("remote-fs"));
        // Local + served + cloud-available: both legs present.
        Assert.Contains("💻:✅", everythingLine);
        Assert.Contains("☁️:✅", everythingLine);
        // Remote server: NO local leg, only cloud.
        Assert.DoesNotContain("💻", remoteLine);
        Assert.Contains("☁️:✅", remoteLine);
    }

    // node-visibility-doctor (2026-07-02), Task B4: "где кто запущен" — the publisher's node
    // name (already resolved server-side, 021) surfaces as a "via <node>" column.
    [Fact]
    public async Task McpListCommand_shows_publisher_column()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        displayName = "remote-fs", status = "Published", isAsserted = true,
                        publisherNodeStatus = "Online", publisherNodeName = "publisher-laptop",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default);

        var line = out_.ToString().Split('\n').First(l => l.StartsWith("remote-fs"));
        Assert.Contains("via publisher-laptop", line);
    }

    [Fact]
    public async Task McpListCommand_no_emoji_alignment_unaffected_by_publisher_column()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        displayName = "everything", status = "Published", isAsserted = true,
                        publisherNodeStatus = "Online", publisherNodeName = (string?)"box-2",
                    },
                    new
                    {
                        displayName = "e", status = "Published", isAsserted = true,
                        publisherNodeStatus = "Online", publisherNodeName = (string?)null,
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default,
            noEmoji: true);

        var printed = out_.ToString();
        var everythingLine = printed.Split('\n').First(l => l.StartsWith("everything"));
        var eLine = printed.Split('\n').First(l => l.StartsWith("e "));
        // Aligned cloud: prefix stays identical up to the padded name width regardless of the
        // trailing publisher column (present on one row, absent on the other).
        Assert.Contains("cloud:[ok]", everythingLine);
        Assert.Contains("cloud:[ok]", eLine);
        Assert.Contains("via box-2", everythingLine);
        Assert.DoesNotContain("via", eLine);
    }

    // node-visibility-doctor (2026-07-02) + Finding 16, M5 (Increment 1, HTTP MCP
    // direct-to-Space): an http_cloud server has no publisher node at all —
    // PublisherNodeStatus is always "" for it, so a naive Online check would report it
    // Unavailable/💤 forever. Availability must collapse to Published alone for http_cloud,
    // plus a disclosed "(cloud-terminated)" suffix instead of "via <node>".
    [Fact]
    public async Task McpListCommand_HttpCloudServer_AvailableWhenPublished_WithCloudTerminatedSuffix()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        displayName = "cloud-api", status = "Published", isAsserted = true,
                        publisherNodeStatus = "", transport = "http_cloud",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default);

        var line = out_.ToString().Split('\n').First(l => l.StartsWith("cloud-api"));
        Assert.Contains("☁️:✅", line);
        Assert.Contains("(cloud-terminated)", line);
        Assert.DoesNotContain("via", line);
    }

    [Fact]
    public async Task McpListCommand_HttpCloudServer_JsonOutput_HasIsCloudTerminated()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        displayName = "cloud-api", status = "Published", isAsserted = true,
                        publisherNodeStatus = "", transport = "http_cloud",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var out_ = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: out_,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default,
            outputJson: true);

        using var doc = JsonDocument.Parse(out_.ToString());
        var entry = doc.RootElement.EnumerateArray().First();
        // McpListJsonEntry serializes via the source-generated KoratCliJsonContext, which does
        // not set PropertyNamingPolicy — output uses the declared (PascalCase) property names,
        // same convention already pinned by NodesCommandTests.cs's `entry.GetProperty("Name")`.
        Assert.True(entry.GetProperty("CloudAvailable").GetBoolean());
        Assert.True(entry.GetProperty("IsCloudTerminated").GetBoolean());
    }

    [Fact]
    public async Task McpListCommand_StalePublisher_IsUnavailable_AndJsonIncludesId()
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
                mcpServers = new[]
                {
                    new
                    {
                        id = new { value = "mcp-stale-1" },
                        displayName = "stale-server",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeStatus = "Online",
                        publisherNodeLastSeenAt = now.AddMinutes(-10),
                        publisherNodeName = "offline-mac",
                        transport = "Stdio",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default,
            outputJson: true);

        using var doc = JsonDocument.Parse(output.ToString());
        var entry = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("mcp-stale-1", entry.GetProperty("Id").GetString());
        Assert.False(entry.GetProperty("CloudAvailable").GetBoolean());
        Assert.Equal("Unavailable", entry.GetProperty("CloudAvailability").GetString());
    }

    [Fact]
    public async Task McpListCommand_ShowIds_PrintsServerId()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        id = new { value = "mcp-visible-id" },
                        displayName = "visible-server",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeStatus = "Online",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default,
            showIds: true);

        Assert.Contains("id mcp-visible-id", output.ToString());
    }

    [Fact]
    public async Task McpListCommand_DuplicateDisplayNames_PreservesEveryServerId()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        id = new { value = "mcp-duplicate-a" },
                        displayName = "duplicate",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeStatus = "Online",
                    },
                    new
                    {
                        id = new { value = "mcp-duplicate-b" },
                        displayName = "duplicate",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeStatus = "Online",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var output = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            localIdentity: new Korat.Cli.Commands.LocalIdentity(),
            serviceStatus: new Korat.Cli.Service.ServiceStatus(false, false, null),
            ct: default,
            outputJson: true);

        using var document = JsonDocument.Parse(output.ToString());
        var ids = document.RootElement
            .EnumerateArray()
            .Select(entry => entry.GetProperty("Id").GetString())
            .ToArray();
        Assert.Collection(
            ids,
            id => Assert.Equal("mcp-duplicate-a", id),
            id => Assert.Equal("mcp-duplicate-b", id));
    }

    [Fact]
    public async Task McpListCommand_Locality_UsesPublisherRuntimeId_WhenAvailable()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                mcpServers = new[]
                {
                    new
                    {
                        id = new { value = "mcp-remote" },
                        displayName = "same-name",
                        status = "Published",
                        isAsserted = true,
                        publisherNodeId = new { value = "remote-runtime" },
                        publisherNodeStatus = "Online",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });

        var identity = new Korat.Cli.Commands.LocalIdentity { NodeId = "local-runtime" };
        identity.McpServers.Add(new Korat.Cli.Commands.LocalMcpServer { DisplayName = "same-name" });

        var output = new StringWriter();
        await McpListCommand.ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            localIdentity: identity,
            serviceStatus: new Korat.Cli.Service.ServiceStatus(true, true, null),
            ct: default,
            outputJson: true);

        using var document = JsonDocument.Parse(output.ToString());
        var entries = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);

        var remote = entries.Single(entry => entry.GetProperty("Id").ValueKind == JsonValueKind.String);
        Assert.False(remote.GetProperty("Local").GetBoolean());

        var localOnly = entries.Single(entry => entry.GetProperty("Id").ValueKind == JsonValueKind.Null);
        Assert.True(localOnly.GetProperty("Local").GetBoolean());
        Assert.Equal("absent", localOnly.GetProperty("CloudStatus").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ConnectCommand — REST helper (ResolveServerIdAsync / CreateBearerHttp)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectCommand_CreateBearerHttp_sends_Authorization_Bearer()
    {
        var credStore = BuildStore();
        var creds = MakeCreds();
        await credStore.SaveAsync(creds);

        string? capturedAuth = null;
        string? capturedOwnerToken = null;

        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedOwnerToken = req.Headers.TryGetValues("X-Korat-Owner-Token", out var v)
                ? string.Join(",", v) : null;
            // Return empty mcpServers so ResolveServerIdAsync returns null (server not found).
            var body = JsonSerializer.Serialize(new { mcpServers = Array.Empty<object>() });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        // ResolveServerIdAsync is testable via CreateBearerHttpClient.
        using var http = ConnectCommand.CreateBearerHttpClient(creds, handler);
        using var resp = await http.GetAsync("/api/space");

        Assert.StartsWith("Bearer korat_cli_test_bearer_token", capturedAuth);
        Assert.Null(capturedOwnerToken);
    }
}
