using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Korat.Cli.Auth;
using Korat.Cli.Util;
using Korat.Domain.Contracts;

namespace Korat.Cli.Commands;

/// <summary>
/// node-visibility-doctor design (2026-07-02): "не понимаю, где кто запущен на каком хосте" —
/// <c>korat runtimes</c> lists publisher runtimes in this Space with host metadata and the same
/// lastSeenAt-derived effective presence the console uses. Synthetic consumer identities remain
/// available through <c>--all</c> for diagnostics. <c>nodes</c> remains a compatibility alias.
/// </summary>
public static class NodesCommand
{
    public static Command Create()
    {
        var command = new Command("runtimes", "List MCP publisher runtimes (host, OS, presence, note)");
        command.AddAlias("nodes");
        var jsonOption = new Option<bool>("--json", "Output machine-readable JSON instead of the human table");
        var allOption = new Option<bool>("--all", "Include internal consumer identities");
        command.AddOption(jsonOption);
        command.AddOption(allOption);
        command.SetHandler(ListAsync, jsonOption, allOption);
        // #165: `korat nodes prune` — GC stale agent-kind nodes. A subcommand of `nodes` (not a
        // sibling top-level command) since it operates on the same collection `korat nodes` lists.
        command.AddCommand(NodesPruneCommand.Create());
        return command;
    }

    private static async Task ListAsync(bool json, bool includeConsumers)
    {
        var credStore = new CredentialStore();
        await ExecuteAsync(credentialStore: credStore, handlerOverride: null, outputWriter: null,
            ct: default, outputJson: json, includeConsumers: includeConsumers);
    }

    /// <summary>Testable core — mirrors McpListCommand/StatusCommand's ExecuteAsync idiom.</summary>
    internal static async Task ExecuteAsync(
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        CancellationToken ct = default,
        bool outputJson = false,
        bool includeConsumers = false)
    {
        var output = outputWriter ?? Console.Out;
        var store = credentialStore ?? new CredentialStore();

        var creds = await store.LoadAsync(ct);
        if (creds is null)
        {
            await output.WriteLineAsync("Not authenticated. Run `korat login` first.");
            return;
        }

        var handler = handlerOverride ?? new HttpClientHandler();
        using var http = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(creds.CloudUrl.TrimEnd('/') + "/"),
        };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", creds.AccessToken);

        var space = await http.GetFromJsonAsync("/api/space", KoratCliJsonContext.Default.SpaceOverviewResponse, ct);
        var nodes = (space?.Nodes ?? [])
            .Where(n => includeConsumers
                || !string.Equals(n.Kind, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nodes.Count == 0)
        {
            await output.WriteLineAsync(outputJson ? "[]" : "No publisher runtimes.");
            return;
        }

        // 019: server clock is the reference for "now" (avoids trusting this machine's own clock).
        var now = space?.ServerTime ?? DateTimeOffset.UtcNow;
        var staleSeconds = space?.PresenceStaleSeconds;

        if (outputJson)
        {
            var entries = nodes.Select(n => new NodeListJsonEntry
            {
                Id = n.Id?.Value ?? string.Empty,
                Name = n.DisplayName,
                Kind = n.Kind,
                Host = n.Hostname,
                Os = n.Os,
                Arch = n.Arch,
                CliVersion = n.CliVersion,
                Status = DeriveEffectiveStatus(n.Status, n.LastSeenAt, staleSeconds, now),
                LastSeenAt = n.LastSeenAt,
                Note = n.Note,
            }).ToList();
            await output.WriteLineAsync(
                JsonSerializer.Serialize(entries, KoratCliJsonContext.Default.ListNodeListJsonEntry));
            return;
        }

        var rows = nodes.Select(n => new
        {
            Name = n.DisplayName,
            Kind = n.Kind,
            Host = n.Hostname ?? "-",
            Os = n.Os ?? "-",
            Status = DeriveEffectiveStatus(n.Status, n.LastSeenAt, staleSeconds, now),
            LastSeen = n.LastSeenAt is { } seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-",
            Note = n.Note ?? string.Empty,
        }).ToList();

        var nameWidth = Math.Max("NAME".Length, rows.Max(r => r.Name.Length));
        var kindWidth = includeConsumers
            ? Math.Max("KIND".Length, rows.Max(r => r.Kind.Length))
            : 0;
        var hostWidth = Math.Max("HOST".Length, rows.Max(r => r.Host.Length));
        var osWidth = Math.Max("OS".Length, rows.Max(r => r.Os.Length));
        var statusWidth = Math.Max("STATUS".Length, rows.Max(r => r.Status.Length));
        var lastSeenWidth = Math.Max("LAST-SEEN".Length, rows.Max(r => r.LastSeen.Length));

        var header = new StringBuilder()
            .Append("NAME".PadRight(nameWidth)).Append("  ");
        if (includeConsumers)
            header.Append("KIND".PadRight(kindWidth)).Append("  ");
        header.Append("HOST".PadRight(hostWidth)).Append("  ")
            .Append("OS".PadRight(osWidth)).Append("  ")
            .Append("STATUS".PadRight(statusWidth)).Append("  ")
            .Append("LAST-SEEN".PadRight(lastSeenWidth)).Append("  ")
            .Append("NOTE");
        await output.WriteLineAsync(header.ToString());

        foreach (var r in rows)
        {
            var line = new StringBuilder()
                .Append(r.Name.PadRight(nameWidth)).Append("  ");
            if (includeConsumers)
                line.Append(r.Kind.PadRight(kindWidth)).Append("  ");
            line.Append(r.Host.PadRight(hostWidth)).Append("  ")
                .Append(r.Os.PadRight(osWidth)).Append("  ")
                .Append(r.Status.PadRight(statusWidth)).Append("  ")
                .Append(r.LastSeen.PadRight(lastSeenWidth)).Append("  ")
                .Append(r.Note);
            await output.WriteLineAsync(line.ToString());
        }
    }

