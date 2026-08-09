using System.Text.Json.Nodes;
using Korat.Mcp;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>
/// Pure in-memory catalog that merges per-server granted tools and ungranted servers into one
/// MCP tools/list result, and resolves a tool name back to a route.
///
/// Thread-safety strategy: a single lock guards all state. SetGranted/SetUngranted rebuild the
/// cached JSON string under the lock; ToolsListJson and TryResolve read under the same lock.
/// This keeps the implementation simple and correct — contention is negligible because mutations
/// happen on a background poll cycle (seconds apart) while reads are per MCP request.
/// </summary>
public sealed class AggregateCatalog
{
    public const string ControlListToolsName = "korat_space_list_tools";
    public const string ControlCallToolName = "korat_space_call_tool";
    public const string ControlRequestAccessName = "korat_space_request_access";

    private readonly object _lock = new();

    // namespaced-name → (ToolRoute, pre-built JSON tool object)
    private readonly Dictionary<string, (ToolRoute Route, JsonObject ToolNode)> _granted = new();
    private readonly Dictionary<string, (ToolRoute Route, JsonObject ToolNode)> _requestAccess = new();

    // Synthetic upgrade tool — present only when an upgrade is available.
    private (ToolRoute Route, JsonObject ToolNode)? _upgradeTool;

    private static readonly IReadOnlyList<(ToolRoute Route, JsonObject ToolNode)> _controlTools =
    [
        (
            new ToolRoute(RouteKind.ControlListTools, null, null, null),
            new JsonObject
            {
                ["name"] = ControlListToolsName,
                ["description"] = "List the current Korat Space bridge tools. Compatibility path for MCP hosts that do not hot-reload dynamic tools after list_changed.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["prefix"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional tool-name prefix filter, for example 'telegram__' or 'request-access__'.",
                        },
                        ["include_control_tools"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Whether to include the Korat compatibility tools themselves in the result.",
                            ["default"] = false,
                        },
                    },
                },
            }
        ),
        (
            new ToolRoute(RouteKind.ControlCallTool, null, null, null),
            new JsonObject
            {
                ["name"] = ControlCallToolName,
                ["description"] = "Call any currently available namespaced Korat Space tool by name. Use after korat_space_list_tools when the MCP host has not exposed refreshed dynamic tool schemas.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray { (JsonNode)"name" },
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The current aggregate tool name to call, for example 'everything__echo'. Control tools cannot be called recursively.",
                        },
                        ["arguments"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "Arguments for the target tool.",
                            ["additionalProperties"] = true,
                            ["default"] = new JsonObject(),
                        },
                    },
                },
            }
        ),
        (
            new ToolRoute(RouteKind.ControlRequestAccess, null, null, null),
            new JsonObject
            {
                ["name"] = ControlRequestAccessName,
                ["description"] = "Request access to an ungranted Space MCP server by slug or by request-access tool name. Compatibility path for hosts that did not expose a new request-access tool.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray { (JsonNode)"target" },
                    ["properties"] = new JsonObject
                    {
                        ["target"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Server slug such as 'telegram', or full request-access tool name such as 'request-access__telegram'.",
                        },
                    },
                },
            }
        ),
    ];

    // Rebuilt on every mutation (and once at construction to include always-present tools).
    private string _toolsListJson = "";

    public AggregateCatalog()
    {
        // Initialise the JSON so always-present tools (the control tools) appear immediately,
        // even before any SetGranted / SetUngranted / SetUpgradeAvailable call.
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
    public void RemoveGranted(string serverId)
    {
        lock (_lock)
        {
            var stale = _granted
                .Where(kv => kv.Value.Route.ServerId == serverId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _granted.Remove(key);
            Rebuild();
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
            // collapse to the same slug (agent-DX underscore-collapse enlarged this set) get
            // distinct request-access tool names instead of the second silently clobbering
            // the first's dictionary entry. Rebuilt fresh from `servers` every call, so a
            // single per-call `taken` set is sufficient — no cross-call state needed.
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

    /// <summary>Show the update_korat tool iff the cloud advertises a newer CLI. Idempotent.</summary>
    public void SetUpgradeAvailable(bool available, string current, string running)
    {
        lock (_lock)
        {
            _upgradeTool = available
                ? (new ToolRoute(RouteKind.Upgrade, null, null, null), new JsonObject
                  {
                      ["name"] = "update_korat",
                      ["description"] = $"Upgrade the Korat CLI to the latest release ({running} -> {current}). Performs the upgrade; reconnect afterward to use the new version.",
                      ["inputSchema"] = new JsonObject { ["type"] = "object" },
                  })
                : null;
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
            if (name == "update_korat" && _upgradeTool is { } up) { route = up.Route; return true; }
            foreach (var control in _controlTools)
            {
                if (name == control.ToolNode["name"]?.GetValue<string>())
                {
                    route = control.Route;
                    return true;
                }
            }
            route = null;
            return false;
        }
    }

    public static bool IsControlToolName(string name) =>
        name == ControlListToolsName ||
        name == ControlCallToolName ||
        name == ControlRequestAccessName;

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

        if (_upgradeTool is { } up)
            tools.Add((JsonNode)up.ToolNode.DeepClone());

        foreach (var (_, node) in _controlTools)
            tools.Add((JsonNode)node.DeepClone());

        _toolsListJson = new JsonObject { ["tools"] = tools }.ToJsonString();
    }
}
