namespace Korat.Domain.Auth;

/// <summary>
/// Long-lived, revocable CLI credential issued via the OAuth 2.0 device flow (SP4).
/// Stored hashed at rest; the raw token is shown to the operator exactly once.
/// Resolves to a real <see cref="User"/> (and via SP2 ISpaceResolver, a Space).
/// </summary>
public sealed record CliToken
{
    public required Guid Id { get; init; }
    public required UserId UserId { get; init; }
    public required string TokenHash { get; init; }       // SHA-256 hex; unique
    public required string Scope { get; init; }           // "full" | "bridge-only"
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}
