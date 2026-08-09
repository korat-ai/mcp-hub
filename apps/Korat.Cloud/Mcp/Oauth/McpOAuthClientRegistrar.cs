using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;

namespace Korat.Cloud.Mcp.Oauth;

/// <summary>DCR (RFC 7591) result — client_secret is null for a public/PKCE-only client.</summary>
public sealed record McpOAuthClientRegistration(string ClientId, string? ClientSecret);

/// <summary>
/// Increment 2 (HTTP MCP OAuth): Dynamic Client Registration (RFC 7591) against the AS's
/// registration_endpoint, discovered by McpOAuthDiscoveryService. redirect_uris is ALWAYS exactly
/// one entry — the per-server callback URI (https://PublicOrigin/api/mcp/oauth/callback/{serverId}
/// — see McpOAuthConnectActionBuilder, Task 4). This is the load-bearing mix-up defense (spec
/// §"Security → Mix-up"): a real AS can only ever redirect to ITS OWN registered client's
/// redirect_uri, so a mix-up attacker's redirect back through a DIFFERENT server's callback path
/// is rejected purely by the path/serverId mismatch, independent of whether iss is ever emitted.
/// The manual client_id/client_secret fallback (when the AS has no registration_endpoint) is
/// handled entirely by the CALLER (McpOAuthConnectActionBuilder) — this class is only ever
/// invoked when DCR is actually attempted.
///
/// MINOR #10 (fable plan-review) — token-endpoint auth method assumption, spelled out: this class
/// requests `token_endpoint_auth_method: "none"` (public/PKCE-only client), but some ASes still
/// issue a `client_secret` anyway (the stub server in this plan's tests does — see
/// `StartStubAuthorizationServerAsync`'s `/register` route). When that happens,
/// `McpOAuthTokenExchange.PostAsync` (Task 4) sends it back as a BODY parameter
/// (`client_secret_post`-shaped), never as an HTTP Basic `Authorization` header
/// (`client_secret_basic`). This is correct for Miro and any public-PKCE-style client, which is
/// this increment's only driving target — but a CONFIDENTIAL-client AS that specifically requires
/// `client_secret_basic` (HTTP Basic auth at the token endpoint) is NOT supported by this
/// increment and would need its own follow-up (an explicit `token_endpoint_auth_methods_supported`
/// negotiation, reading the AS metadata Task 2 already fetches).
/// </summary>
public sealed class McpOAuthClientRegistrar(IOutboundHttpClientFactory httpClientFactory, ILogger<McpOAuthClientRegistrar> logger)
{
    private const long MaxResponseBytes = 262_144;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public async Task<McpOAuthClientRegistration> RegisterAsync(string registrationEndpoint, string redirectUri, CancellationToken ct)
    {
        var ssrfError = SsrfGuard.ValidateUrl(registrationEndpoint);
        if (ssrfError is not null)
            throw new McpOAuthDiscoveryException($"registration_endpoint is not allowed: {ssrfError}");

        using var http = httpClientFactory.CreateClient("mcp-oauth-dcr");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint);
        var body = JsonSerializer.Serialize(new
        {
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
            client_name = "Korat Cloud",
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new McpOAuthDiscoveryException("Could not reach the registration endpoint.");
        }
        using (response)
        {
            // Bound the body read by the same 15s cts (not the caller's bare ct) — otherwise a
            // slowloris peer that answers headers instantly then trickles bytes under the 256 KB
            // cap can hold the pinned connection open indefinitely (neither the 15s timeout nor
            // the factory's 600s HttpClient.Timeout covers a streamed body read under
            // ResponseHeadersRead). Same fix as McpOAuthDiscoveryService.FetchJsonAsync (T2 gate).
            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxResponseBytes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new McpOAuthDiscoveryException("Timed out reading the registration response.");
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("DCR rejected registrationEndpoint={Endpoint} status={Status}", registrationEndpoint, (int)response.StatusCode);
                throw new McpOAuthDiscoveryException($"Client registration was rejected (HTTP {(int)response.StatusCode}).");
            }

            JsonNode json;
            try { json = JsonNode.Parse(bytes) ?? throw new FormatException(); }
            catch (Exception) { throw new McpOAuthDiscoveryException("Client registration returned malformed JSON."); }

            var clientId = RequireString(json["client_id"], "Client registration response is missing client_id.");
            var clientSecret = OptionalString(json["client_secret"]);
            return new McpOAuthClientRegistration(clientId, clientSecret);
        }
    }

    /// <summary>
    /// Type-safe string extraction from an attacker-controlled JSON node: missing key (null node),
    /// wrong JSON type (object/array), and JSON null all fall through to the same clean
    /// McpOAuthDiscoveryException instead of JsonNode.GetValue&lt;string&gt;()'s uncaught
    /// InvalidOperationException on a type mismatch. LOCAL copy of the same helper in
    /// McpOAuthDiscoveryService (T2 gate) — kept local rather than shared to keep this fix focused
    /// and avoid re-verifying already-gated code.
    /// </summary>
    private static string RequireString(JsonNode? node, string errorMessage) =>
        node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : throw new McpOAuthDiscoveryException(errorMessage);

    /// <summary>Same type-safety as <see cref="RequireString"/> but for an optional field: a
    /// missing key or a wrong-type value is treated as absent rather than as an error.</summary>
    private static string? OptionalString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new McpOAuthDiscoveryException("Registration response exceeded the size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
