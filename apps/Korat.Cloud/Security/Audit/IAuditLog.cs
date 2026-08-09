namespace Korat.Cloud.Security.Audit;

/// <summary>
/// 032: one audit event as supplied by a call site. Seq / hashes / timestamp / trace
/// enrichment are added by <see cref="AuditLogger"/>.
///
/// SECURITY: <see cref="DetailsJson"/> must NEVER contain secret values, KEK/DEK bytes,
/// or raw tokens. The logger deny-scrubs it as defence-in-depth, but call sites are the
/// first line — pass ids and structural metadata only.
/// </summary>
public sealed record AuditEvent(
    string Action,
    string TargetType,
    string TargetId,
    string Outcome = AuditOutcomes.Success,
    string? SpaceId = null,
    string ActorType = AuditActorTypes.System,
    string ActorId = "system",
    string? AuthKind = null,
    string? DetailsJson = null);

/// <summary>
/// 032 (#57 Leg 3 C1): append-only, tamper-evident audit log.
///
/// Failure policy contract:
/// <list type="bullet">
/// <item><c>required: true</c> (privileged mutations) — a failed audit write THROWS; the caller's
/// HTTP request surfaces 500. The audit row is written after the operation succeeds (own
/// transaction); the "op applied but audit failed" window is alarmed via GlitchTip
/// (<c>audit.write_failed</c>) and accepted for v1 — see specs/032-leg3-hardening/plan.md §1.4.</item>
/// <item><c>required: false</c> (hot-path reads: secret.decrypt; best-effort events) — failures are
/// swallowed, logged critical, and alarmed. Availability of the data path wins.</item>
/// </list>
/// </summary>
public interface IAuditLog
{
    /// <summary>Records one event. Returns the assigned chain Seq, or null when a best-effort write failed.</summary>
    Task<long?> RecordAsync(AuditEvent auditEvent, bool required, CancellationToken ct = default);
}
