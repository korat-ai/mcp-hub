using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a, Task 1: idempotent upsert of the single pre-registered MCP OAuth client
/// (spec §Increments Inc-2a — "manually pre-registered client", NO open DCR until inc-2b).
/// Runs on every boot right after migrations (Program.cs), and from the test fixture
/// (KoratIntegrationFixture.EnsureOAuthClientAsync) through the same descriptor builder, so
/// tests and production can never drift on the client's shape.
///
/// The client is PUBLIC (no secret — Cursor/Claude/Codex are public clients), PKCE-required
/// (both the global RequireProofKeyForCodeExchange() server option AND the per-client
/// ft:pkce requirement — defense in depth), consent-explicit, and scope-limited to
/// korat:mcp ONLY (SF-7: never the identity scopes).
/// </summary>
public static class SpaceMcpOAuthClientSeeder
{
    public static async Task SeedAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<SpaceMcpOAuthOptions>();
        if (options.RedirectUris.Length == 0)
        {
            logger.LogWarning(
                "Space-MCP OAuth client '{ClientId}' NOT seeded: Korat:Cloud:SpaceMcpOAuth:RedirectUris is empty. " +
                "Configure the MCP client's exact redirect URI(s) to enable the OAuth flow.", options.ClientId);
            return;
        }
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await UpsertAsync(manager, options, ct);
    }

    public static async Task UpsertAsync(
        IOpenIddictApplicationManager manager, SpaceMcpOAuthOptions options, CancellationToken ct)
    {
        if (options.RedirectUris.Length == 0)
            return; // see SeedAsync's warning path — an authorization-code client needs >=1 URI.

        var descriptor = BuildDescriptor(options);
        var existing = await manager.FindByClientIdAsync(options.ClientId, ct);
        if (existing is null)
            await manager.CreateAsync(descriptor, ct);
        else
            await manager.UpdateAsync(existing, descriptor, ct); // converge config changes (e.g. redirect URIs)
    }

    public static OpenIddictApplicationDescriptor BuildDescriptor(SpaceMcpOAuthOptions options)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = options.ClientId,
            DisplayName = options.DisplayName,
            ClientType = ClientTypes.Public,          // no client_secret — PKCE carries the proof
            ConsentType = ConsentTypes.Explicit,      // every (client × Space) consent is owner-approved
        };
        foreach (var uri in options.RedirectUris)
            descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + KoratOAuthConstants.McpScope, // korat:mcp ONLY — no openid/email/profile
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        return descriptor;
    }
}
