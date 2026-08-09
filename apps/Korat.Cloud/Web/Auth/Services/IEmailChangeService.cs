namespace Korat.Cloud.Web.Auth.Services;

/// <summary>Result of a successful email-change request.</summary>
public enum EmailChangeRequestStatus
{
    Success,
    EmailAlreadyInUse,
    RateLimited,
    /// <summary>
    /// The requested new email is identical to the user's current primary email.
    /// Distinct from <see cref="EmailAlreadyInUse"/> to avoid the confusing response
    /// of telling users their own address is "already in use".
    /// </summary>
    SameAsCurrentEmail,
    /// <summary>The email address failed syntactic validation.</summary>
    InvalidEmailFormat,
}

/// <summary>
/// Status returned from <see cref="IEmailChangeService.ConfirmAsync"/>.
/// </summary>
public enum EmailChangeConfirmStatus
{
    Success,
    /// <summary>
    /// The token was not found, is expired, already consumed, or superseded.
    /// These cases are collapsed into a single status to avoid revealing timing differences.
    /// </summary>
    ExpiredOrInvalid,
}

/// <summary>Result payload from <see cref="IEmailChangeService.ConfirmAsync"/> on success.</summary>
public sealed record EmailChangeConfirmResult(
    EmailChangeConfirmStatus Status,
    string? NewEmail = null);

/// <summary>
/// Handles the email-change request and confirm flows.
/// </summary>
public interface IEmailChangeService
{
    /// <summary>
    /// Initiates an email-change verification for <paramref name="userId"/>.
    /// Stores a hashed single-use token (30-min TTL) and sends the verification link
    /// to <paramref name="newEmail"/>. Any prior pending token for the user is
    /// superseded (marked SupersededAt, retained for rate-limit accounting) rather than
    /// deleted — so superseded rows still count toward the per-user issuance window.
    /// </summary>
    /// <returns>
    /// <see cref="EmailChangeRequestStatus.Success"/> on success;
    /// <see cref="EmailChangeRequestStatus.EmailAlreadyInUse"/> if another user already owns <paramref name="newEmail"/>;
    /// <see cref="EmailChangeRequestStatus.RateLimited"/> if the user has exceeded the per-hour request cap.
    /// </returns>
    /// <exception cref="Exception">
    /// Propagates any exception thrown by the email sender (transient mail failure). The token
    /// row is persisted but the link was not delivered; callers should return a retryable error
    /// rather than 202-success so the user knows to try again.
    /// </exception>
    Task<EmailChangeRequestStatus> RequestAsync(
        Korat.Domain.Auth.UserId userId,
        string newEmail,
        Uri appBaseUri,
        CancellationToken ct);

    /// <summary>
    /// Confirms an email-change by validating <paramref name="rawToken"/> against the stored hash.
    /// On success: promotes the new email to <c>User.PrimaryEmail</c> in the database,
    /// sends a security-alert email to the old address, and marks the token consumed.
    /// The caller (endpoint) must subsequently call
    /// <c>IUserGrain.UpdatePrimaryEmailAsync</c> to keep the grain's in-memory cache consistent.
    /// </summary>
    /// <param name="userId">The authenticated user attempting the confirmation.</param>
    /// <param name="rawToken">The raw (un-hashed) token from the verification link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="EmailChangeConfirmResult"/> with <see cref="EmailChangeConfirmStatus.Success"/> and
    /// the new email on success; <see cref="EmailChangeConfirmStatus.ExpiredOrInvalid"/> otherwise.
    /// </returns>
    Task<EmailChangeConfirmResult> ConfirmAsync(
        Korat.Domain.Auth.UserId userId,
        string rawToken,
        CancellationToken ct);
}
