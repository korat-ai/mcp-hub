namespace Korat.Domain.Auth;

/// <summary>Сессия входа в аккаунт — значение cookie. Не путать с relay-сессией
/// (<see cref="Korat.Domain.Entities.RelaySession"/>), которая туннелирует agent → MCP-сервер.</summary>
public sealed record LoginSession
{
    public required Guid Id { get; init; }                    // = cookie value
    public required UserId UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }   // sliding cap
    public required DateTimeOffset AbsoluteExpiresAt { get; init; } // CreatedAt + 90d
    public string? UserAgent { get; init; }
    public string? CreatedFromIp { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}
