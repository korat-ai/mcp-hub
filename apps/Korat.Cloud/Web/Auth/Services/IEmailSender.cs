namespace Korat.Cloud.Web.Auth.Services;

public interface IEmailSender
{
    Task SendMagicLinkAsync(string toEmail, Uri consumeUrl, TimeSpan ttl, CancellationToken ct);
}
