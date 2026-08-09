namespace Korat.Domain;

/// <summary>
/// Step-C: how long a node must be silent (past presence-stale) before its Active/Opening sessions
/// are persisted Closed, and the pure predicate that decides reapability. Mirrors the read-time
/// staleness derivation in Endpoints.cs (Status in {Active,Opening} AND NOT both nodes online) plus
/// a grace horizon so a brief reconnect blip never reaps a live session.
/// </summary>
/// <remarks>
/// Lives in Korat.Domain so the SQL query (EfMetadataRepository.ListReapableSessionsAsync) and the
/// background SessionReaperService apply ONE interpretation without cross-layer duplication.
/// </remarks>
public static class SessionReaperRules
{
    /// <summary>&gt; <see cref="NodePresenceRules.StaleThreshold"/> (90s) so a reconnect blip never
    /// reaps; short enough that a genuinely dead session is reconciled within the hour.</summary>
    public static readonly TimeSpan ReapGrace = TimeSpan.FromMinutes(15);

    /// <summary>MUST-FIX F2 (adversarial review, second pass, should-fix): default absolute-age
    /// backstop for Space-MCP aggregator-opened (sentinel-client) sessions — see
    /// <c>EfMetadataRepository.ListReapableSessionsAsync</c>'s doc comment for why a sentinel
    /// session (and especially a sentinel×http_cloud session) is otherwise gated out of BOTH of
    /// the other two reap clauses. Deliberately generous (24h, not minutes like <see cref="ReapGrace"/>)
    /// because a sentinel client has no per-session node-liveness signal at all — this is a crude
    /// catch-all net for MUST-FIX 1's own failure modes (crash / best-effort-terminate failure /
    /// shutdown-deadline-canceled terminate), not the primary lifecycle close.</summary>
    public static readonly TimeSpan DefaultSpaceMcpSessionMaxAge = TimeSpan.FromHours(24);

    public static bool IsReapable(
        SessionStatus status,
        DateTimeOffset? clientNodeLastSeen,
        DateTimeOffset? publisherNodeLastSeen,
        DateTimeOffset now,
        TimeSpan grace)
    {
        if (status is not (SessionStatus.Active or SessionStatus.Opening))
            return false;

        var cutoff = now - grace;
        static bool Live(DateTimeOffset? seen, DateTimeOffset cutoff) => seen is { } s && s >= cutoff;

        // Reapable iff EITHER node has been silent past grace (or is missing/never-seen) —
        // the negation of the read-time "both nodes online" liveness check.
        return !(Live(clientNodeLastSeen, cutoff) && Live(publisherNodeLastSeen, cutoff));
    }
}
