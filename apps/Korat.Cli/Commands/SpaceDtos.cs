using System.Text.Json.Serialization;
using Korat.Domain.Contracts;

namespace Korat.Cli.Commands;

/// <summary>
/// Cloud REST API response for <c>GET /api/space</c>.
/// Shared between <see cref="McpListCommand"/> and <see cref="StatusCommand"/>.
/// </summary>
internal sealed class SpaceOverviewResponse
{
    public string DisplayName { get; set; } = string.Empty;
    // 019 rule (shared by doctor + nodes commands): never trust raw node Status; derive effective
    // presence from LastSeenAt age vs PresenceStaleSeconds, exactly like the SPA's indicator.
    // ServerTime is the cloud's own clock at response time — use it as "now" for the age math
    // instead of the caller's possibly-skewed local clock. Both nullable so an old/partial cloud
    // that omits them degrades gracefully.
    public DateTimeOffset? ServerTime { get; set; }
    public int? PresenceStaleSeconds { get; set; }
    public List<NodeDto> Nodes { get; set; } = [];
    public List<McpServerDto> McpServers { get; set; } = [];
}

/// <summary>The API serializes strongly-typed ids as <c>{value:…}</c> objects.</summary>
internal sealed class NodeIdDto
{
    public string Value { get; set; } = string.Empty;
}

internal sealed class NodeDto
{
    public NodeIdDto? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    // #167 review (fix 1): mirrors the cloud's never-seen fallback (LastSeenAt ?? CreatedAt) so
    // `korat nodes prune`'s preview uses the SAME cutoff PruneAgentNodesAsync applies server-side.
    // Nullable for forward/backward compat with a cloud response that omits it, even though this
    // is a same-repo additive change (every other optional-ish field on this DTO is nullable too).
    public DateTimeOffset? CreatedAt { get; set; }
    public string Kind { get; set; } = string.Empty;
    // node-visibility-doctor (2026-07-02): host metadata refreshed on every hello (nullable —
    // legacy CLIs / not-yet-connected nodes report nothing) + owner-editable Note. The doctor's
    // node-presence/agents-stale checks consume Id/LastSeenAt/Kind from this same DTO.
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? CliVersion { get; set; }
    public string? Note { get; set; }
}

internal sealed class McpServerDto
{
    public NodeIdDto? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    // Presence fields used by `mcp list` for the same heartbeat-aware availability rule as the
    // SPA. PublisherNodeId itself is not needed: locality comes from local config.json.
    public bool IsAsserted { get; set; } = true;
    public NodeIdDto? PublisherNodeId { get; set; }
    public string? PublisherNodeStatus { get; set; }
    public DateTimeOffset? PublisherNodeLastSeenAt { get; set; }
    // node-visibility-doctor (2026-07-02): publisher's display name, already resolved server-side
    // (021) — surfaced in `mcp list` as a "via <node>" column so servers are traceable to a host.
    public string? PublisherNodeName { get; set; }
    // Finding 16, M5: Increment 1 (HTTP MCP direct-to-Space) — "Stdio" | "http_cloud". Drives the
    // transport-aware availability branch below and the cloud-terminated suffix.
    public string Transport { get; set; } = "Stdio";
}

/// <summary>#99: machine-readable shape for <c>korat mcp list --json</c>.</summary>
internal sealed class McpListJsonEntry
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Local { get; set; }
    public bool LocalServed { get; set; }
    public string CloudStatus { get; set; } = string.Empty;
    public string CloudAvailability { get; set; } = string.Empty;
    public bool CloudAvailable { get; set; }
    // node-visibility-doctor (2026-07-02): publisher node display name (mirrors the "via <node>"
    // column in the human table); null when there is no cloud row (local-only server).
    public string? Publisher { get; set; }
    public string? Transport { get; set; }
    // Finding 16, M5: lets scripts consuming --json branch on cloud-terminated servers without
    // string-matching Publisher's absence.
    public bool IsCloudTerminated { get; set; }
}

/// <summary>Increment 1 (HTTP MCP direct-to-Space): body for <c>POST /api/mcp-servers</c> from
/// <c>korat mcp add-http</c>. Explicit camelCase JsonPropertyName so the wire shape matches the
/// cloud's CreateHttpMcpServerRequest regardless of this record's C# casing. AuthMode is one of
/// "none", "bearer", "header", or "oauth"; OAuth consent is completed in the console.</summary>
internal sealed record McpAddHttpRequest(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("remoteUrl")] string RemoteUrl,
    [property: JsonPropertyName("authMode")] string AuthMode,
    [property: JsonPropertyName("authHeaderName")] string? AuthHeaderName,
    [property: JsonPropertyName("secret")] string? Secret);

/// <summary>node-visibility-doctor (2026-07-02): machine-readable shape for `korat nodes --json`.</summary>
internal sealed class NodeListJsonEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Host { get; set; }
    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? CliVersion { get; set; }
    // 019: effective status derived from lastSeenAt age vs presenceStaleSeconds — NOT the raw
    // stored Status (a node can still say "Online" for a few seconds after its stream drops).
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>node-visibility-doctor (2026-07-02): body for <c>PATCH /api/nodes/{id}</c>.
/// Null <see cref="Note"/> clears it (mirrors the cloud's PatchNodeRequest).</summary>
internal sealed record NodeNotePatchRequest(string? Note);

