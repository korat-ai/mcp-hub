namespace Korat.Domain;

/// <summary>
/// 024: how long a <c>Published</c> MCP server whose owner node has gone silent is kept before
/// the background reaper hard-deletes it (catalog hygiene). This is the LONG purge horizon —
/// distinct from <see cref="NodePresenceRules.StaleThreshold"/> (90s), which only drives the
/// display/admission "offline" decision. A node that returns within this window keeps its servers.
/// </summary>
public static class McpServerReaperRules
{
    public static readonly TimeSpan PurgeThreshold = TimeSpan.FromDays(7);
}
