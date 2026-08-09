using System.Text.Json.Nodes;

namespace Korat.Mcp;

/// <summary>
/// Р32: how a namespaced aggregate tool name resolves.
///
/// <para>The CLI aggregator emits every member; the Cloud's Space-MCP endpoint emits only
/// <see cref="Tool"/> and <see cref="RequestAccess"/> and deliberately drops the rest — a shared
/// cloud session has no CLI process to upgrade and no per-session control tools to expose. The
/// enum is shared anyway rather than split: it is one vocabulary with two speakers, and splitting
/// it would mean two <c>RouteKind</c> types whose members must be kept in correspondence by hand,
/// which is the failure this decision exists to remove.</para>
/// </summary>
public enum RouteKind
{
    /// <summary>A live backend tool call.</summary>
    Tool,
    /// <summary>The synthetic "request access to X" stub for an ungranted server.</summary>
    RequestAccess,
    /// <summary>CLI only: the synthetic "update korat" tool, present when an upgrade is available.</summary>
    Upgrade,
    /// <summary>CLI only: compatibility tool listing for hosts that do not hot-reload dynamic tools.</summary>
    ControlListTools,
    /// <summary>CLI only: call an aggregate tool by name, for hosts without refreshed schemas.</summary>
    ControlCallTool,
    /// <summary>CLI only: request access through the compatibility path.</summary>
    ControlRequestAccess,
}

/// <summary>
/// Р32: where a namespaced tool name routes. <see cref="ServerId"/> is always the plain
/// (non-namespaced) <c>McpServerId.Value</c>; <see cref="Slug"/> identifies the backend session in
/// the aggregator's per-slug index; <see cref="OriginalName"/> is the tool name as the backend
/// itself knows it (null for <see cref="RouteKind.RequestAccess"/>).
///
/// <para>Named ToolRoute, not Route: the Cloud host imports
/// <c>Microsoft.AspNetCore.Routing</c>, whose own <c>Route</c> would collide. A bare "Route" was
/// also simply less true — this routes a tool NAME, it is not an HTTP route.</para>
/// </summary>
public sealed record ToolRoute(RouteKind Kind, string? Slug, string? OriginalName, string? ServerId);

/// <summary>
/// Р32: one tool as an aggregator knows it — the namespaced name it is exposed under, the name the
/// backend answers to, and the schema/description to republish.
/// </summary>
public sealed record ToolInfo(
    string NamespacedName,
    string OriginalName,
    string Slug,
    JsonObject? Schema,
    string? Description);
