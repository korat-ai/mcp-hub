namespace Korat.Domain.Auth;

/// <summary>
/// Pending email-change verification token. Stores only the SHA-256 hash of the raw token;
/// the raw value lives only in the verification link sent to the new address.
/// TTL is enforced at the application layer (30 minutes). Only one active pending token per
/// user is kept; a new request supersedes (marks SupersededAt on) any prior pending token
/// rather than hard-deleting it, so superseded rows still count toward the per-user
/// rate-limit window (see EmailChangeService step 4 and step 3).
/// </summary>
public sealed record EmailChangeToken
{
    public Guid Id { get; init; }
    public required UserId UserId { get; init; }
    /// <summary>The new email address awaiting verification (normalised lowercase).</summary>
    public required string NewEmail { get; init; }
    /// <summary>SHA-256 hex of the raw random token. Never store or log the raw value.</summary>
    public required string TokenHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Set to the UTC timestamp when the token was consumed (single-use guard).</summary>
    public DateTimeOffset? ConsumedAt { get; init; }
    /// <summary>
    /// Set when a subsequent email-change request supersedes this token (keeps the record for
    /// rate-limit counting without blocking re-requests). Superseded tokens are inactive but
    /// count toward the per-user issuance window to prevent request-storm abuse.
    /// </summary>
    public DateTimeOffset? SupersededAt { get; init; }
}
