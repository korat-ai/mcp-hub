namespace Korat.Domain.Contracts;

/// <summary>
/// Р32: the request body of <c>POST /api/nodes/prune</c>, defined once.
///
/// <para>It previously existed twice: a class on the Cloud side (with the validation rules in its
/// doc comments) and a positional record on the CLI side, <c>(string Kind, int OlderThanDays)</c>.
/// The two disagreed on nullability — the CLI's <c>OlderThanDays</c> was non-nullable, so it could
/// not express "omit and take the default", while the server's contract says omitted means 30.
/// Two shapes for one wire format is exactly how a client ends up unable to say something the
/// server supports.</para>
/// </summary>
public sealed class PruneNodesRequest
{
    /// <summary>
    /// Node kind to prune. v1 ONLY allows "agent" — publisher nodes host MCP servers and are never
    /// bulk-deletable (the owner deletes a publisher's servers explicitly instead).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Prune agent nodes not seen (or, if never seen, not created) in at least this many days.
    /// Must be &gt;= 1. Null/omitted defaults to 30.
    /// </summary>
    public int? OlderThanDays { get; set; }
}
