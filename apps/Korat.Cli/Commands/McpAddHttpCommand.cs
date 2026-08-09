using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Domain;

namespace Korat.Cli.Commands;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space, Task 7): <c>korat mcp add-http &lt;name&gt; --url
/// &lt;url&gt; [--bearer|--header &lt;name&gt;] [--secret &lt;value&gt;]</c> registers a
/// cloud-hosted HTTP MCP server directly against the owner's Space via
/// <c>POST /api/mcp-servers</c> (Task 3, <c>McpServerEndpoints</c>) — unlike <c>korat mcp add</c>,
/// this is NOT a local-node publish: no daemon/config.json entry is created, and there is no
/// launch command. The cloud itself is the terminus (spec §11 decision 3, "cloud-terminated";
/// disclosed to the owner on <c>korat mcp list</c>/the console via the transport-aware badge —
/// see McpListCommand/presence.ts). Increment 2 adds <c>--oauth</c>: this command is create-only
/// for oauth mode — no static secret is prompted/stored; the owner finishes the browser consent
/// in the console (spec: "console-only consent this increment").
/// </summary>
public static class McpAddHttpCommand
{
    public static Command Create()
    {
        var command = new Command("add-http", "Register a cloud-hosted HTTP MCP server (no publisher runtime required)");
        var nameArg = new Argument<string>("name", "Server display name");
        var urlOption = new Option<string>("--url", "Remote Streamable-HTTP MCP endpoint URL") { IsRequired = true };
        var bearerOption = new Option<bool>("--bearer", "Use a static Bearer token (reads --secret)");
        var headerOption = new Option<string?>("--header", "Use a custom auth header with this name (reads --secret)");
        var secretOption = new Option<string?>("--secret",
            "Static secret value (omit to be prompted; pass '-' to read the value from stdin)");
        var oauthOption = new Option<bool>("--oauth", "Use OAuth 2.1 (finish consent in the console after this command)");

        command.AddArgument(nameArg);
        command.AddOption(urlOption);
        command.AddOption(bearerOption);
        command.AddOption(headerOption);
        command.AddOption(secretOption);
        command.AddOption(oauthOption);
        command.SetHandler(AddHttpAsync, nameArg, urlOption, bearerOption, headerOption, secretOption, oauthOption);
        return command;
    }

    private static async Task AddHttpAsync(string name, string url, bool bearer, string? header, string? secret, bool oauth)
    {
        var credStore = new CredentialStore();
        var exitCode = await ExecuteAsync(name, url, bearer, header, secret, oauth,
            credentialStore: credStore, handlerOverride: null, outputWriter: null, ct: default);
        Environment.ExitCode = exitCode;
    }

