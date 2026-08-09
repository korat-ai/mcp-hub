namespace Korat.Domain.Auth;

public sealed record MagicLinkToken
{
    public required Guid Id { get; init; }                    // surrogate PK — NOT the URL token
    /// <summary>
    /// SHA-256 hex of the raw opaque token sent in the sign-in URL.
    /// The raw token lives only in the emailed link — never persisted.
    /// Mirrors the hash-at-rest discipline used by CliToken and EmailChangeToken.
    /// Added nullable for rolling-deploy safety: old silos write null, new silos write hash.
    /// A null here means the row was created by an old silo before this column existed — those
    /// links are ≤1h old and stop working after the new silos take over (owner-approved tradeoff).
    /// </summary>
    public string? TokenHash { get; init; }                   // SHA-256 hex; unique index
    public required string Email { get; init; }               // normalised lowercase plaintext
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }   // 1h from IssuedAt
    public DateTimeOffset? ConsumedAt { get; init; }
    public string? IssuedFromIp { get; init; }
    public string? IssuedUaHash { get; init; }
    public string? ConsumedFromIp { get; init; }
    public string? ConsumedUaHash { get; init; }
}
