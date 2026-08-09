using System.Net.Http.Headers;
using Korat.Cloud.Web.Auth.Options;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Auth.Services;

public sealed class ResendEmailSender(
    HttpClient http,
    IOptions<ResendOptions> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    private static readonly Uri ResendEndpoint = new("https://api.resend.com/emails");

    public async Task SendMagicLinkAsync(string toEmail, Uri consumeUrl, TimeSpan ttl, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            // Dev convenience: log the link instead of failing the signin flow when Resend is unconfigured.
            logger.LogWarning("Resend API key missing — magic-link not sent. URL would have been: {Url}", consumeUrl);
            return;
        }

        var html = BuildHtml(consumeUrl, ttl);
        var text = BuildText(consumeUrl, ttl);

        var request = new HttpRequestMessage(HttpMethod.Post, ResendEndpoint)
        {
            Content = JsonContent.Create(new
            {
                from = $"{opts.FromName} <{opts.FromEmail}>",
                to = new[] { toEmail },
                subject = "Sign in to Korat",
                html,
                text,
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Resend send failed: {Status} {Body}", (int)response.StatusCode, body);
            // Do NOT throw — we returned 204 to the user already (anti-enumeration). Log and move on.
        }
    }

    private static string BuildHtml(Uri url, TimeSpan ttl) => $$"""
        <html><body style="font-family:system-ui,sans-serif;color:#1c1917">
          <p>Click the button below to sign in to Korat:</p>
          <p><a href="{{url}}" style="display:inline-block;padding:10px 20px;background:#92400e;color:#fff;text-decoration:none;border-radius:6px">Sign in to Korat</a></p>
          <p>Or paste this link into your browser:<br><code>{{url}}</code></p>
          <p style="color:#78716c">This link expires in {{(int)ttl.TotalMinutes}} minutes. If you didn't request it, ignore this email.</p>
        </body></html>
        """;

    private static string BuildText(Uri url, TimeSpan ttl) =>
        $"Sign in to Korat:\n\n{url}\n\nThis link expires in {(int)ttl.TotalMinutes} minutes.\nIf you didn't request it, ignore this email.";
}
