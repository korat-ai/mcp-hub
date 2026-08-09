using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="ConnectCommand.ResolveServerIdAsync"/> status-code
/// branching (Bug 2 fix): 401/403 → auth error, 5xx → cloud-unreachable error,
/// not-found list → not-found message, found → returns id.
/// </summary>
[Collection("Console state")]
public class ConnectCommandResolveTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static CliCredentials MakeCreds(string url = "https://cloud.example.com") =>
        new("korat_cli_test_token", "full", DateTimeOffset.UtcNow.AddDays(90), url);

    private static LocalIdentity MakeIdentity(string url = "https://cloud.example.com") =>
        new() { NodeId = "test-node", CloudUrl = url };

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // Capture stderr by temporarily redirecting Console.Error.
    private static (string? result, string stderr) CaptureStderr(Func<Task<string?>> action)
    {
        var buffer = new StringWriter();
        var original = Console.Error;
        Console.SetError(buffer);
        try
        {
            var result = action().GetAwaiter().GetResult();
            return (result, buffer.ToString());
        }
        finally
        {
            Console.SetError(original);
            // Reset ExitCode so it doesn't bleed into other tests.
            Environment.ExitCode = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 401 → auth error path
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveServerIdAsync_401_prints_login_hint_and_returns_null()
    {
        var handler = new LoginCommandTests.CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var (serverId, stderr) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds(),
                "MyServer",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Null(serverId);
        Assert.Contains("korat login", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveServerIdAsync_403_prints_login_hint_and_returns_null()
    {
        var handler = new LoginCommandTests.CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        var (serverId, stderr) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds(),
                "MyServer",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Null(serverId);
        Assert.Contains("korat login", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5xx → cloud-unreachable error
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveServerIdAsync_500_prints_server_error_and_returns_null()
    {
        var handler = new LoginCommandTests.CallbackHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var (serverId, stderr) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds("https://cloud.example.com"),
                "MyServer",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Null(serverId);
        // Must mention something about the cloud or the status code.
        Assert.Contains("500", stderr);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 200 list with name absent → not-found
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveServerIdAsync_200_list_missing_name_returns_null()
    {
        var body = new
        {
            mcpServers = new[]
            {
                new { id = "server-id-1", displayName = "OtherServer" }
            }
        };
        var handler = new LoginCommandTests.CallbackHandler(_ => JsonResponse(body));

        var (serverId, stderr) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds(),
                "MyServer",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Null(serverId);
        Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
        // Must NOT contain auth-error messages.
        Assert.DoesNotContain("korat login", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 200 list with name present → returns id
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveServerIdAsync_200_list_with_matching_name_returns_id()
    {
        var body = new
        {
            mcpServers = new[]
            {
                new { id = "server-id-abc", displayName = "MyServer" }
            }
        };
        var handler = new LoginCommandTests.CallbackHandler(_ => JsonResponse(body));

        var (serverId, _) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds(),
                "MyServer",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Equal("server-id-abc", serverId);
    }

    [Fact]
    public void ResolveServerIdAsync_200_name_match_is_case_insensitive()
    {
        var body = new
        {
            mcpServers = new[]
            {
                new { id = "server-id-xyz", displayName = "MYSERVER" }
            }
        };
        var handler = new LoginCommandTests.CallbackHandler(_ => JsonResponse(body));

        var (serverId, _) = CaptureStderr(() =>
            ConnectCommand.ResolveServerIdAsync(
                MakeIdentity(),
                MakeCreds(),
                "myserver",
                CancellationToken.None,
                handlerOverride: handler));

        Assert.Equal("server-id-xyz", serverId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveOrCreateAgent — stable default and explicit name
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveOrCreateAgent_reuses_stable_default_when_requested_name_is_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"korat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var store = new LocalIdentityStore(Path.Combine(dir, "config.json"));
        var identity = store.LoadOrCreate();

        var agent = ConnectCommand.ResolveOrCreateAgent(identity, agentName: null, store);

        Assert.Equal("default", agent.Name);
        Assert.False(string.IsNullOrEmpty(agent.AgentClientId));
        var reloaded = new LocalIdentityStore(Path.Combine(dir, "config.json")).LoadOrCreate();
        var again = ConnectCommand.ResolveOrCreateAgent(reloaded, agentName: null, store);
        Assert.Equal(agent.AgentClientId, again.AgentClientId);
        Assert.Single(reloaded.Agents, a => a.Name == "default");
    }

    [Fact]
    public void ResolveOrCreateAgent_uses_explicit_name_when_provided()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"korat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var store = new LocalIdentityStore(Path.Combine(dir, "config.json"));
        var identity = store.LoadOrCreate();

        var agent = ConnectCommand.ResolveOrCreateAgent(identity, "cursor", store);

        Assert.Equal("cursor", agent.Name);
    }
}
