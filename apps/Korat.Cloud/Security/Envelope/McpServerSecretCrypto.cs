using Korat.Domain;

namespace Korat.Cloud.Security.Envelope;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): the AAD label for an http_cloud McpServer's static
/// secret. Distinct from every existing envelope AAD ("pointId.Value" for BYOK/inference,
/// "channel:{id}" for Telegram bot tokens, "msg:{id}" for thread messages) — pinned here so
/// existing ciphertext for those stays decryptable untouched.
///
/// Increment 2 (HTTP MCP OAuth) adds OAuthAad ("mcp-oauth:{id}") — a DISTINCT label so an oauth
/// server's token ciphertext can never be swapped for its own (or another server's) static-secret
/// ciphertext even if both existed on the same row (they cannot in practice — AuthMode is
/// single-valued — but the AAD binding is a defense-in-depth property, not something that depends
/// on that invariant holding forever).
/// </summary>
public static class McpServerSecretCrypto
{
    public static string Aad(McpServerId id) => $"mcp:{id.Value}";

    public static string OAuthAad(McpServerId id) => $"mcp-oauth:{id.Value}";
}
