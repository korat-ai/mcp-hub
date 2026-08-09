using System.Text.Json.Nodes;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Space;

/// <summary>A backend server descriptor as consumed by <see cref="AggregateCatalog.SetUngranted"/>
/// — just enough to render a "request access to X" synthetic tool.</summary>
public sealed record ServerDescriptor(string Id, string DisplayName);

/// <summary>
/// Space-MCP (increment 1, Task 4 — plan-review correction B2 moves this file's creation here
/// from Task 5, since Task 4's <c>SpaceMcpAggregatorGrain.InitializeAsync</c> already needs a
/// <c>_catalog</c> to populate). Server-side port of
/// <c>apps/Korat.Cli/Mcp/Aggregation/AggregateCatalog.cs</c>, DROPPING the CLI-only synthetic
/// tools (<c>_feedbackTool</c> "submit_feedback" / <c>_upgradeTool</c> "update_korat" — CLI-daemon
/// concepts with no meaning for a shared cloud aggregator session; there is no CLI process to
/// upgrade and no maintainer feedback channel scoped to one MCP session). Everything else —
/// <c>_granted</c>/<c>_requestAccess</c>/<c>SetGranted</c>/<c>RemoveGranted</c>/
/// <c>SetUngranted</c>/<c>ToolsListJson</c>/<c>TryResolve</c>/<c>Rebuild</c> — is kept verbatim,
/// including the CLI's own locking-strategy rationale: a single lock guards all state; mutations
/// happen on backend-open/close/poll events (Task 5 concurrent-open, Task 8 revalidation poll)
/// while reads are per MCP request, so contention is negligible.
///
/// Task 5 (S9): each <c>SpaceMcpAggregatorGrain</c> activation owns its OWN
/// <see cref="AggregateCatalog"/> instance — no cross-session sharing. The cloud grain is a
/// shared service with N concurrent sessions (unlike the CLI's single-consumer/sequential/stdio
/// reference), so a per-session catalog is load-bearing, not an incidental detail: two Space-MCP
/// sessions must never see each other's granted-tool namespacing collide or leak.
/// </summary>
public sealed class AggregateCatalog
{
    private readonly object _lock = new();

    // namespaced-name → (ToolRoute, pre-built JSON tool object)
    private readonly Dictionary<string, (ToolRoute Route, JsonObject ToolNode)> _granted = new();
    private readonly Dictionary<string, (ToolRoute Route, JsonObject ToolNode)> _requestAccess = new();
    // Kept separately because an MCP server may legitimately expose zero tools. Reconcile uses
    // this set to distinguish "never cataloged" from "cataloged but temporarily unavailable"
    // without reopening offline mobile nodes on every timer tick.
    private readonly HashSet<string> _knownGrantedServerIds = new(StringComparer.Ordinal);

    // Rebuilt on every mutation (and once at construction, mirroring the CLI's shape even though
    // there are no always-present synthetic tools here — an empty tools/list is a valid result).
    private string _toolsListJson = "";

    public AggregateCatalog()
    {
        lock (_lock) Rebuild();
    }

