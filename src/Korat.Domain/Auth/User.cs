namespace Korat.Domain.Auth;

public sealed record User
{
    public required UserId Id { get; init; }
    public required string PrimaryEmail { get; init; }        // normalised lowercase
    public string? DisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required UserStatus Status { get; init; }
    public required bool IsAdmin { get; init; }
}
