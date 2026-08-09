using System.Text;
using Korat.Persistence;

namespace Korat.Cloud.Security.Audit;

/// <summary>
/// 032: canonical string construction for the audit hash chain.
///
/// Format (v1):
///   v1|{Seq}|{OccurredAtUtc:O}|{ActorType}|{ActorId}|{AuthKind}|{SpaceId}|{Action}|{TargetType}|{TargetId}|{Outcome}|{DetailsJson}|{TraceId}|{SourceIp}
///
/// Null fields canonicalize to the empty string. Every free-text field is escaped
/// (<c>\</c> → <c>\\</c>, <c>|</c> → <c>\|</c>) so field boundaries are unambiguous —
/// without escaping, an attacker-influenced value containing '|' could produce two
/// different events with the same canonical string (hash-collision-by-construction).
/// Pure + deterministic: unit-tested with golden vectors.
/// </summary>
internal static class AuditCanonical
{
    public const string Version = "v1";

    /// <summary>Escapes a single field value for canonical embedding.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        // Fast path: nothing to escape.
        if (value.IndexOf('\\') < 0 && value.IndexOf('|') < 0)
            return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '|')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Builds the canonical string for a fully-populated event row (Seq already assigned).</summary>
    internal static string Canonicalize(AuditEventRecord r)
    {
        var sb = new StringBuilder(256);
        sb.Append(Version).Append('|');
        sb.Append(r.Seq).Append('|');
        // Round-trip ("O") format: fixed-width, culture-invariant, preserves offset.
        sb.Append(r.OccurredAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Escape(r.ActorType)).Append('|');
        sb.Append(Escape(r.ActorId)).Append('|');
        sb.Append(Escape(r.AuthKind)).Append('|');
        sb.Append(Escape(r.SpaceId)).Append('|');
        sb.Append(Escape(r.Action)).Append('|');
        sb.Append(Escape(r.TargetType)).Append('|');
        sb.Append(Escape(r.TargetId)).Append('|');
        sb.Append(Escape(r.Outcome)).Append('|');
        sb.Append(Escape(r.DetailsJson)).Append('|');
        sb.Append(Escape(r.TraceId)).Append('|');
        sb.Append(Escape(r.SourceIp));
        return sb.ToString();
    }
}
