using System.Text.Json;

namespace Korat.Cloud.Security.Audit;

/// <summary>
/// Deferred-fix (consistency): the single source of truth for building
/// <see cref="AuditEvent.DetailsJson"/>. Previously call sites mixed hand-rolled string
/// interpolation (no escaping) with ad-hoc <see cref="JsonSerializer"/> calls (default
/// options) — one unescaped quote in a value would have produced malformed JSON in the
/// audit row.
///
/// DETERMINISM CONTRACT (hash-chain relevant): AuditLogger hashes DetailsJson as an OPAQUE
/// string at write time (AuditCanonical embeds the stored string verbatim), so chain
/// verification of EXISTING rows is unaffected by producer changes. For NEW rows the output
/// must still be deterministic per call site so re-serialization debates never arise:
///   - compact output (no indentation),
///   - property order = declaration order of the (anonymous) payload type — stable,
///   - default <c>JavaScriptEncoder</c> — strict, culture-independent escaping,
///   - no naming policy: property names are emitted exactly as written at the call site
///     (all call sites use camelCase names matching the previous hand-rolled strings, e.g.
///     <c>prunedThroughSeq</c>/<c>prunedThroughHash</c> which AuditVerifier.ResolveSeedAsync
///     parses back).
/// </summary>
public static class AuditDetails
{
    /// <summary>
    /// Shared options instance. Deliberately default-constructed: compact, ordinal property
    /// order, strict default encoder, no naming policy. Do not add <c>WriteIndented</c> or a
    /// naming policy — see the determinism contract in the class summary.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new();

    /// <summary>Serializes an audit-details payload with the shared deterministic options.</summary>
    public static string Json<T>(T payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}
