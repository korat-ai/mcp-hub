using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cli;
using Korat.Cli.Auth;
using Korat.Cli.Service;

namespace Korat.Cli.Commands;

public static class McpListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List MCP servers with local (💻) + cloud (☁️) status");

        // #99: alternate output modes for alignment-sensitive terminals and tooling.
        var noEmojiOption = new Option<bool>("--no-emoji",
            "Use ASCII symbols instead of emoji (better for alignment-sensitive terminals)");
        var jsonOption = new Option<bool>("--json",
            "Emit machine-readable JSON instead of the human table");
        var showIdsOption = new Option<bool>("--ids",
            "Show server IDs in human output (IDs are always included in --json)");

        command.AddOption(noEmojiOption);
        command.AddOption(jsonOption);
        command.AddOption(showIdsOption);
        command.SetHandler(ListAsync, noEmojiOption, jsonOption, showIdsOption);
        return command;
    }

    private static async Task ListAsync(bool noEmoji, bool outputJson, bool showIds)
    {
        var credStore = new CredentialStore();
        await ExecuteAsync(credentialStore: credStore, handlerOverride: null, outputWriter: null,
            localIdentity: null, serviceStatus: null, ct: default, noEmoji: noEmoji,
            outputJson: outputJson, showIds: showIds);
    }

    /// <summary>
    /// Testable core. Merges two views per server:
    ///   💻  local daemon leg — shown only for servers THIS machine publishes (present in local
    ///       config, or cloud publisherNodeId == local NodeId): ✅ served (in config + service
    ///       running) / ⏸ declared but daemon not running.
    ///   ☁️  cloud leg (from GET /api/space) — ✅ available (Published + asserted + publisher
    ///       Online) / 💤 unavailable (Published but not asserted or publisher offline) /
    ///       ⛔ disabled / — not in the cloud catalog (local-only).
    /// <paramref name="localIdentity"/> / <paramref name="serviceStatus"/> are injectable for
    /// tests; null → load real config / query the real service controller.
    /// </summary>
    internal static async Task ExecuteAsync(
        CredentialStore? credentialStore,
        HttpMessageHandler? handlerOverride,
        TextWriter? outputWriter,
        LocalIdentity? localIdentity = null,
        ServiceStatus? serviceStatus = null,
        CancellationToken ct = default,
        bool noEmoji = false,
        bool outputJson = false,
        bool showIds = false)
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
        var cloud = space?.McpServers ?? [];
        var serverTime = space?.ServerTime;
        var staleSeconds = space?.PresenceStaleSeconds;

        // Local view: which servers this machine declares + is the daemon running.
        var identity = localIdentity ?? new LocalIdentityStore().LoadOrCreate();
        var localNames = new HashSet<string>(
            identity.McpServers.Select(s => s.DisplayName), StringComparer.OrdinalIgnoreCase);
        bool serviceRunning;
        if (serviceStatus is not null)
            serviceRunning = serviceStatus.IsRunning;
        else
        {
            var ctrl = ServiceCommand.TryGetController();
            serviceRunning = ctrl is not null && (await ctrl.GetStatusAsync(ct)).IsRunning;
        }

        // Preserve one row per cloud server. Display names are not unique (ConnectCommand
        // explicitly handles that case), so a name-keyed union would hide all but one server
        // and make `--ids` unable to provide the IDs needed for disambiguation.
        //
        // Current clouds expose PublisherNodeId, allowing exact locality. For an older cloud
        // that omits it, fall back to the historic name match for stdio servers only.
        var rows = cloud
            .Select(server => (
                Name: server.DisplayName,
                Server: (McpServerDto?)server,
                IsLocal: IsPublishedByLocalRuntime(server, identity.NodeId, localNames)))
            .ToList();

        // A locally declared server may not have reached Cloud yet (for example, the service is
        // stopped). Add a local-only row unless the catalog already contains that same server
        // from this publisher runtime.
        foreach (var localName in localNames)
        {
            var represented = cloud.Any(server =>
                string.Equals(server.DisplayName, localName, StringComparison.OrdinalIgnoreCase)
                && IsPublishedByLocalRuntime(server, identity.NodeId, localNames));
            if (!represented)
                rows.Add((localName, null, true));
        }

        rows = rows
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Server?.Id?.Value, StringComparer.Ordinal)
            .ToList();

        if (rows.Count == 0)
        {
            if (outputJson)
                await output.WriteLineAsync("[]");
            else
                await output.WriteLineAsync("No servers.");
            return;
        }

        // #99: JSON output — stable machine-readable shape for tooling.
        if (outputJson)
        {
            var entries = new List<McpListJsonEntry>();
            foreach (var row in rows)
            {
                var s = row.Server;
                entries.Add(new McpListJsonEntry
                {
                    Id = s?.Id?.Value,
                    Name = row.Name,
                    Local = row.IsLocal,
                    LocalServed = row.IsLocal && serviceRunning,
                    CloudStatus = s?.Status ?? "absent",
                    CloudAvailability = GetCloudAvailability(s, staleSeconds, serverTime),
                    CloudAvailable = IsCloudAvailable(s, staleSeconds, serverTime),
                    Publisher = s?.PublisherNodeName,
                    Transport = s?.Transport,
                    // Finding 16, M5: lets scripts consuming --json branch on cloud-terminated
                    // servers without string-matching Publisher's absence.
                    IsCloudTerminated = string.Equals(s?.Transport, "http_cloud", StringComparison.OrdinalIgnoreCase),
                });
            }
            var json = JsonSerializer.Serialize(entries, KoratCliJsonContext.Default.ListMcpListJsonEntry);
            await output.WriteLineAsync(json);
            return;
        }

        var width = rows.Max(row => row.Name.Length);
        foreach (var row in rows)
        {
            var s = row.Server;

            var legs = new List<string>();
            if (row.IsLocal)
            {
                var localGlyph = serviceRunning ? "✅" : "⏸";
                legs.Add((noEmoji ? "local:" : "💻:") + (noEmoji ? AsciiGlyph(localGlyph) : localGlyph));
            }
            var cloudGlyph = CloudGlyph(s, staleSeconds, serverTime);
            legs.Add((noEmoji ? "cloud:" : "☁️:") + (noEmoji ? AsciiGlyph(cloudGlyph) : cloudGlyph));

            // node-visibility-doctor (2026-07-02): "где кто запущен" — trace a server back to its
            // host. Plain text (no emoji variant needed) so --no-emoji alignment is unaffected.
            var line = $"{row.Name.PadRight(width)}  {string.Join("  ", legs)}";
            if (string.Equals(s?.Transport, "http_cloud", StringComparison.OrdinalIgnoreCase))
                // Finding 16, M5 / spec §11 decision 3: disclosed, always — no publisher node to
                // report "via" for (there is none).
                line += "  (cloud-terminated)";
            else if (!string.IsNullOrEmpty(s?.PublisherNodeName))
                line += $"  via {s.PublisherNodeName}";
            if (string.Equals(s?.Status, "NeedsReauth", StringComparison.OrdinalIgnoreCase))
                line += "  (needs reauth)";
            if (showIds && !string.IsNullOrEmpty(s?.Id?.Value))
                line += $"  id {s.Id.Value}";

            await output.WriteLineAsync(line);
        }

        // #99: legend footer so glyphs are self-explanatory.
        await output.WriteLineAsync(string.Empty);
        if (noEmoji)
            await output.WriteLineAsync("Legend: local/cloud  [ok]=available  [||]=declared/unavailable  [reauth]=needs reauthorization  [x]=disabled  [-]=absent");
        else
            await output.WriteLineAsync("Legend: 💻=local  ☁️=cloud  ✅=available  ⏸/💤=unavailable  🔒=needs reauthorization  ⛔=disabled  —=absent  (use --no-emoji for ASCII)");
    }

    private static bool IsPublishedByLocalRuntime(
        McpServerDto server,
        string localRuntimeId,
        HashSet<string> localNames)
    {
        if (!string.IsNullOrEmpty(server.PublisherNodeId?.Value))
        {
            return string.Equals(
                server.PublisherNodeId.Value,
                localRuntimeId,
                StringComparison.Ordinal);
        }

        return !string.Equals(server.Transport, "http_cloud", StringComparison.OrdinalIgnoreCase)
            && localNames.Contains(server.DisplayName);
    }

    /// <summary>#99: maps an emoji status glyph to an ASCII token preserving meaning.</summary>
    private static string AsciiGlyph(string glyph) => glyph switch
    {
        "✅" => "[ok]",
        "⏸" => "[||]",
        "💤" => "[||]",
        "⛔" => "[x]",
        "🔒" => "[reauth]",
        "—" => "[-]",
        _ => glyph,
    };

    /// <summary>Whether the cloud row represents an available (Published + asserted + Online,
    /// or — Finding 16, M5 — Published for http_cloud, which has no publisher node) server.</summary>
    private static bool IsCloudAvailable(
        McpServerDto? s,
        int? presenceStaleSeconds,
        DateTimeOffset? serverTime)
        => GetCloudAvailability(s, presenceStaleSeconds, serverTime) == "Available";

    internal static string GetCloudAvailability(
        McpServerDto? s,
        int? presenceStaleSeconds,
        DateTimeOffset? serverTime)
    {
        if (s is null) return "Absent";
        if (string.Equals(s.Status, "Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
        if (string.Equals(s.Status, "NeedsReauth", StringComparison.OrdinalIgnoreCase)) return "NeedsReauth";
        if (string.Equals(s.Transport, "http_cloud", StringComparison.OrdinalIgnoreCase))
            return string.Equals(s.Status, "Published", StringComparison.OrdinalIgnoreCase)
                ? "Available"
                : "Unavailable";

        // Older clouds did not return heartbeat timestamps/serverTime. Preserve their raw-status
        // behaviour only when both pieces of presence metadata are absent; current clouds always
        // send serverTime, so a missing lastSeenAt correctly fails closed.
        var publisherOnline = s.PublisherNodeLastSeenAt is null && serverTime is null
            ? string.Equals(s.PublisherNodeStatus, "Online", StringComparison.OrdinalIgnoreCase)
            : NodesCommand.DeriveEffectiveStatus(
                s.PublisherNodeStatus ?? "Offline",
                s.PublisherNodeLastSeenAt,
                presenceStaleSeconds,
                serverTime ?? DateTimeOffset.UtcNow) == "Online";

        return string.Equals(s.Status, "Published", StringComparison.OrdinalIgnoreCase)
            && s.IsAsserted
            && publisherOnline
                ? "Available"
                : "Unavailable";
    }

    /// <summary>☁️ glyph from the cloud row: ✅ available / 💤 unavailable / ⛔ disabled / — absent.</summary>
    private static string CloudGlyph(
        McpServerDto? s,
        int? presenceStaleSeconds,
        DateTimeOffset? serverTime)
    {
        return GetCloudAvailability(s, presenceStaleSeconds, serverTime) switch
        {
            "Absent" => "—",
            "Disabled" => "⛔",
            "NeedsReauth" => "🔒",
            "Available" => "✅",
            _ => "💤",
        };
    }
}