    /// <summary>
    /// 019 rule: never trust the raw stored Status — derive effective presence from lastSeenAt
    /// age vs presenceStaleSeconds. Mirrors the SPA's isNodeOnline (apps/Korat.App/src/lib/presence.ts).
    /// </summary>
    internal static string DeriveEffectiveStatus(
        string rawStatus, DateTimeOffset? lastSeenAt, int? presenceStaleSeconds, DateTimeOffset now)
    {
        if (!string.Equals(rawStatus, "Online", StringComparison.OrdinalIgnoreCase))
            return "Offline";
        if (lastSeenAt is null)
            return "Offline";

        var staleSeconds = presenceStaleSeconds ?? 90;
        var age = now - lastSeenAt.Value;
        return age.TotalSeconds < staleSeconds ? "Online" : "Offline";
    }
}

/// <summary>
/// #165: <c>korat nodes prune [--older-than 30] [--yes] [--json]</c> — GC for the one-shot
/// <c>korat connect --agent</c> identities that accumulate over time (they show up as offline
/// agent-kind nodes forever otherwise, polluting <c>korat nodes</c> / the space nodes page /
/// doctor's agents-stale warnings). Publisher nodes are NEVER pruned (v1 scope) — the cloud
/// endpoint enforces <c>kind=agent</c> server-side regardless of what the CLI sends.
///
/// Flow: fetch <c>/api/space</c> to preview which agent nodes are stale (name + last-seen) using
/// the SAME cutoff the cloud will apply, print the preview, confirm (unless <c>--yes</c>) via
/// <see cref="TtyConfirm"/> (mirrors UpgradeCommand's /dev/tty idiom), then POST
/// <c>/api/nodes/prune</c> and print the cloud's authoritative result. Zero matches short-circuits
/// before any confirmation prompt or POST.
/// </summary>
public static class NodesPruneCommand
{
    public static Command Create()
    {
        var command = new Command("prune", "Delete stale internal consumer identities created by `connect --agent`");
        var olderThanOption = new Option<int>(
            "--older-than", () => 30,
            "Prune internal consumer runtimes not seen (or, if never seen, not created) in at least this many days");
        var yesOption = new Option<bool>("--yes", "Skip the confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output machine-readable JSON instead of the human summary");
        command.AddOption(olderThanOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);
        command.SetHandler(RunAsync, olderThanOption, yesOption, jsonOption);
        return command;
    }

    private static async Task RunAsync(int olderThan, bool yes, bool json)
    {
        var credStore = new CredentialStore();
        var exitCode = await ExecuteAsync(
            olderThan, yes, json,
            credentialStore: credStore, handlerOverride: null, outputWriter: null,
            confirmAsync: TtyConfirm.AskAsync, ct: default);
        Environment.ExitCode = exitCode;
    }

