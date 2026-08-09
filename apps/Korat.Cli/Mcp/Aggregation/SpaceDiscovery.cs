using System.Text.Json;
using Korat.Cli.Commands;

namespace Korat.Cli.Mcp.Aggregation;

public sealed record ServerDescriptor(string Id, string DisplayName, bool IsAsserted);
public sealed record SpaceSnapshot(IReadOnlyList<ServerDescriptor> Granted, IReadOnlyList<ServerDescriptor> Ungranted);

public static class SpaceDiscovery
{
    public static async Task<SpaceSnapshot> DiscoverAsync(HttpClient http, string agentClientId, CancellationToken ct)
    {
        var activeServerIds = await FetchActiveGrantServerIdsAsync(http, agentClientId, ct);

        using var spaceResp = await http.GetAsync("api/space", ct);
        spaceResp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await spaceResp.Content.ReadAsStreamAsync(ct), default, ct);

        var granted = new List<ServerDescriptor>();
        var ungranted = new List<ServerDescriptor>();
        if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
        {
            foreach (var s in servers.EnumerateArray())
            {
                if (!s.TryGetProperty("status", out var st) || st.GetString() != "Published") continue;
                if (!s.TryGetProperty("displayName", out var dn)) continue;
                var id = ConnectCommand.ReadId(s.GetProperty("id"));
                if (id is null) continue;
                var asserted = !s.TryGetProperty("isAsserted", out var a) || a.GetBoolean();
                var desc = new ServerDescriptor(id, dn.GetString() ?? id, asserted);
                (activeServerIds.Contains(id) ? granted : ungranted).Add(desc);
            }
        }
        return new SpaceSnapshot(granted, ungranted);
    }

    private static async Task<HashSet<string>> FetchActiveGrantServerIdsAsync(HttpClient http, string agentClientId, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var resp = await http.GetAsync("api/grants", ct);
        if (!resp.IsSuccessStatusCode) return ids;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            if (g.TryGetProperty("status", out var st) && st.GetString() != "Active") continue;
            var ac = g.TryGetProperty("agentClientId", out var acEl) ? ConnectCommand.ReadId(acEl) : null;
            if (!string.Equals(ac, agentClientId, StringComparison.Ordinal)) continue;
            var sid = g.TryGetProperty("mcpServerId", out var sidEl) ? ConnectCommand.ReadId(sidEl) : null;
            if (sid is not null) ids.Add(sid);
        }
        return ids;
    }
}