/// <summary>#165 (`korat nodes prune`): body for <c>POST /api/nodes/prune</c>.</summary>

/// <summary>#165: response body from <c>POST /api/nodes/prune</c>.</summary>
internal sealed class PruneNodesResponse
{
    public int PrunedCount { get; set; }
    public List<string> PrunedNames { get; set; } = [];
}

/// <summary>#99: machine-readable shape for a single local inference point in
/// <c>korat agent list --json</c>'s <c>localPoints</c> array.</summary>
internal sealed class AgentListJsonEntry
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Id { get; set; }
    public List<string> Models { get; set; } = new();
}

/// <summary>
/// Additive, opt-in JSON shape for <c>korat agent list --json --include-hosted</c>.
/// The plain <c>--json</c> output remains the released bare local-points array.
/// </summary>
internal sealed class AgentListDocument
{
    public List<AgentListJsonEntry> LocalPoints { get; set; } = [];
    public List<AgentDto> HostedAgents { get; set; } = [];
}

/// <summary>
/// Rebrain/roster (2026-07-03): the "brain" block on an <see cref="AgentDto"/> — which inference
/// point/machine/kind/online the hosted agent currently runs on. Mirrors the cloud's
/// ProjectAgentEnriched (GET /api/agents) shape; <c>Machine</c> is null for in-cloud
/// byok/byo_endpoint bases (no publisher node), non-null for headless_agent bases.
/// </summary>
internal sealed class AgentBrainDto
{
    public string PointId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Machine { get; set; }
    public bool Online { get; set; }
}

/// <summary>
/// Rebrain/roster (2026-07-03): a hosted agent as returned by <c>GET /api/agents</c> (enriched
/// list, Task 4) and <c>PATCH /api/agents/{id}</c> (Task 3 — <see cref="Brain"/>/<see cref="Tools"/>
/// are absent from the PATCH response and simply default to null/empty when deserialized here).
/// Distinct from a locally-registered <see cref="InferencePointIdentity"/> — a hosted agent is the
/// persona+façade that USES an inference point as its "brain"; see AgentListCommand's roster
/// section for where the two are joined for display.
/// </summary>
internal sealed class AgentDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PersonaPrompt { get; set; }
    public string BasePointId { get; set; } = string.Empty;
    public string FacadePointId { get; set; } = string.Empty;
    public bool MemoryEnabled { get; set; }
    public bool ToolsEnabled { get; set; }
    public string Identity { get; set; } = string.Empty;
    /// <summary>Owner-editable team-roster label (Task 1's Agent.Role). Null/empty = unset.</summary>
    public string? Role { get; set; }
    public string Status { get; set; } = string.Empty;
    /// <summary>Review fix (resilience/observability): the sanitized error from this agent's
    /// most recent FAILED turn ("&lt;utc&gt;: &lt;message&gt;"), cleared on the next success.
    /// Null when healthy / never turned.</summary>
    public string? LastTurnError { get; set; }
    /// <summary>Null when the base point no longer resolves (foreign/deleted point).</summary>
    public AgentBrainDto? Brain { get; set; }
    public List<string> Tools { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Rebrain (2026-07-03): body for <c>PATCH /api/agents/{id}</c> re-pointing the base
/// point. Explicit camelCase JsonPropertyName so the wire shape matches the cloud's
/// PatchAgentRequest.BasePointId regardless of this record's C# casing.</summary>
internal sealed record AgentRebrainPatchRequest(
    [property: JsonPropertyName("basePointId")] string BasePointId);

/// <summary>Roster (2026-07-03): body for <c>PATCH /api/agents/{id}</c> setting/clearing the
/// role label. An empty string clears it (mirrors the cloud's PatchAgentRequest.Role contract);
/// a missing/omitted Role leaves it unchanged — callers here always send an explicit value.</summary>
internal sealed record AgentRolePatchRequest(
    [property: JsonPropertyName("role")] string Role);

/// <summary>Candidate-brain resolution (2026-07-03): one point from <c>GET /api/inference-points</c>
/// — the <c>points</c> array element shape (see InferenceManagementEndpoints). Only the fields
/// AgentRebrainCommand needs to resolve a brain by name and filter to invokable kinds.</summary>
internal sealed class InferencePointDto
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string AgentKind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Response body from <c>GET /api/inference-points</c>.</summary>
internal sealed class InferencePointListResponse
{
    public string SpaceSlug { get; set; } = string.Empty;
    public List<InferencePointDto> Points { get; set; } = [];
}

/// <summary>#98: machine-readable shape for <c>korat status --json</c>.</summary>
internal sealed class StatusDocument
{
    public string RuntimeId { get; set; } = string.Empty;
    // Released alias retained for script compatibility.
    public string NodeId { get; set; } = string.Empty;
    public string CloudUrl { get; set; } = string.Empty;
    public string? SpaceName { get; set; }
    public int RuntimesOnline { get; set; }
    public int RuntimesTotal { get; set; }
    // Released aliases retained for script compatibility. These now count publisher runtimes,
    // not synthetic consumer-identity rows.
    public int NodesOnline { get; set; }
    public int NodesTotal { get; set; }
    public int McpServersAvailable { get; set; }
    public int McpServersTotal { get; set; }
    public int DeclaredServerCount { get; set; }
    public bool CloudReachable { get; set; }
    public string? CloudError { get; set; }
}
