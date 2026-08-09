using Korat.Cloud.Web.Auth.Security;

namespace Korat.Cloud.Web.Mcp.Space;

/// <summary>
/// Space-MCP (increment 1): the per-Space Streamable-HTTP MCP responder,
/// <c>POST/GET/DELETE /mcp/{spaceSeg}</c> (design §, Global Constraint "Per-Space resource
/// URL"). Anonymous at the ASP.NET level exactly like <c>/inference/{spaceSeg}/...</c>
/// (<see cref="Korat.Cloud.Web.Inference.InferenceEndpoints"/>) — Bearer validation happens
/// inside <see cref="SpaceMcpDispatcher"/> (via <see cref="SpaceMcpAuth"/>), not an ASP.NET auth
/// handler, so a 403/404 can distinguish "wrong scope" from "unknown/foreign Space" without
/// leaking existence.
///
/// Task 1 mapped only <c>POST</c> and stubbed a successful authentication as
/// <c>501 Not Implemented</c>. Task 7 replaces that stub with the real responder: all three
/// verbs delegate to <see cref="SpaceMcpDispatcher"/>, which writes status/headers/body directly
/// onto <see cref="HttpContext.Response"/> for every branch (success and failure alike) — mirrors
/// <see cref="SpaceMcpAuth"/>'s own "write the status, return" convention, so every route handler
/// here just awaits the dispatcher and returns <see cref="Results.Empty"/>.
/// </summary>
public static class SpaceMcpEndpoints
{
    public static void MapSpaceMcpEndpoints(this WebApplication app)
    {
        app.MapPost("/mcp/{spaceSeg}",
            async (string spaceSeg, HttpContext ctx, SpaceMcpDispatcher dispatcher, CancellationToken ct) =>
            {
                await dispatcher.HandlePostAsync(ctx, spaceSeg, ct);
                return Results.Empty;
            })
            .RequireRateLimiting(RateLimiterRegistration.InferencePreAuthPolicy);

        app.MapGet("/mcp/{spaceSeg}",
            async (string spaceSeg, HttpContext ctx, SpaceMcpDispatcher dispatcher, CancellationToken ct) =>
            {
                await dispatcher.HandleGetAsync(ctx, spaceSeg, ct);
                return Results.Empty;
            })
            .RequireRateLimiting(RateLimiterRegistration.InferencePreAuthPolicy);

        app.MapDelete("/mcp/{spaceSeg}",
            async (string spaceSeg, HttpContext ctx, SpaceMcpDispatcher dispatcher, CancellationToken ct) =>
            {
                await dispatcher.HandleDeleteAsync(ctx, spaceSeg, ct);
                return Results.Empty;
            })
            .RequireRateLimiting(RateLimiterRegistration.InferencePreAuthPolicy);
    }
}
