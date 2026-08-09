namespace Korat.Cloud.Web.Auth.Services;

public enum DeviceCodeStatus { Pending, Approved, Denied, Expired }

public sealed record DeviceCodeEntry(
    string DeviceCode,
    string UserCode,
    DeviceCodeStatus Status,
    Guid? UserId);

public interface IDeviceCodeStore
{
    /// <summary>Creates a new device-code handshake entry valid for <paramref name="ttl"/>.</summary>
    Task<DeviceCodeEntry> CreateAsync(TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Marks the handshake identified by <paramref name="userCode"/> as approved by the given user.
    /// Returns false if the code is unknown or already resolved.
    /// </summary>
    Task<bool> ApproveAsync(string userCode, Guid userId, CancellationToken ct);

    /// <summary>
    /// Marks the handshake identified by <paramref name="userCode"/> as denied.
    /// Returns false if the code is unknown or already resolved.
    /// </summary>
    Task<bool> DenyAsync(string userCode, CancellationToken ct);

    /// <summary>
    /// Non-destructively reads the current status of the entry for <paramref name="deviceCode"/>.
    /// Returns null if the device code is unknown.
    /// Use before <see cref="MarkConsumedAsync"/> so irreversible side effects can be
    /// ordered AFTER the credential is durably issued (prevents lost-approval on issue failure).
    /// </summary>
    Task<DeviceCodeEntry?> GetStatusAsync(string deviceCode, CancellationToken ct);

    /// <summary>
    /// Burns an Approved entry (sets status to Expired, single-use guarantee).
    /// Must be called AFTER <see cref="ICliTokenService.IssueAsync"/> succeeds.
    /// No-op if the entry is not in Approved state.
    /// </summary>
    Task MarkConsumedAsync(string deviceCode, CancellationToken ct);

    /// <summary>
    /// Normalizes a user-typed user_code: trims whitespace, uppercases, and applies
    /// Crockford base32 ambiguous-character folding (I/L → 1, O → 0).
    /// Centralized here so endpoint and store cannot diverge.
    /// </summary>
    static string NormalizeUserCode(string raw) =>
        string.Concat(
            raw.Trim().ToUpperInvariant().Select(c => c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            }));
}
