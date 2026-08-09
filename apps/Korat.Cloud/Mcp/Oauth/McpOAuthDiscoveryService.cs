using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Korat.Cloud.Mcp.Http;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Oauth;

/// <summary>
/// Increment 2 (HTTP MCP OAuth): a safe, upstream-detail-free failure signal for every step of
/// the oauth flow (discovery, DCR, token exchange except the classified McpOAuthInvalidGrant/
/// TransientToken subtypes defined in Task 4). Message is always safe to surface directly to the
/// owner (never a raw exception message / upstream body).
/// </summary>
public sealed class McpOAuthDiscoveryException(string message) : Exception(message);

/// <summary>RFC 8414 authorization server metadata, the fields this flow actually needs.</summary>
public sealed record McpOAuthServerMetadata(
    string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint);

/// <summary>
/// "Auto-detect auth mode" feature (Add-HTTP-MCP-server form, Remote URL onBlur): the 3-way
/// classification produced by <see cref="McpOAuthDiscoveryService.DetectAuthModeAsync"/> — a
/// lightweight, unauthenticated challenge probe that never runs full discovery/DCR. `Unknown`
/// covers everything that isn't a clean, positive signal (any status other than 200, a 401
/// without the RFC 9728 challenge, 5xx, timeout, network error) — the console leaves the Auth
/// dropdown on manual pick whenever this comes back.
/// </summary>
public enum McpAuthMode { None, OAuth, Unknown }

/// <summary>Wire-string mapping for <see cref="McpAuthMode"/> — the shape POST
/// /api/mcp-servers/detect-auth returns as its `authMode` field.</summary>
public static class McpAuthModeStrings
{
    public static string ToWireString(McpAuthMode mode) => mode switch
    {
        McpAuthMode.None => "none",
        McpAuthMode.OAuth => "oauth",
        _ => "unknown",
    };
}

/// <summary>
/// Increment 2: RFC 9728 protected-resource-metadata discovery (401 + WWW-Authenticate →
/// resource_metadata URL → fetch → its authorization_servers[0]) followed by RFC 8414
/// authorization-server metadata. Every fetched URL is SsrfGuard.ValidateUrl-checked at USE time
/// (discovery can name a host different from RemoteUrl — see the increment-2 plan's Global
/// Constraints) and every response is read with a bounded reader + a short per-call timeout
/// (these responses come from an attacker-controlled host).
/// </summary>
public sealed class McpOAuthDiscoveryService(IOutboundHttpClientFactory httpClientFactory, ILogger<McpOAuthDiscoveryService> logger)
{
    private const long MaxResponseBytes = 262_144; // 256 KB bounded read
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex ResourceMetadataParamRegex = new("resource_metadata=\"([^\"]+)\"", RegexOptions.Compiled);

    public async Task<McpOAuthServerMetadata> DiscoverAsync(string remoteUrl, CancellationToken ct)
    {
        var prmUrl = await ProbeForProtectedResourceMetadataUrlAsync(remoteUrl, ct);
        var prm = await FetchJsonAsync(prmUrl, ct);
        ValidateResourceMatches(prm, remoteUrl);

        var authServers = prm["authorization_servers"] as JsonArray
            ?? throw new McpOAuthDiscoveryException("Protected resource metadata is missing authorization_servers.");
        if (authServers.Count == 0)
            throw new McpOAuthDiscoveryException("Protected resource metadata lists no authorization servers.");
        var issuerCandidate = RequireString(authServers[0], "Protected resource metadata's authorization_servers[0] is not a string.");

        var asMetadataUrl = issuerCandidate.TrimEnd('/') + "/.well-known/oauth-authorization-server";
        var asMetadata = await FetchJsonAsync(asMetadataUrl, ct);

        var issuer = RequireString(asMetadata["issuer"], "Authorization server metadata is missing issuer.");
        var authorizationEndpoint = RequireString(asMetadata["authorization_endpoint"], "Authorization server metadata is missing authorization_endpoint.");
        var tokenEndpoint = RequireString(asMetadata["token_endpoint"], "Authorization server metadata is missing token_endpoint.");
        var registrationEndpoint = OptionalString(asMetadata["registration_endpoint"]);

        return new McpOAuthServerMetadata(issuer, authorizationEndpoint, tokenEndpoint, registrationEndpoint);
    }