    /// <summary>
    /// Register or replace the granted tools for one backend server.
    /// Each tool becomes a namespaced MCP tool prefixed with [displayName] in its description.
    /// </summary>
    public void SetGranted(string serverId, string slug, string displayName, IReadOnlyList<ToolInfo> tools)
    {
        lock (_lock)
        {
            _knownGrantedServerIds.Add(serverId);

            // Remove stale entries for this server.
            var stale = _granted
                .Where(kv => kv.Value.Route.ServerId == serverId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _granted.Remove(key);

            foreach (var tool in tools)
            {
                var toolNode = new JsonObject
                {
                    ["name"] = tool.NamespacedName,
                    ["description"] = $"[{displayName}] {tool.Description ?? ""}",
                    ["inputSchema"] = tool.Schema is not null
                        ? (JsonObject)tool.Schema.DeepClone()
                        : new JsonObject { ["type"] = "object" }
                };
                var route = new ToolRoute(RouteKind.Tool, tool.Slug, tool.OriginalName, serverId);
                _granted[tool.NamespacedName] = (route, toolNode);
            }

            Rebuild();
        }
    }

    /// <summary>
    /// Drop all granted tools belonging to <paramref name="serverId"/> and rebuild the cached JSON.
    /// </summary>
    public bool RemoveGranted(string serverId)
    {
        lock (_lock)
        {
            var stale = _granted
                .Where(kv => kv.Value.Route.ServerId == serverId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _granted.Remove(key);
            var knownServerRemoved = _knownGrantedServerIds.Remove(serverId);
            var changed = stale.Count > 0 || knownServerRemoved;
            if (changed) Rebuild();
            return changed;
        }
    }

    /// <summary>Whether this server has a cached granted catalog, including an empty tool list.</summary>
    public bool HasGrantedServer(string serverId)
    {
        lock (_lock) return _knownGrantedServerIds.Contains(serverId);
    }

    /// <summary>
    /// Removes cached granted catalogs whose server is no longer present in the authoritative
    /// discovery snapshot. This also prunes offline servers, which have no live backend entry for
    /// the ordinary reconcile close loop to inspect.
    /// </summary>
    public bool RemoveGrantedExcept(IReadOnlySet<string> grantedServerIds)
    {
        lock (_lock)
        {
            var staleServerIds = _knownGrantedServerIds
                .Where(id => !grantedServerIds.Contains(id))
                .ToList();
            if (staleServerIds.Count == 0) return false;

            foreach (var serverId in staleServerIds)
                _knownGrantedServerIds.Remove(serverId);

            var staleTools = _granted
                .Where(kv => kv.Value.Route.ServerId is { } id && !grantedServerIds.Contains(id))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleTools) _granted.Remove(key);

            Rebuild();
            return true;
        }
    }

    /// <summary>
    /// Replace the full set of ungranted servers. For each server a synthetic
    /// "request-access" tool is emitted so the LLM can ask for access.
    /// </summary>
    public void SetUngranted(IReadOnlyList<ServerDescriptor> servers)
    {
        lock (_lock)
        {
            _requestAccess.Clear();

            // UniqueSlug (not the bare Slug) so two ungranted servers whose display names
            // collapse to the same slug get distinct request-access tool names instead of the
            // second silently clobbering the first's dictionary entry. Rebuilt fresh from
            // `servers` every call, so a single per-call `taken` set is sufficient.
            var taken = new HashSet<string>();
            foreach (var s in servers)
            {
                var sSlug = ToolNamespacer.UniqueSlug(s.DisplayName, s.Id, taken);
                var name = ToolNamespacer.RequestAccessTool(sSlug);
                var toolNode = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = $"Request access to the '{s.DisplayName}' MCP server (creates an access request for the owner to approve).",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" }
                };
                var route = new ToolRoute(RouteKind.RequestAccess, sSlug, null, s.Id);
                _requestAccess[name] = (route, toolNode);
            }

            Rebuild();
        }
    }

    /// <summary>Returns the JSON result object <c>{ "tools": [...] }</c> for MCP tools/list.</summary>
    public string ToolsListJson()
    {
        lock (_lock) return _toolsListJson;
    }

    /// <summary>Resolves a namespaced tool name to a route; returns false if unknown.</summary>
    public bool TryResolve(string name, out ToolRoute? route)
    {
        lock (_lock)
        {
            if (_granted.TryGetValue(name, out var g)) { route = g.Route; return true; }
            if (_requestAccess.TryGetValue(name, out var ra)) { route = ra.Route; return true; }
            route = null;
            return false;
        }
    }

    // Must be called under _lock. Rebuilds _toolsListJson from the current dictionaries.
    private void Rebuild()
    {
        var tools = new JsonArray();

        // Cast to JsonNode so the non-generic JsonArray.Add(JsonNode?) overload is bound
        // rather than the RequiresUnreferencedCode generic Add<T> — DeepClone already returns
        // a JsonNode, so this is allocation-free and trim-safe (silences IL2026).
        foreach (var (_, (_, node)) in _granted)
            tools.Add((JsonNode)node.DeepClone());

        foreach (var (_, (_, node)) in _requestAccess)
            tools.Add((JsonNode)node.DeepClone());

        _toolsListJson = new JsonObject { ["tools"] = tools }.ToJsonString();
    }
}
