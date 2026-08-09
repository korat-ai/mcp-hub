namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Sends the email-change verification link to the new address, and (on confirm) the
/// security alert to the old address.
/// Separate interface from <see cref="IEmailSender"/> so the integration tests can
/// replace only email-change mail without touching the magic-link sender.
/// </summary>
public interface IEmailChangeEmailSender
{
    /// <summary>
    /// Sends a verification link to <paramref name="toEmail"/> (the NEW address).
    /// The link contains the raw token — caller must never persist the raw value.
    /// </summary>
    Task SendVerificationLinkAsync(string toEmail, Uri verifyUrl, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Sends a security-alert email to <paramref name="toEmail"/> (the OLD address) informing
    /// the owner that their primary email was changed.
    /// </summary>
    Task SendSecurityAlertAsync(string toEmail, string newEmail, CancellationToken ct);
}