    /// <summary>Testable core — mirrors NodesCommand.ExecuteAsync/NodeNoteCommand.ExecuteAsync's
    /// idiom. <paramref name="confirmAsync"/> is injectable so tests can supply a canned y/N
    /// answer instead of a real terminal; defaults to <see cref="TtyConfirm.AskAsync"/>.</summary>
    internal static async Task<int> ExecuteAsync(
        int olderThanDays,
        bool yes,
        bool outputJson,
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        Func<string, Task<bool>>? confirmAsync,
        CancellationToken ct = default)
    {
        var output = outputWriter ?? Console.Out;
        var store = credentialStore ?? new CredentialStore();
        var confirm = confirmAsync ?? TtyConfirm.AskAsync;

        if (olderThanDays < 1)
        {
            await output.WriteLineAsync("Error: --older-than must be at least 1 (days).");
            return 1;
        }

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
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", creds.AccessToken);

        // Preview: fetch the current node list and apply the SAME cutoff the cloud will apply
        // (kind=agent, LastSeenAt null-or-older-than-cutoff), so the confirmation prompt shows
        // exactly what is about to be deleted. The cloud's response (after the POST below) is
        // still the authoritative pruned set — this is display-only.
        var space = await http.GetFromJsonAsync("/api/space", KoratCliJsonContext.Default.SpaceOverviewResponse, ct);
        var now = space?.ServerTime ?? DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-olderThanDays);

        // #167 review (fix 1): mirror the cloud's exact never-seen fallback
        // ((n.LastSeenAt ?? n.CreatedAt) < olderThan in SpaceGrain.PruneAgentNodesAsync) instead of
        // treating LastSeenAt == null as always-stale. Before this fix a just-registered,
        // never-connected node showed up in this preview's "will be pruned" list even though the
        // cloud would NOT actually prune it (CreatedAt was recent) — confusing UX.
        // n.CreatedAt is DateTimeOffset? on the CLI DTO: if a (hypothetical) old cloud response
        // omits it, `n.LastSeenAt ?? n.CreatedAt` is null, and C#'s lifted `<` operator on a null
        // DateTimeOffset? always evaluates to false — so the node is excluded (fail-safe), never
        // wrongly included.
        var candidates = (space?.Nodes ?? [])
            .Where(n => string.Equals(n.Kind, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(n => (n.LastSeenAt ?? n.CreatedAt) < cutoff)
            .ToList();

        if (candidates.Count == 0)
        {
            await output.WriteLineAsync(outputJson
                ? "{\"prunedCount\":0,\"prunedNames\":[]}"
                : $"No stale internal consumer identities found (older than {olderThanDays}d). Nothing to prune.");
            return 0;
        }

        if (!outputJson)
        {
            await output.WriteLineAsync($"The following {candidates.Count} internal consumer identity record(s) will be pruned:");
            foreach (var n in candidates)
            {
                var lastSeen = n.LastSeenAt is { } seen
                    ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "never";
                await output.WriteLineAsync($"  {n.DisplayName}  (last seen: {lastSeen})");
            }
        }

        if (!yes)
        {
            var confirmed = await confirm($"Prune {candidates.Count} consumer identity record(s)? [y/N] ");
            if (!confirmed)
            {
                await output.WriteLineAsync("Prune cancelled.");
                return 0;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/nodes/prune")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new PruneNodesRequest { Kind = "agent", OlderThanDays = olderThanDays },
                    KoratCliJsonContext.Default.PruneNodesRequest),
                Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            await output.WriteLineAsync($"Error: cloud returned {(int)response.StatusCode} pruning consumer identities.");
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync(KoratCliJsonContext.Default.PruneNodesResponse, ct);
        var prunedNames = result?.PrunedNames ?? [];

        if (outputJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new PruneNodesResponse { PrunedCount = prunedNames.Count, PrunedNames = prunedNames },
                KoratCliJsonContext.Default.PruneNodesResponse));
        }
        else
        {
            await output.WriteLineAsync(prunedNames.Count == 0
                ? "Pruned 0 consumer identities."
                : $"Pruned {prunedNames.Count} consumer identity record(s): {string.Join(", ", prunedNames)}");
        }

        return 0;
    }
}

/// <summary>
/// node-visibility-doctor design (2026-07-02): "нельзя добавить комментарий к запущенному
/// инстансу" — <c>korat node note "&lt;name&gt;" "&lt;text&gt;"</c> or
/// <c>korat node note --id &lt;nodeId&gt; "&lt;text&gt;"</c> sets/clears the owner-editable Note
/// via <c>PATCH /api/nodes/{id}</c>. An explicit empty string ("") still clears it; a MISSING
/// text argument is a usage error, never an implicit clear.
///
/// Final-review fix: the command used to declare TWO independent positional Arguments
/// (name, text). System.CommandLine (beta4) binds positional tokens to positional Arguments in
/// DECLARATION ORDER regardless of which option preceded them — so
/// <c>korat node note --id abc "hello"</c> had its single remaining token ("hello") bound to the
/// FIRST positional (name), leaving text empty and silently CLEARING the note. Fixed by
/// collecting every remaining positional token into one array and resolving name/text from it
/// explicitly (based on whether --id was given), instead of relying on System.CommandLine's
/// per-Argument slot assignment.
/// </summary>
public static class NodeNoteCommand
{
    public static Command Create()
    {
        var command = new Command("note", "Set or clear the owner note on a publisher runtime (empty text clears it)");
        // Deliberately a single greedy positional (not two separate Arguments) — see the
        // class doc-comment for why: two independent positionals get bound in declaration
        // order by System.CommandLine regardless of --id, which silently swallowed the text.
        var positionalArgs = new Argument<string[]>(
            "args",
            () => Array.Empty<string>(),
            "\"<name>\" \"<text>\" — or just \"<text>\" when --id is given. " +
            "Pass \"\" as the text to clear the note.")
        {
            Arity = ArgumentArity.ZeroOrMore,
        };
        var idOption = new Option<string?>("--id",
            "Runtime id — use when multiple runtimes share the same display name");
        command.AddArgument(positionalArgs);
        command.AddOption(idOption);
        command.SetHandler(RunAsync, positionalArgs, idOption);
        return command;
    }

