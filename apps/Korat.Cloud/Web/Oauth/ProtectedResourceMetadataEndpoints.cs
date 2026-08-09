using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Mcp;
using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a, Task 2: RFC 9728 protected-resource metadata, path-scoped per Space
/// (spec §Pillar C). Served for ANY syntactically-plausible segment WITHOUT resolving it —
/// the document is a pure function of the path + origin, so an anonymous prober can never
/// use it to enumerate real Space slugs (the /mcp endpoint itself still 401s before any
/// existence signal). authorization_servers carries the SAME issuer identity the AS metadata
/// document advertises: the explicitly-configured OpenIddict issuer when set (prod), else
/// the resolved public origin (dev/tests) — trailing slash trimmed on both sides so the two
/// documents can never disagree by a '/' (clients compare these strings).
/// </summary>
public static class ProtectedResourceMetadataEndpoints
{
    public static void MapProtectedResourceMetadataEndpoints(this WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource/mcp/{spaceSeg}", (
            string spaceSeg,
            HttpContext ctx,
            IOptions<CliOptions> cliOptions,
            IOptionsMonitor<OpenIddictServerOptions> serverOptions) =>
        {
            var origin = McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions.Value, ctx.Request);
            var issuer = serverOptions.CurrentValue.Issuer is { } configured
                ? configured.AbsoluteUri.TrimEnd('/')
                : origin;
            return Results.Json(new
            {
                resource = $"{origin}/mcp/{spaceSeg}",
                authorization_servers = new[] { issuer },
                scopes_supported = new[] { KoratOAuthConstants.McpScope },
                bearer_methods_supported = new[] { "header" },
            });
        });
    }
}
