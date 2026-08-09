using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;

namespace Korat.Cloud.Mcp.Oauth;

public sealed record McpOAuthTokenResult(string AccessToken, string? RefreshToken, DateTimeOffset AccessExpiry);

/// <summary>A definitive AS rejection (error=invalid_grant) — the refresh/authorization grant is
/// permanently dead. HttpMcpProxyGrain (Task 5) maps this, and ONLY this, to NeedsReauth.</summary>
public sealed class McpOAuthInvalidGrantException(string message) : Exception(message);

/// <summary>Network failure, 5xx, timeout, or a malformed token response — transient. Task 5
/// must NOT flip Status on this (a one-hour AS outage must not brick every server into
/// re-consent).</summary>
public sealed class McpOAuthTransientTokenException(string message) : Exception(message);

/// <summary>
/// Increment 2 (HTTP MCP OAuth): the shared authorization_code / refresh_token POST to
/// token_endpoint, used by BOTH the callback endpoint (Task 4, authorization_code) and
/// HttpMcpProxyGrain's refresh logic (Task 5, refresh_token) — one implementation, one failure-
/// classification policy, consumed identically by both callers.
/// </summary>
public static class McpOAuthTokenExchange
{
    private const long MaxResponseBytes = 262_144;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    public static Task<McpOAuthTokenResult> ExchangeAuthorizationCodeAsync(
        IOutboundHttpClientFactory httpClientFactory, string tokenEndpoint, string code, string codeVerifier,
        string redirectUri, string clientId, string? clientSecret, string resource, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = resource,
        };
        return PostAsync(httpClientFactory, tokenEndpoint, form, clientSecret, ct);
    }

    public static Task<McpOAuthTokenResult> RefreshAsync(
        IOutboundHttpClientFactory httpClientFactory, string tokenEndpoint, string refreshToken,
        string clientId, string? clientSecret, string resource, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["resource"] = resource,
        };
        return PostAsync(httpClientFactory, tokenEndpoint, form, clientSecret, ct);
    }

    private static async Task<McpOAuthTokenResult> PostAsync(
        IOutboundHttpClientFactory httpClientFactory, string tokenEndpoint, Dictionary<string, string> form,
        string? clientSecret, CancellationToken ct)
    {
        var ssrfError = SsrfGuard.ValidateUrl(tokenEndpoint);
        if (ssrfError is not null)
            throw new McpOAuthDiscoveryException($"token_endpoint is not allowed: {ssrfError}");
        if (!string.IsNullOrEmpty(clientSecret))
            form["client_secret"] = clientSecret;

        using var http = httpClientFactory.CreateClient("mcp-oauth-token");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new McpOAuthTransientTokenException("Could not reach the token endpoint.");
        }
        using (response)
        {
            // Bound the body read by the same cts (not the caller's bare ct) — otherwise a
            // slowloris peer that answers headers instantly then trickles bytes under the 256 KB
            // cap can hold the pinned connection open indefinitely (neither the CallTimeout nor
            // the factory's HttpClient.Timeout covers a streamed body read under
            // ResponseHeadersRead). Same fix as McpOAuthDiscoveryService.FetchJsonAsync and
            // McpOAuthClientRegistrar.RegisterAsync (T2/T3 opus gates).
            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxResponseBytes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new McpOAuthTransientTokenException("Timed out reading the token endpoint's response.");
            }
            if (!response.IsSuccessStatusCode)
            {
                // Already wrong-type-safe: GetValue<string>() failing here is caught by this same
                // try/catch(Exception), which correctly falls through to the transient-error path
                // below rather than crashing — no T2/T3-style hardening needed for this one field.
                string? error = null;
                try { error = JsonNode.Parse(bytes)?["error"]?.GetValue<string>(); }
                catch (Exception) { /* malformed error body — treat as transient below */ }

                if (error == "invalid_grant")
                    throw new McpOAuthInvalidGrantException("The authorization server rejected the grant (invalid_grant).");
                throw new McpOAuthTransientTokenException($"Token endpoint returned HTTP {(int)response.StatusCode}.");
            }

            JsonNode json;
            try { json = JsonNode.Parse(bytes) ?? throw new FormatException(); }
            catch (Exception) { throw new McpOAuthTransientTokenException("Token endpoint returned malformed JSON."); }

            // Grounding note (de-staled per T2/T3 opus gates — see McpOAuthDiscoveryService and
            // McpOAuthClientRegistrar): access_token/refresh_token are attacker-controlled (a
            // malicious or misbehaving AS), so a wrong-type value (e.g. {"access_token":123}) must
            // throw a classified exception, never an uncaught InvalidOperationException from
            // JsonNode.GetValue<string>(). RequireString/OptionalString mirror those two classes'
            // local helpers, but throw McpOAuthTransientTokenException — a malformed token response
            // is transient (retry-worthy), not the McpOAuthDiscoveryException used for discovery/DCR
            // shape errors.
            var accessToken = RequireString(json["access_token"], "Token response is missing access_token.");
            var refreshToken = OptionalString(json["refresh_token"]);
            var expiresIn = ParseExpiresIn(json["expires_in"]);
            return new McpOAuthTokenResult(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
    }

    private static string RequireString(JsonNode? node, string errorMessage) =>
        node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : throw new McpOAuthTransientTokenException(errorMessage);

    private static string? OptionalString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// MINOR #9 (fable plan-review): some authorization servers return expires_in as a JSON
    /// STRING ("expires_in":"3600") rather than a number — a bare `GetValue&lt;long&gt;()` THROWS on
    /// that shape (an unhandled exception, not one of this class's own classified exceptions),
    /// which would 500 the callback endpoint instead of degrading gracefully to the same default
    /// this method already uses when the field is absent. Tolerate both shapes; never throw here.
    /// </summary>
    private static long ParseExpiresIn(JsonNode? node)
    {
        const long defaultSeconds = 3600;
        if (node is not JsonValue value)
            return defaultSeconds;
        if (value.TryGetValue<long>(out var asLong))
            return asLong;
        if (value.TryGetValue<string>(out var asString) && long.TryParse(asString, out var parsed))
            return parsed;
        return defaultSeconds;
    }

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
                    throw new McpOAuthTransientTokenException("Token response exceeded the size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
