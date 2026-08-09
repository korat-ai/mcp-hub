using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli;

namespace Korat.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Show publisher runtime and MCP server status");
        // #98: machine-readable output for tooling / scripts.
        var jsonOption = new Option<bool>("--json",
            "Output machine-readable JSON instead of the human dashboard");
        command.AddOption(jsonOption);
        command.SetHandler(ShowStatusAsync, jsonOption);
        return command;
    }

    private static async Task ShowStatusAsync(bool json)
    {
        var credStore = new CredentialStore();
        var err = Console.Error;
        await ExecuteAsync(
            credentialStore: credStore,
            handlerOverride: null,
            errorWriter: err,
            ct: default,
            outputJson: json,
            outputWriter: null);
    }

    /// <summary>
    /// Testable core: loads credentials from <paramref name="credentialStore"/>, sends
    /// <c>Authorization: Bearer</c> on REST calls, and prints status to stdout.
    /// If no credentials are found, writes a "run korat login" message to
    /// <paramref name="errorWriter"/> and returns without making any HTTP call.
    /// </summary>
    internal static async Task ExecuteAsync(
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? errorWriter,
        CancellationToken ct,
        bool outputJson = false,
        TextWriter? outputWriter = null)
    {
        var errOut = errorWriter ?? Console.Error;
        var output = outputWriter ?? Console.Out;
        var store = credentialStore ?? new CredentialStore();

        var creds = await store.LoadAsync(ct);
        if (creds is null)
        {
            await errOut.WriteLineAsync("Not authenticated. Run `korat login` first.");
            return;
        }

        var identity = new LocalIdentityStore().LoadOrCreate();

        var handler = handlerOverride ?? new HttpClientHandler();
        using var http = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(creds.CloudUrl.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", creds.AccessToken);

        SpaceOverviewResponse? space = null;
        string? fetchError = null;
        try
        {
            space = await http.GetFromJsonAsync("/api/space", KoratCliJsonContext.Default.SpaceOverviewResponse, ct);
        }
        catch (Exception ex)
        {
            fetchError = ex.Message;
        }

        // The public runtime count excludes synthetic agent-kind consumer identities and applies
        // the same heartbeat-derived status as `korat nodes` and the web console.
        var publisherNodes = space?.Nodes
            .Where(n => !string.Equals(n.Kind, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var statusNow = space?.ServerTime ?? DateTimeOffset.UtcNow;
        var onlineNodes = publisherNodes.Count(n =>
            NodesCommand.DeriveEffectiveStatus(
                n.Status,
                n.LastSeenAt,
                space?.PresenceStaleSeconds,
                statusNow) == "Online");
        var totalNodes = publisherNodes.Count;
        var serversTotal = space?.McpServers.Count ?? 0;
        var serversAvailable = space?.McpServers.Count(server =>
            McpListCommand.GetCloudAvailability(
                server,
                space.PresenceStaleSeconds,
                space.ServerTime) == "Available") ?? 0;
        var declaredCount = identity.McpServers.Count;

        // #98: JSON output.
        if (outputJson)
        {
            var doc = new StatusDocument
            {
                RuntimeId = identity.NodeId,
                NodeId = identity.NodeId,
                CloudUrl = creds.CloudUrl,
                SpaceName = space?.DisplayName,
                RuntimesOnline = onlineNodes,
                RuntimesTotal = totalNodes,
                NodesOnline = onlineNodes,
                NodesTotal = totalNodes,
                McpServersAvailable = serversAvailable,
                McpServersTotal = serversTotal,
                DeclaredServerCount = declaredCount,
                CloudReachable = fetchError is null && space is not null,
                CloudError = fetchError,
            };
            await output.WriteLineAsync(
                JsonSerializer.Serialize(doc, KoratCliJsonContext.Default.StatusDocument));
            return;
        }

        // ── Human dashboard (#99) ─────────────────────────────────────────────
        await output.WriteLineAsync("── Korat Status ─────────────────────────────────────────");
        await output.WriteLineAsync($"Runtime  : {identity.NodeId}");
        await output.WriteLineAsync($"Cloud    : {creds.CloudUrl}");

        if (fetchError is not null)
        {
            await output.WriteLineAsync($"Cloud    : UNREACHABLE — {fetchError}");
            await output.WriteLineAsync($"Servers  : {declaredCount} declared locally");
            await output.WriteLineAsync("─────────────────────────────────────────────────────────");
            return;
        }
        if (space is null)
        {
            await output.WriteLineAsync("Space    : unavailable");
            await output.WriteLineAsync("─────────────────────────────────────────────────────────");
            return;
        }

        await output.WriteLineAsync($"Space    : {space.DisplayName}");
        await output.WriteLineAsync($"Runtimes : {onlineNodes}/{totalNodes} online");
        await output.WriteLineAsync($"MCP      : {serversAvailable}/{serversTotal} available / {declaredCount} declared locally");
        await output.WriteLineAsync("─────────────────────────────────────────────────────────");
    }

}
