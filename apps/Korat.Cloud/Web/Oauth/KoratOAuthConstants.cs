namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a (spec §Pillar C, SF-7): names for Korat's own OAuth AS surface. The
/// dedicated MCP resource scope is deliberately DISJOINT from the identity scopes
/// (openid/email/profile) the future-OIDC side of this AS also serves — an MCP client may
/// request ONLY <see cref="McpScope"/> (enforced by the consent handler, Task 3), so a rogue
/// client + one phished consent can never mint Korat identity tokens.
/// </summary>
public static class KoratOAuthConstants
{
    /// <summary>The one scope an MCP client may request (SF-7).</summary>
    public const string McpScope = "korat:mcp";

    /// <summary>Access-token claim: the 32-hex SpaceId this consent was granted FOR. One half
    /// of the BLOCKER-1 check at the resource server (consent-Space == path-Space).</summary>
    public const string SpaceClaim = "korat:space";

    /// <summary>Access-token claim: the client_id the consent was granted TO — one input of
    /// the durable consumer-identity derivation (client_id × ownerUserId × SpaceId).</summary>
    public const string ClientClaim = "korat:client";

    /// <summary>OpenIddict authorization Properties key carrying the consented SpaceId —
    /// lets the console list/revoke consents per Space (Task 8) and consent-reuse match
    /// per (subject, client, Space) (Task 4).</summary>
    public const string AuthorizationSpaceProperty = "korat:space";

    /// <summary>The single manually pre-registered MCP client of inc-2a (no DCR yet).</summary>
    public const string DefaultClientId = "korat-mcp";

    /// <summary>Server-assigned client_id prefix for DCR-registered clients (RFC 7591). Lets
    /// logs/console tell an auto-registered client apart from the one pre-registered
    /// <see cref="DefaultClientId"/> at a glance. Not security-load-bearing (the DCR marker
    /// property is), just a readable convention.</summary>
    public const string DcrClientIdPrefix = "dcr_";

    /// <summary>OpenIddict application Properties key marking a row as DCR-created. Its PRESENCE
    /// is how the TTL sweep (inc-2b Task 6) finds DCR clients and — critically — never touches
    /// the seeded pre-registered client or any future OIDC client, which never carry it.</summary>
    public const string DcrMarkerProperty = "korat:dcr";

    /// <summary>OpenIddict application Properties key holding the DCR registration instant
    /// (ISO-8601 "O"). Stored by us because OpenIddict's application entity has NO creation-date
    /// column (verified grounding #1); the TTL sweep reads it to age out never-consented rows.</summary>
    public const string DcrRegisteredAtProperty = "korat:dcr:registered_at";

    /// <summary>The RFC 7591 registration endpoint path. Advertised in AS metadata (Task 2) and
    /// mapped as a minimal API (Task 4). NOT an OpenIddict-claimed URI — this app owns it.</summary>
    public const string RegistrationEndpointPath = "/connect/register";
}
