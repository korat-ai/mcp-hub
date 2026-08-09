using System.Security.Cryptography;
using System.Text;

namespace Korat.Cloud.Mcp.Oauth;

/// <summary>
/// Increment 2 (HTTP MCP OAuth): PKCE (RFC 7636) verifier/challenge/state generation, and
/// injection-safe authorizeUrl construction. Uses UriBuilder + System.Web.HttpUtility.
/// ParseQueryString (the SAME idiom already used in this codebase —
/// apps/Korat.Cloud/Web/Auth/Services/ResendEmailChangeEmailSender.cs) rather than naive string
/// concatenation with "?", which would silently double up when authorization_endpoint already
/// carries a query string (some ASes' do).
/// </summary>
public static class McpOAuthPkce
{
    public static string GenerateVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32)); // -> 43 chars

    public static string Challenge(string verifier) => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string GenerateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Builds the AS authorize URL with response_type=code, PKCE S256, state, and the RFC 8707
    /// resource parameter (the MCP server's canonical RemoteUrl). Injection-safe against an
    /// authorization_endpoint that already carries its own query string.
    /// </summary>
    public static string BuildAuthorizeUrl(
        string authorizationEndpoint, string clientId, string redirectUri, string state,
        string codeChallenge, string resource)
    {
        var builder = new UriBuilder(authorizationEndpoint);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = redirectUri;
        query["state"] = state;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["resource"] = resource;
        builder.Query = query.ToString();
        return builder.Uri.ToString();
    }
}
