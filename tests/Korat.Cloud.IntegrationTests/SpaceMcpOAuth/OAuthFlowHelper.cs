using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a test infra: drives the MCP Authorization 2025-06-18 client role against
/// the fixture host — PKCE pair, authorize URL, consent-page GET→POST (antiforgery cookie +
/// hidden field extracted from the real page), code capture from the redirect, and the two
/// token-endpoint grants. Every OAuth test goes through here so the client shape can't drift.
/// </summary>
internal static class OAuthFlowHelper
{
    public const string ClientId = "korat-mcp";
    public const string RedirectUri = "http://127.0.0.1:45123/callback";

    public static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string AuthorizeUrl(string resource, string challenge, string scope = "korat:mcp") =>
        "/connect/authorize?response_type=code" +
        $"&client_id={ClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&resource={Uri.EscapeDataString(resource)}" +
        $"&code_challenge={challenge}&code_challenge_method=S256" +
        $"&state=st-{Guid.NewGuid():N}";

    /// <summary>Inc-2b, Task 7 overload: same as <see cref="AuthorizeUrl(string,string,string)"/>
    /// but for a DCR-registered client, which carries its OWN client_id/redirect_uri rather than
    /// the pre-registered <see cref="ClientId"/>/<see cref="RedirectUri"/> constants.</summary>
    public static string AuthorizeUrl(
        string resource, string challenge, string clientId, string redirectUri, string scope = "korat:mcp") =>
        "/connect/authorize?response_type=code" +
        $"&client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&resource={Uri.EscapeDataString(resource)}" +
        $"&code_challenge={challenge}&code_challenge_method=S256" +
        $"&state=st-{Guid.NewGuid():N}";

    /// <summary>GETs the consent page (cookie-authenticated, no-redirect client), extracts the
    /// antiforgery cookie + hidden field + all hidden inputs, POSTs "allow", and returns the
    /// authorization code from the 302 Location back to RedirectUri.</summary>
    public static async Task<string> AuthorizeAndConsentAsync(HttpClient authedNoRedirectClient, string authorizeUrl)
    {
        var page = await authedNoRedirectClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        // Antiforgery COOKIE token from Set-Cookie (name pinned in Program.cs:575).
        string? xsrfCookie = null;
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                if (sc.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal))
                {
                    var end = sc.IndexOf(';');
                    xsrfCookie = end >= 0 ? sc[..end] : sc;
                    break;
                }
            }
        }
        Assert.NotNull(xsrfCookie);

        // Every hidden input (original OAuth params + __RequestVerificationToken) → form body.
        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\" />")
            .Select(m => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();
        fields.Add(new("submit", "allow"));

        var post = new HttpRequestMessage(HttpMethod.Post, "/connect/authorize")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        // NOTE: HttpClient does NOT merge a header set on DefaultRequestHeaders (the session
        // cookie, set by CreateAuthenticatedNoRedirectClientAsync) with the same header set
        // per-request (the antiforgery cookie) — only the per-request value reaches the wire,
        // silently dropping the session cookie and bouncing the POST to signin. Combine both
        // into ONE explicit Cookie header value.
        var sessionCookie = authedNoRedirectClient.DefaultRequestHeaders.TryGetValues("Cookie", out var sessionCookies)
            ? sessionCookies.First() : null;
        post.Headers.Add("Cookie", sessionCookie is null ? xsrfCookie : $"{sessionCookie}; {xsrfCookie}");
        var consentResponse = await authedNoRedirectClient.SendAsync(post);

        Assert.True(
            consentResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"expected code redirect, got {(int)consentResponse.StatusCode}: {await consentResponse.Content.ReadAsStringAsync()}");
        var location = consentResponse.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);
        var code = Regex.Match(location, "[?&]code=([^&]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(code), $"no code in redirect: {location}");
        return Uri.UnescapeDataString(code);
    }

    /// <summary>Inc-2b, Task 7 overload of <see cref="AuthorizeAndConsentAsync(HttpClient,string)"/>
    /// that expects the code to redirect to a caller-supplied redirect URI (a DCR client's own
    /// callback) rather than the pre-registered <see cref="RedirectUri"/> constant.</summary>
    public static async Task<string> AuthorizeAndConsentAsync(
        HttpClient authedNoRedirectClient, string authorizeUrl, string redirectUri)
    {
        var page = await authedNoRedirectClient.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        string? xsrfCookie = null;
        if (page.Headers.TryGetValues("Set-Cookie", out var setCookies))
            foreach (var sc in setCookies)
                if (sc.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal))
                {
                    var end = sc.IndexOf(';');
                    xsrfCookie = end >= 0 ? sc[..end] : sc;
                    break;
                }
        Assert.NotNull(xsrfCookie);

        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\" />")
            .Select(m => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();
        fields.Add(new("submit", "allow"));

        var post = new HttpRequestMessage(HttpMethod.Post, "/connect/authorize")
        { Content = new FormUrlEncodedContent(fields) };
        var sessionCookie = authedNoRedirectClient.DefaultRequestHeaders.TryGetValues("Cookie", out var sessionCookies)
            ? sessionCookies.First() : null;
        post.Headers.Add("Cookie", sessionCookie is null ? xsrfCookie : $"{sessionCookie}; {xsrfCookie}");
        var consentResponse = await authedNoRedirectClient.SendAsync(post);

        Assert.True(consentResponse.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"expected code redirect, got {(int)consentResponse.StatusCode}: {await consentResponse.Content.ReadAsStringAsync()}");
        var location = consentResponse.Headers.Location!.ToString();
        Assert.StartsWith(redirectUri, location);
        var code = Regex.Match(location, "[?&]code=([^&]+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(code), $"no code in redirect: {location}");
        return Uri.UnescapeDataString(code);
    }

    public static async Task<JsonNode> ExchangeCodeAsync(
        HttpClient client, string code, string verifier, string resource)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new("code", code),
            new("code_verifier", verifier),
            new("redirect_uri", RedirectUri),
            new("client_id", ClientId),
            new("resource", resource),
        ]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"token exchange failed {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>Inc-2b, Task 7 overload of <see cref="ExchangeCodeAsync(HttpClient,string,string,string)"/>
    /// parameterized by a DCR client's own client_id/redirect_uri.</summary>
    public static async Task<JsonNode> ExchangeCodeAsync(
        HttpClient client, string code, string verifier, string resource, string clientId, string redirectUri)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new("code", code),
            new("code_verifier", verifier),
            new("redirect_uri", redirectUri),
            new("client_id", clientId),
            new("resource", resource),
        ]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"token exchange failed {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    public static async Task<(HttpStatusCode Status, JsonNode Body)> RefreshAsync(
        HttpClient client, string refreshToken)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", ClientId),
        ]));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonNode.Parse(body)!);
    }
}
