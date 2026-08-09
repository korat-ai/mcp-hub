namespace Korat.Domain.Auth;

public sealed record ExternalLogin
{
    public required Guid Id { get; init; }
    public required UserId UserId { get; init; }
    public required LoginProvider Provider { get; init; }
    public required string ProviderUserId { get; init; }
    public required string EmailAtLink { get; init; }
    public required bool EmailVerified { get; init; }
    public required DateTimeOffset LinkedAt { get; init; }
}
