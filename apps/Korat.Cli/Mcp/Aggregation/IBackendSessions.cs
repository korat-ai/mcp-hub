using System.Text.Json.Nodes;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>The backend-session operations the aggregator MCP server needs (seam for testing).</summary>
internal interface IBackendSessions
{
    Task<string> CallAsync(string namespacedName, string argsJson, JsonNode idNode, CancellationToken ct);
    Task<AccessRequestResult> RequestAccessAsync(string serverId, CancellationToken ct);
}

/// <summary>Outcome of asking the cloud to open/grant a session for an ungranted server.</summary>
public sealed record AccessRequestResult(bool AlreadyGranted, string? AccessRequestId);
