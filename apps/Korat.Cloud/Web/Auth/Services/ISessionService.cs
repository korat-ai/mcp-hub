using Korat.Domain.Auth;

namespace Korat.Cloud.Web.Auth.Services;

public record SessionBumpResult(UserId UserId, DateTimeOffset ExpiresAt);

public interface ISessionService
{
    Task<LoginSession> CreateAsync(UserId userId, string? userAgent, string? ip, CancellationToken ct);
    Task<SessionBumpResult?> ValidateAndBumpAsync(Guid sessionId, CancellationToken ct);
    Task RevokeAsync(Guid sessionId, CancellationToken ct);
    /// <summary>Revoke all of the user's active sessions except <paramref name="exceptSessionId"/> (current device).</summary>
    Task RevokeOthersAsync(UserId userId, Guid exceptSessionId, CancellationToken ct);
    Task<IReadOnlyList<LoginSession>> ListActiveAsync(UserId userId, CancellationToken ct);
}