    private static async Task RunAsync(string[] args, string? id)
    {
        var credStore = new CredentialStore();
        var exitCode = await ExecuteFromArgsAsync(args, id,
            credentialStore: credStore, handlerOverride: null, outputWriter: null, ct: default);
        Environment.ExitCode = exitCode;
    }

    /// <summary>
    /// Resolves the raw positional tokens into (name, text) per whether --id was given, and
    /// validates the count EXPLICITLY (this is the fix for the --id/text mis-binding bug — see
    /// the class doc-comment). Missing/extra positionals are a usage error; no HTTP call is made.
    /// Testable core for the parsing layer (in addition to <see cref="ExecuteAsync"/>, the
    /// pre-existing testable core for the PATCH behavior itself).
    /// </summary>
    internal static async Task<int> ExecuteFromArgsAsync(
        string[] args,
        string? id,
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        CancellationToken ct = default)
    {
        var output = outputWriter ?? Console.Out;
        string? name;
        string? text;

        if (!string.IsNullOrEmpty(id))
        {
            if (args.Length != 1)
            {
                await output.WriteLineAsync(
                    "Error: with --id, pass exactly one argument — the note text " +
                    "(use \"\" to clear). Usage: korat runtime note --id <runtimeId> \"<text>\"");
                return 1;
            }
            name = null;
            text = args[0];
        }
        else
        {
            if (args.Length != 2)
            {
                await output.WriteLineAsync(
                    "Error: pass the runtime name and the note text (use \"\" to clear), " +
                    "or use --id to skip the name. Usage: korat runtime note \"<name>\" \"<text>\"");
                return 1;
            }
            name = args[0];
            text = args[1];
        }

        return await ExecuteAsync(name, text, id, credentialStore, handlerOverride, outputWriter, ct);
    }

    /// <summary>Testable core. Returns the process exit code (0 = success).</summary>
    internal static async Task<int> ExecuteAsync(
        string? name,
        string? text,
        string? id,
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        CancellationToken ct = default)
    {
        var output = outputWriter ?? Console.Out;
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
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", creds.AccessToken);

        string nodeId;
        if (!string.IsNullOrEmpty(id))
        {
            nodeId = id;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                await output.WriteLineAsync("Error: specify a runtime name or --id.");
                return 1;
            }

            var space = await http.GetFromJsonAsync("/api/space", KoratCliJsonContext.Default.SpaceOverviewResponse, ct);
            var candidates = (space?.Nodes ?? [])
                .Where(n => string.Equals(n.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                await output.WriteLineAsync($"Error: no runtime named '{name}' found.");
                return 1;
            }
            if (candidates.Count > 1)
            {
                var ids = string.Join(", ", candidates.Select(c => c.Id?.Value ?? "?"));
                await output.WriteLineAsync(
                    $"Error: multiple runtimes are named '{name}' (ids: {ids}) — use --id to disambiguate.");
                return 1;
            }
            nodeId = candidates[0].Id?.Value ?? string.Empty;
        }

        // An explicit "" clears the note (mirrors the cloud endpoint: null Note clears it).
        // ExecuteFromArgsAsync above never lets a MISSING text argument reach this point — it
        // errors out first — but treat null defensively the same as "" for direct callers of
        // this testable core.
        var note = string.IsNullOrEmpty(text) ? null : text;

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/nodes/{Uri.EscapeDataString(nodeId)}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new NodeNotePatchRequest(note), KoratCliJsonContext.Default.NodeNotePatchRequest),
                Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            await output.WriteLineAsync($"Error: cloud returned {(int)response.StatusCode} for runtime '{nodeId}'.");
            return 1;
        }

        await output.WriteLineAsync(note is null ? "Note cleared." : $"Note set: {note}");
        return 0;
    }
}
