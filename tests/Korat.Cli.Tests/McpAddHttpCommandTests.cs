using System.Net;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space, Task 7): unit tests for <c>korat mcp add-http</c>'s
/// testable core, <see cref="McpAddHttpCommand.ExecuteAsync"/>. Mirrors BridgeAuthTests'
/// CredentialStore + <see cref="LoginCommandTests.CallbackHandler"/> injection style.
/// </summary>
public class McpAddHttpCommandTests : IDisposable
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

    private static HttpResponseMessage CreatedResponse(string id = "srv_1") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { id, displayName = "n", transport = "http_cloud" }),
                Encoding.UTF8, "application/json"),
        };

    // ──────────────────────────────────────────────────────────────────────────
    // Valid input POSTs the expected JSON body
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidInput_PostsExpectedJsonBody_WithBearerAuth()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreatedResponse();
        });

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "my-remote-server",
            url: "https://example.test/mcp",
            bearer: true,
            header: null,
            secret: "sk-super-secret-token",
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/api/mcp-servers", captured.RequestUri!.AbsolutePath);
        Assert.StartsWith("Bearer korat_cli_test_bearer_token", captured.Headers.Authorization?.ToString());

        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("my-remote-server", body.GetProperty("displayName").GetString());
        Assert.Equal("https://example.test/mcp", body.GetProperty("remoteUrl").GetString());
        Assert.Equal("bearer", body.GetProperty("authMode").GetString());
        Assert.Equal("sk-super-secret-token", body.GetProperty("secret").GetString());
        Assert.True(body.GetProperty("authHeaderName").ValueKind is JsonValueKind.Null);

        Assert.Contains("Created HTTP MCP server 'my-remote-server'", output.ToString());
    }

    [Fact]
    public async Task ValidInput_HeaderAuthMode_SendsAuthHeaderName()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreatedResponse();
        });

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "hdr-server",
            url: "https://example.test/mcp",
            bearer: false,
            header: "X-Api-Key",
            secret: "the-api-key-value",
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(0, exitCode);
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("header", body.GetProperty("authMode").GetString());
        Assert.Equal("X-Api-Key", body.GetProperty("authHeaderName").GetString());
        Assert.Equal("the-api-key-value", body.GetProperty("secret").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // --header without --secret prompts
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HeaderWithoutSecret_PromptsForSecret()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreatedResponse();
        });

        var promptCalled = false;
        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "prompted-server",
            url: "https://example.test/mcp",
            bearer: false,
            header: "X-Api-Key",
            secret: null, // omitted — must prompt, not silently send a null/empty secret
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default,
            readSecret: () => { promptCalled = true; return "prompted-secret-value"; });

        Assert.Equal(0, exitCode);
        Assert.True(promptCalled, "the interactive secret prompt must be invoked when --secret is omitted under a non-none authMode");
        Assert.Contains("Secret:", output.ToString());

        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("prompted-secret-value", body.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task SecretDash_ReadsFromStdin()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreatedResponse();
        });

        var stdinCalled = false;
        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "stdin-server",
            url: "https://example.test/mcp",
            bearer: true,
            header: null,
            secret: "-",
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default,
            readStdin: () => { stdinCalled = true; return "piped-secret-value"; });

        Assert.Equal(0, exitCode);
        Assert.True(stdinCalled);
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("piped-secret-value", body.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task AuthModeNone_NeverPromptsOrReadsStdin()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ => CreatedResponse());

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "no-auth-server",
            url: "https://example.test/mcp",
            bearer: false,
            header: null,
            secret: null,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default,
            readSecret: () => throw new InvalidOperationException("must not prompt when authMode is none"),
            readStdin: () => throw new InvalidOperationException("must not read stdin when authMode is none"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AuthModeNone_WithStraySecret_OmitsSecretFromBody()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreatedResponse();
        });

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "no-auth-server",
            url: "https://example.test/mcp",
            bearer: false,
            header: null,
            secret: "stray-value-should-not-be-stored",
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(0, exitCode);
        // T7b gate (LOW): authMode=none must never store a secret, even if --secret was passed —
        // otherwise the endpoint envelope-encrypts an unusable secret and reports hasSecret=true.
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("none", body.GetProperty("authMode").GetString());
        Assert.True(body.GetProperty("secret").ValueKind is JsonValueKind.Null);
        Assert.DoesNotContain("stray-value-should-not-be-stored", capturedBody);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Increment 2: --oauth flag
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OAuthFlag_SendsOAuthAuthModeAndNoSecret_PrintsConsoleHint()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new LoginCommandTests.CallbackHandler(req =>
        {
            captured = req;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"srv-1","status":"NeedsReauth"}""", Encoding.UTF8, "application/json"),
            };
        });

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "my-oauth-server",
            url: "https://mcp.example.test/",
            bearer: false,
            header: null,
            secret: null,
            oauth: true,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default,
            readSecret: () => throw new InvalidOperationException("must not prompt for a secret when --oauth is set"),
            readStdin: () => throw new InvalidOperationException("must not read stdin when --oauth is set"));

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("/api/mcp-servers", captured!.RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal("oauth", body.GetProperty("authMode").GetString());
        Assert.True(body.GetProperty("secret").ValueKind is JsonValueKind.Null); // no secret leaked into the request
        Assert.Contains("needs re-authorization", output.ToString());
        Assert.Contains("console", output.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // An invalid name is rejected before any HTTP call
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("bad/slash")]
    [InlineData(" leading")]
    public async Task InvalidName_RejectedBeforeAnyHttpCall(string invalidName)
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("no HTTP call should be made for an invalid name"));

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: invalidName,
            url: "https://example.test/mcp",
            bearer: false,
            header: null,
            secret: null,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("Error:", output.ToString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Auth / error-path plumbing
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotLoggedIn_ReturnsErrorExitCodeAndNeverCallsHttp()
    {
        var credStore = BuildStore(); // empty — no saved credentials

        var handler = new LoginCommandTests.CallbackHandler(_ =>
            throw new InvalidOperationException("no HTTP call should be made when not logged in"));

        var output = new StringWriter();
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "some-server",
            url: "https://example.test/mcp",
            bearer: false,
            header: null,
            secret: null,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(1, exitCode);
        Assert.Contains("korat login", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonSuccessResponse_ReturnsExitCode1_AndNeverEchoesTheSecret()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"remoteUrl: not allowed\"}", Encoding.UTF8, "application/json"),
        });

        var output = new StringWriter();
        const string secretValue = "sk-must-never-appear-in-output";
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "rejected-server",
            url: "http://169.254.169.254/",
            bearer: true,
            header: null,
            secret: secretValue,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(1, exitCode);
        var printed = output.ToString();
        Assert.Contains("400", printed);
        Assert.DoesNotContain(secretValue, printed);
    }

    [Fact]
    public async Task SuccessfulCreate_NeverEchoesTheSecretInConfirmationOutput()
    {
        var credStore = BuildStore();
        await credStore.SaveAsync(MakeCreds());

        var handler = new LoginCommandTests.CallbackHandler(_ => CreatedResponse());

        var output = new StringWriter();
        const string secretValue = "sk-must-never-appear-in-output-either";
        var exitCode = await McpAddHttpCommand.ExecuteAsync(
            name: "quiet-server",
            url: "https://example.test/mcp",
            bearer: true,
            header: null,
            secret: secretValue,
            credentialStore: credStore,
            handlerOverride: handler,
            outputWriter: output,
            ct: default);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(secretValue, output.ToString());
    }
}
