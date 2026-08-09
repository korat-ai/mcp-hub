namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a: config for the ONE manually pre-registered MCP OAuth client
/// (section <c>Korat:Cloud:SpaceMcpOAuth</c>; plain singleton record — same binding style as
/// <see cref="Korat.Cloud.Web.Mcp.Space.SpaceMcpOptions"/>, Program.cs:683-686).
/// RedirectUris MUST be configured for the client to be seeded at all — OpenIddict validates
/// that an authorization-code client has at least one redirect URI, so seeding an empty list
/// would throw at startup; the seeder skips (with a warning) instead. Exact-match URIs only
/// (RFC 8252 variable-port loopback matching is an inc-2b/DCR concern — Known Limitations).
/// </summary>
public sealed record SpaceMcpOAuthOptions
{
    public string ClientId { get; init; } = KoratOAuthConstants.DefaultClientId;
    public string DisplayName { get; init; } = "Korat MCP client";
    public string[] RedirectUris { get; init; } = [];
}
