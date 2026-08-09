using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Auth.Endpoints;

public static class MagicLinkEndpoints
{
    public static IEndpointRouteBuilder MapMagicLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /signin/magic-link  — request a magic-link email.
        // Antiforgery is validated via RequireAntiforgeryValidation() filter (single source of truth
        // shared with all other auth JSON POSTs — see RequireAntiforgeryExtensions.cs).
        app.MapPost("/signin/magic-link", async (
            HttpContext ctx,
            IMagicLinkService magicLink,
            IOptions<CliOptions> cliOpts,
            IWebHostEnvironment env,
            [FromBody] MagicLinkRequest body,
            CancellationToken ct) =>
        {
            // Validate Origin header against the canonical app host.
            //
            // Derive the canonical host from a trusted configured origin when available
            // (host-header injection defence: AllowedHosts="*" means the Host header is not
            // validated, so deriving the canonical host from req.Host would let an attacker
            // who can spoof the Host neutralise the Origin equality check — and poison the
            // appBase used in the verification email). In Development/Testing only, fall back
            // to req.Scheme://req.Host which is acceptable for local use. Mirrors the
            // CliOptions.PublicOrigin pattern in CliDeviceEndpoints.cs / AuthApiEndpoints.cs.
            var publicOrigin = cliOpts.Value.PublicOrigin;
            string canonicalHost;
            if (!string.IsNullOrEmpty(publicOrigin))
            {
                canonicalHost = publicOrigin.TrimEnd('/');
            }
            else if (env.IsDevelopment() || env.IsEnvironment("Testing"))
            {
                canonicalHost = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            }
            else
            {
                // Non-Development without PublicOrigin: fall back but warn.
                canonicalHost = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("MagicLink")
                    .LogWarning(
                        "Magic-link Origin check and verification link built from request Host " +
                        "header because Korat:Cli:PublicOrigin is not configured. Set PublicOrigin " +
                        "to prevent host-header injection.");
            }

            var origin = ctx.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && origin != canonicalHost)
                return Results.BadRequest(new { error = "bad-origin" });

            if (string.IsNullOrWhiteSpace(body.Email))
                return Results.NoContent();  // anti-enumeration: same 204 regardless

            var appBase = new Uri(canonicalHost);
            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            var ua = ctx.Request.Headers.UserAgent.ToString();
            var uaHash = string.IsNullOrEmpty(ua) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua)).AsSpan(0, 16));

            await magicLink.IssueAsync(body.Email, ip, uaHash, appBase, ct);
            return Results.NoContent();
        }).WithName("RequestMagicLink")
          .RequireRateLimiting(RateLimiterRegistration.MagicLinkRequestPolicy)
          .RequireAntiforgeryValidation();

        // GET /signin/magic-link/consume?token=...
        app.MapGet("/signin/magic-link/consume", async (
            HttpContext ctx,
            IMagicLinkService magicLink,
            CanonicalSigninHandler canonical,
            CancellationToken ct) =>
        {
            // F5: token is now an opaque string (not a Guid). The service hashes it
            // internally and never stores the raw value — only the SHA-256 hash at rest.
            var rawToken = ctx.Request.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(rawToken))
                return Results.Content(BuildExpiredHtml(), "text/html");

            var ip = ctx.Connection.RemoteIpAddress?.ToString();
            var ua = ctx.Request.Headers.UserAgent.ToString();
            var uaHash = string.IsNullOrEmpty(ua) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ua)).AsSpan(0, 16));

            var consumed = await magicLink.TryConsumeAsync(rawToken, ip, uaHash, ct);
            if (consumed is null) return Results.Content(BuildExpiredHtml(), "text/html");

            // ProviderUserId for magic-link is the SHA256 hash of the consumed email (hex).
            // Simplified from plan's tautological ternary (SHA256 always produces 32 bytes).
            var providerUserId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(consumed.Email)));

            return await canonical.CompleteAsync(ctx, new CanonicalSigninRequest(
                Provider: Korat.Domain.Auth.LoginProvider.MagicLink,
                ProviderUserId: providerUserId,
                Email: consumed.Email,
                EmailVerified: true,
                DisplayName: null,
                ReturnUrl: "/app/"), ct);
        }).WithName("ConsumeMagicLink").RequireRateLimiting(RateLimiterRegistration.MagicLinkConsumePolicy);

        return app;
    }

    private static string BuildExpiredHtml() => """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Link expired</title></head>
        <body style="font-family:system-ui,sans-serif;max-width:480px;margin:48px auto;color:#1c1917">
          <h1 style="font-size:20px">Link expired or already used</h1>
          <p style="color:#78716c">Magic links expire 1 hour after they're sent and can only be used once.</p>
          <p><a href="/app/signin" style="display:inline-block;padding:8px 16px;background:#92400e;color:#fff;text-decoration:none;border-radius:6px">Request a new link</a></p>
        </body></html>
        """;

    public sealed record MagicLinkRequest(string Email);
}
