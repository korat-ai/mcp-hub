using System.Net.Http.Headers;
using Korat.Cloud.Web.Auth.Options;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Production email-change email sender backed by the Resend API.
///
/// SEC-HIGH startup guard: in non-Development environments the Resend API key MUST be
/// configured. The check is enforced in Program.cs (see "SEC-HIGH email-change" comment).
/// If the key is missing in Development or Testing, the link is logged instead of sent —
/// the same dev-convenience pattern as <see cref="ResendEmailSender"/>.
/// </summary>
public sealed class ResendEmailChangeEmailSender(
    HttpClient http,
    IOptions<ResendOptions> options,
    ILogger<ResendEmailChangeEmailSender> logger) : IEmailChangeEmailSender
{
    private static readonly Uri ResendEndpoint = new("https://api.resend.com/emails");

    public async Task SendVerificationLinkAsync(string toEmail, Uri verifyUrl, TimeSpan ttl, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            // Sec M3: redact the raw token from the dev-fallback log so it never appears
            // in plaintext log output (e.g. stdout, log aggregators, crash reporters).
            // The token value is in the `token` query parameter; replace it with "***".
            var redactedUrl = RedactTokenParam(verifyUrl);
            logger.LogWarning(
                "Resend API key missing — email-change verification link not sent to {To} (token redacted)",
                toEmail);
            logger.LogDebug(
                "Redacted verification URL: {RedactedUrl}", redactedUrl);
            return;
        }

        var minutes = (int)ttl.TotalMinutes;
        var html = $"""
            <html><body style="font-family:system-ui,sans-serif;color:#1c1917">
              <p>Someone requested to change the email address on your Korat account to this address.</p>
              <p>Click below to verify and complete the change:</p>
              <p><a href="{verifyUrl}" style="display:inline-block;padding:10px 20px;background:#92400e;color:#fff;text-decoration:none;border-radius:6px">Verify email change</a></p>
              <p>Or paste this link: <code>{verifyUrl}</code></p>
              <p style="color:#78716c">This link expires in {minutes} minutes. If you did not request this, ignore this email — your account is unchanged.</p>
            </body></html>
            """;
        var text = $"Verify your email change for Korat:\n\n{verifyUrl}\n\nExpires in {minutes} minutes.\nIf you did not request this, ignore this email.";

        await SendAsync(toEmail, "Verify your new Korat email address", html, text, opts, ct);
    }

    public async Task SendSecurityAlertAsync(string toEmail, string newEmail, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            logger.LogWarning(
                "Resend API key missing — email-change security alert not sent to {To}", toEmail);
            return;
        }

        var html = $"""
            <html><body style="font-family:system-ui,sans-serif;color:#1c1917">
              <p>The primary email address on your Korat account was changed to <strong>{newEmail}</strong>.</p>
              <p>If you made this change, no action is needed.</p>
              <p>If you did <strong>not</strong> make this change, please contact support immediately.</p>
            </body></html>
            """;
        var text = $"Your Korat account email was changed to {newEmail}.\n\nIf you did not make this change, contact support immediately.";

        await SendAsync(toEmail, "Security alert: your Korat email address was changed", html, text, opts, ct);
    }

    /// <summary>
    /// Replaces the value of the <c>token</c> query parameter with <c>***</c> so that
    /// the raw secret is never emitted to log output. The rest of the URL is preserved
    /// to aid debugging (path, host, other params are not sensitive).
    /// </summary>
    private static Uri RedactTokenParam(Uri url)
    {
        var builder = new UriBuilder(url);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        if (query["token"] is not null)
        {
            query["token"] = "***";
            builder.Query = query.ToString();
        }
        return builder.Uri;
    }

    private async Task SendAsync(
        string toEmail, string subject, string html, string text,
        ResendOptions opts, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ResendEndpoint)
        {
            Content = JsonContent.Create(new
            {
                from = $"{opts.FromName} <{opts.FromEmail}>",
                to = new[] { toEmail },
                subject,
                html,
                text,
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Resend send failed for email-change: {Status} {Body}", (int)response.StatusCode, body);
        }
    }
}
