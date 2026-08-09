using System.Text.Json;

namespace Korat.Cloud.Mcp.Oauth;

/// <summary>
/// Increment 2 (HTTP MCP OAuth): the single JSON document encrypted as one ciphertext under
/// McpServerSecretCrypto.OAuthAad(serverId) — access/refresh tokens, the negotiated token
/// endpoint + issuer (needed to refresh after a restart, since neither is stored anywhere else),
/// and the DCR-issued (or owner-supplied manual) client credentials. One document, one
/// ciphertext — atomic rotation, no field-swap-at-rest (spec §"State model").
/// </summary>
public sealed record McpOAuthTokenDocument(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset AccessExpiry,
    string TokenEndpoint,
    string? Issuer,
    string ClientId,
    string? ClientSecret)
{
    private static readonly JsonSerializerOptions Options = new();

    public static string Serialize(McpOAuthTokenDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static McpOAuthTokenDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<McpOAuthTokenDocument>(json, Options)
        ?? throw new FormatException("Malformed OAuth token document.");

    /// <summary>
    /// Redacted ToString (T1 opus-gate defense-in-depth). This record flows through heavily-logged
    /// paths — the OAuth callback (Task 4) and HttpMcpProxyGrain refresh (Task 5). The auto-generated
    /// positional-record ToString would print AccessToken/RefreshToken/ClientSecret verbatim, so a
    /// single structured <c>LogError(ex, "...{Doc}", doc)</c> or a captured exception would emit them
    /// to logs/Sentry — against the "tokens never logged" Global Constraint. Drop the three secret
    /// members here (at the type definition, before any consumer exists to trip it); keep the
    /// non-sensitive fields + presence booleans for diagnostics.
    /// </summary>
    public override string ToString() =>
        $"McpOAuthTokenDocument {{ ClientId = {ClientId}, AccessExpiry = {AccessExpiry:o}, " +
        $"TokenEndpoint = {TokenEndpoint}, Issuer = {Issuer}, " +
        $"HasRefreshToken = {RefreshToken is not null}, HasClientSecret = {ClientSecret is not null} }}";
}