    /// <summary>
    /// Testable core (mirrors McpListCommand.ExecuteAsync's injectable HttpMessageHandler +
    /// CredentialStore + writer pattern, and AgentRebrainCommand.ExecuteAsync's int-exit-code
    /// shape — this is a single mutating POST, not a listing command, so a real exit code beats
    /// an untestable <c>Environment.Exit</c> call in the core). Returns the process exit code
    /// (0 = success). <paramref name="readSecret"/>/<paramref name="readStdin"/> stand in for
    /// the two real-console-touching operations (the masked interactive prompt, and reading
    /// <c>--secret -</c> from stdin) — both default to the real implementation when null, so
    /// tests never block on actual console input.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        string name,
        string url,
        bool bearer,
        string? header,
        string? secret,
        // Increment 2: added after `secret`, defaulted so every pre-existing named-argument call
        // site (this method has no positional callers besides AddHttpAsync, which now always
        // supplies it) keeps compiling unchanged. `credentialStore`/`handlerOverride`/
        // `outputWriter` gain matching `= null` defaults for the same reason — each already
        // treated null as "use the real default" before this change (see `?? new CredentialStore()`
        // / `?? new HttpClientHandler()` / `?? Console.Out` below), so giving them literal C#
        // defaults changes no runtime behavior, only which parameters a caller must supply.
        bool oauth = false,
        CredentialStore? credentialStore = null,
        HttpMessageHandler? handlerOverride = null,
        TextWriter? outputWriter = null,
        CancellationToken ct = default,
        Func<string>? readSecret = null,
        Func<string>? readStdin = null)
    {
        var output = outputWriter ?? Console.Out;

        // #105/#96-style validation: reject before any I/O, mirrors McpAddCommand.AddAsync.
        if (!McpAddCommand.TryValidateName(name, out var nameError))
        {
            await output.WriteLineAsync($"Error: {nameError}");
            return 1;
        }

        var authMode = oauth
            ? McpServerAuthModes.Oauth
            : header is not null ? McpServerAuthModes.Header : bearer ? McpServerAuthModes.Bearer : McpServerAuthModes.None;

        // Increment 2: oauth has no static secret to prompt for — the owner finishes consent in
        // the console, not the CLI (spec: "console-only consent this increment").
        if (authMode is McpServerAuthModes.Bearer or McpServerAuthModes.Header)
        {
            if (secret == "-")
            {
                // Documented in the option's help text ("pass '-' to read the value from
                // stdin") — read the whole stream, trimming exactly one trailing line ending
                // (a piped `echo "secret"` always appends one).
                secret = readStdin is not null
                    ? readStdin()
                    : TrimOneTrailingNewline(await Console.In.ReadToEndAsync(ct));
            }
            else if (string.IsNullOrEmpty(secret))
            {
                await output.WriteAsync("Secret: ");
                secret = readSecret is not null ? readSecret() : ReadSecretFromConsole();
            }
        }

        var store = credentialStore ?? new CredentialStore();
        var creds = await store.LoadAsync(ct);
        if (creds is null)
        {
            await output.WriteLineAsync("Not authenticated. Run `korat login` first.");
            return 1;
        }

        var handler = handlerOverride ?? new HttpClientHandler();
        using var http = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(creds.CloudUrl.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);

        // T7b gate (LOW): a stray --secret with authMode=none must NOT be stored — the endpoint
        // would envelope-encrypt an unusable secret and report hasSecret=true for a no-auth server.
        // Mirror the console form, which omits the secret for 'none'.
        var requestBody = new McpAddHttpRequest(
            name, url, authMode, header,
            authMode == McpServerAuthModes.None || authMode == McpServerAuthModes.Oauth ? null : secret);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp-servers")
        {
            // Serialize via the source-generated context — trim-safe (no reflection over an
            // anonymous/arbitrary type), matching this CLI's established idiom for outbound
            // POST/PATCH bodies (see AgentRebrainCommand/FeedbackService; PostAsJsonAsync<T> is
            // RequiresUnreferencedCode/IL2026 and breaks the trimmed single-file CLI publish).
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, KoratCliJsonContext.Default.McpAddHttpRequest),
                Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            await output.WriteLineAsync(
                $"Error: cloud returned {(int)response.StatusCode} creating the server.{(string.IsNullOrWhiteSpace(body) ? "" : " " + body)}");
            return 1;
        }

        // Confirmation deliberately echoes only name/url — the secret is envelope-encrypted
        // server-side and MUST NEVER be logged/echoed here, not even masked (there is nothing
        // to mask against: the CLI never receives a secretHint back from this response either).
        if (authMode == McpServerAuthModes.Oauth)
            await output.WriteLineAsync($"Created HTTP MCP server '{name}' -> {url} (needs re-authorization — finish connecting it in the console).");
        else
            await output.WriteLineAsync($"Created HTTP MCP server '{name}' -> {url}");
        return 0;
    }

    private static string TrimOneTrailingNewline(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s[..^2];
        if (s.EndsWith('\n') || s.EndsWith('\r')) return s[..^1];
        return s;
    }

    private static string ReadSecretFromConsole()
    {
        var input = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                input.Length--;
            else if (!char.IsControl(key.KeyChar))
                input.Append(key.KeyChar);
        }
        Console.WriteLine();
        return input.ToString();
    }
}
