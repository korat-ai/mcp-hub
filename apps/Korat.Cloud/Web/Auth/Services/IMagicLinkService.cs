namespace Korat.Cloud.Web.Auth.Services;

public record MagicLinkConsumeResult(string Email, bool ForensicsDivergence);

public interface IMagicLinkService
{
    Task IssueAsync(string email, string? ip, string? uaHash, Uri appBaseUri, CancellationToken ct);
    /// <param name="rawToken">
    /// The opaque token from the email URL query string.
    /// The service hashes it internally and looks up by hash — the raw value is never stored.
    /// </param>
    Task<MagicLinkConsumeResult?> TryConsumeAsync(string rawToken, string? ip, string? uaHash, CancellationToken ct);
}