    /// <summary>
    /// "Auto-detect auth mode" feature: an unauthenticated challenge probe classified WITHOUT
    /// running discovery/DCR (unlike <see cref="DiscoverAsync"/>, which requires the 401+PRM
    /// signal to succeed and throws on anything else). Best-effort by design — every failure mode
    /// (SSRF-blocked, network error, timeout, non-200/401 status, a 401 with no RFC 9728 challenge)
    /// classifies as <see cref="McpAuthMode.Unknown"/> rather than throwing, so the caller
    /// (POST /api/mcp-servers/detect-auth) never needs to translate an exception into a response.
    /// Reuses the same SSRF-guarded factory + bounded/timed read as
    /// <see cref="ProbeForProtectedResourceMetadataUrlAsync"/>; the response body is always
    /// discarded (never surfaced) — classification depends only on status code + WWW-Authenticate.
    /// </summary>
    public async Task<McpAuthMode> DetectAuthModeAsync(string remoteUrl, CancellationToken ct)
    {
        // Defense-in-depth: the sole call site (the detect-auth endpoint) already runs
        // SsrfGuard.ValidateUrl and returns 400 before reaching this method, but this makes the
        // method safe to call directly regardless of caller discipline.
        if (SsrfGuard.ValidateUrl(remoteUrl) is not null)
            return McpAuthMode.Unknown;

        using var http = httpClientFactory.CreateClient("mcp-auth-detect");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, remoteUrl);
        var initReq = HttpMcpMessage.Request(1, "initialize", new
        {
            protocolVersion = HttpMcpClient.McpProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "korat-cloud", version = "1" }
        });
        request.Content = new ByteArrayContent(initReq.ToUtf8Bytes());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return McpAuthMode.Unknown;
        }
        using (response)
        {
            // Drain (never surface) the body under the same bounded/timed read ProbeFor... uses —
            // a slowloris peer that answers headers instantly then trickles bytes must not hold
            // the connection open past CallTimeout. Content is discarded either way: classification
            // never reads it, and returning it would be an SSRF-exfil-via-reflection vector.
            try
            {
                await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxResponseBytes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return McpAuthMode.Unknown;
            }
            catch (McpOAuthDiscoveryException)
            {
                // Exceeded the size cap — irrelevant here; classification below only reads the
                // status code + WWW-Authenticate header, never the body.
            }

            if (response.StatusCode == HttpStatusCode.OK)
                return McpAuthMode.None;

            if (response.StatusCode == HttpStatusCode.Unauthorized
                && response.Headers.TryGetValues("WWW-Authenticate", out var values)
                && ResourceMetadataParamRegex.IsMatch(values.FirstOrDefault() ?? string.Empty))
            {
                return McpAuthMode.OAuth;
            }

            return McpAuthMode.Unknown;
        }
    }

    private async Task<string> ProbeForProtectedResourceMetadataUrlAsync(string remoteUrl, CancellationToken ct)
    {
        var ssrfError = SsrfGuard.ValidateUrl(remoteUrl);
        if (ssrfError is not null)
            throw new McpOAuthDiscoveryException($"Remote URL is not allowed: {ssrfError}");

        using var http = httpClientFactory.CreateClient("mcp-oauth-discovery");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, remoteUrl);
        var initReq = HttpMcpMessage.Request(1, "initialize", new
        {
            protocolVersion = HttpMcpClient.McpProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "korat-cloud", version = "1" }
        });
        request.Content = new ByteArrayContent(initReq.ToUtf8Bytes());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new McpOAuthDiscoveryException("Could not reach the remote MCP server to discover its authorization server.");
        }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                throw new McpOAuthDiscoveryException("The remote MCP server did not challenge with HTTP 401 — it may not require OAuth.");

            if (!response.Headers.TryGetValues("WWW-Authenticate", out var values))
                throw new McpOAuthDiscoveryException("The remote MCP server's 401 response has no WWW-Authenticate header.");

            var match = ResourceMetadataParamRegex.Match(values.FirstOrDefault() ?? string.Empty);
            if (!match.Success)
                throw new McpOAuthDiscoveryException("WWW-Authenticate header has no resource_metadata parameter.");
            return match.Groups[1].Value;
        }
    }

    private async Task<JsonNode> FetchJsonAsync(string url, CancellationToken ct)
    {
        var ssrfError = SsrfGuard.ValidateUrl(url);
        if (ssrfError is not null)
            throw new McpOAuthDiscoveryException($"Discovery URL is not allowed: {ssrfError}");

        using var http = httpClientFactory.CreateClient("mcp-oauth-discovery");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new McpOAuthDiscoveryException($"Could not reach {url}.");
        }
        using (response)
        {
            // Bound the body read by the same 15s cts (not the caller's bare ct) — otherwise a
            // slowloris peer that answers headers instantly then trickles bytes under the 256 KB
            // cap can hold the pinned connection open indefinitely (neither the 15s timeout nor
            // the factory's 600s HttpClient.Timeout covers a streamed body read under
            // ResponseHeadersRead).
            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxResponseBytes, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new McpOAuthDiscoveryException($"Timed out reading the response from {url}.");
            }
            if (!response.IsSuccessStatusCode)
                throw new McpOAuthDiscoveryException($"{url} returned HTTP {(int)response.StatusCode}.");
            try
            {
                return JsonNode.Parse(bytes) ?? throw new FormatException();
            }
            catch (Exception)
            {
                throw new McpOAuthDiscoveryException($"{url} returned malformed JSON.");
            }
        }
    }

    internal static void ValidateResourceMatches(JsonNode prm, string remoteUrl)
    {
        var resource = RequireString(prm["resource"], "Protected resource metadata is missing resource.");
        if (!CanonicalUrlEquals(resource, remoteUrl))
            throw new McpOAuthDiscoveryException("Protected resource metadata's resource does not match this server's URL.");
    }

    /// <summary>
    /// Type-safe string extraction from an attacker-controlled JSON node: missing key (null node),
    /// wrong JSON type (object/array), and JSON null all fall through to the same clean
    /// McpOAuthDiscoveryException instead of JsonNode.GetValue&lt;string&gt;()'s uncaught
    /// InvalidOperationException on a type mismatch.
    /// </summary>
    private static string RequireString(JsonNode? node, string errorMessage) =>
        node is JsonValue value && value.TryGetValue<string>(out var s)
            ? s
            : throw new McpOAuthDiscoveryException(errorMessage);

    /// <summary>Same type-safety as <see cref="RequireString"/> but for an optional field: a
    /// missing key or a wrong-type value is treated as absent rather than as an error.</summary>
    private static string? OptionalString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Canonical comparison: scheme/host lowercased, default port + trailing slash normalized.
    /// The driving target, Miro, is literally "https://mcp.miro.com/" — an exact-string compare
    /// against a PRM resource of "https://mcp.miro.com" (no trailing slash) would false-negative.
    /// </summary>
    internal static bool CanonicalUrlEquals(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub))
            return false;
        return string.Equals(Canonicalize(ua), Canonicalize(ub), StringComparison.Ordinal);
    }

    private static string Canonicalize(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var defaultPort = scheme == "https" ? 443 : 80;
        var port = uri.Port == -1 ? defaultPort : uri.Port;
        var path = uri.AbsolutePath.TrimEnd('/');
        return port == defaultPort ? $"{scheme}://{host}{path}" : $"{scheme}://{host}:{port}{path}";
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
                    throw new McpOAuthDiscoveryException("Response exceeded the discovery size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
