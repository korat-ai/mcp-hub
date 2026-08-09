using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Push;

/// <summary>
/// Shared APNs HTTP/2 transport: ES256 JWT signing (45-min cache) + named HttpClient("apns") +
/// host selection (prod vs sandbox) + the 403-provider-token-refresh-once retry. Extracted from
/// <see cref="ApnsPushWakeSender"/> (030 push-to-wake) so the new alert sender (031, mobile-push
/// increment 2) can reuse the SAME JWT cache — sharing one cache is strictly better than two
/// independent ones (Apple throttles aggressive JWT rotation).
///
/// Deliberately dumb about payload shape: it sends whatever headers/body the caller supplies
/// (plus apns-topic + Authorization, which it always owns) to
/// <c>https://{host}/3/device/{token}</c> and returns the raw (status, body) — each caller
/// (<see cref="ApnsPushWakeSender"/>, <see cref="ApnsAlertSender"/>) maps that to its OWN result
/// enum. This mirrors the design constraint: "the transport returns raw status+body; each sender
/// maps its own result."
///
/// JWT cache: cached for 45 minutes (Apple accepts 20–60 min) to avoid per-push JWT generation.
/// One <see cref="ECDsa"/> instance is created from the PEM private key at construction time and
/// reused under a lock.
///
/// HttpClient lifetime: <see cref="IHttpClientFactory"/> is injected and
/// <see cref="IHttpClientFactory.CreateClient(string)"/> is called per-send so that the underlying
/// <see cref="System.Net.Http.SocketsHttpHandler"/> respects its <c>PooledConnectionLifetime</c>
/// (configured via named client "apns" in Program.cs) and DNS changes are picked up on schedule.
/// </summary>
public sealed class ApnsTransport : IDisposable
{
    /// Named HttpClient key registered in Program.cs for BOTH APNs senders.
    public const string HttpClientName = "apns";

    private const string ProductionHost = "api.push.apple.com";
    private const string SandboxHost = "api.sandbox.push.apple.com";

    // APNs JWT cache window: 45 min (Apple allows 20–60 min; stay well inside).
    private static readonly TimeSpan JwtCacheWindow = TimeSpan.FromMinutes(45);

    private readonly ApnsOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ApnsTransport> _log;

    // JWT cache — protected by _jwtLock.
    private readonly object _jwtLock = new();
    private string? _cachedJwt;
    private DateTimeOffset _jwtIssuedAt = DateTimeOffset.MinValue;

    // Single ECDsa instance constructed once from the PEM private key.
    private readonly ECDsa _ecdsa;

    public ApnsTransport(
        IOptions<ApnsOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<ApnsTransport> log)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _log = log;

        // Import the ES256 private key from the .p8 PEM contents.
        // The .p8 file from Apple is a PKCS#8 encoded EC key.
        var pem = _options.PrivateKeyPem
            ?? throw new InvalidOperationException("Korat:Apns:PrivateKeyPem is required for ApnsTransport.");

        _ecdsa = ECDsa.Create();
        try
        {
            var pemBody = pem
                .Replace("-----BEGIN PRIVATE KEY-----", "")
                .Replace("-----END PRIVATE KEY-----", "")
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();
            var keyBytes = Convert.FromBase64String(pemBody);
            _ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (Exception ex)
        {
            _ecdsa.Dispose();
            throw new InvalidOperationException(
                "Failed to import APNs private key from Korat:Apns:PrivateKeyPem. " +
                "Ensure the value is a valid PKCS#8 PEM (full .p8 file contents).", ex);
        }
    }

    /// <summary>
    /// Sends one APNs HTTP/2 request. Handles JWT injection, host selection (prod/sandbox), and
    /// the 403-ExpiredProviderToken/InvalidProviderToken-refresh-once retry INTERNALLY — the
    /// returned (Status, Body) is always the FINAL response the caller should map. apns-topic and
    /// Authorization are always set by the transport; <paramref name="headers"/> supplies
    /// everything that differs per push type (apns-push-type, apns-priority, apns-expiration,
    /// apns-collapse-id, ...).
    /// </summary>
    public Task<(int Status, string? Body)> SendAsync(
        string deviceToken,
        string platform,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        CancellationToken ct)
        => SendOnceAsync(deviceToken, platform, headers, body, ct, forceNewJwt: false);

    private async Task<(int Status, string? Body)> SendOnceAsync(
        string deviceToken,
        string platform,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        CancellationToken ct,
        bool forceNewJwt)
    {
        var host = platform == "apns_sandbox" ? SandboxHost : ProductionHost;
        var url = $"https://{host}/3/device/{deviceToken}";
        var tokenPrefix = deviceToken.Length >= 8 ? deviceToken[..8] : deviceToken;

        // FIX (carried over from ApnsPushWakeSender): on forceNewJwt (403 retry path) invalidate
        // the cached JWT so GetOrRefreshJwt() generates a fresh one.
        if (forceNewJwt)
        {
            lock (_jwtLock) { _jwtIssuedAt = DateTimeOffset.MinValue; }
        }

        var jwt = GetOrRefreshJwt();

        // FIX (stale-DNS, carried over): create a new HttpClient wrapper per-send via the factory.
        var http = _httpFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Version = System.Net.HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
        request.Headers.TryAddWithoutValidation("apns-topic", _options.BundleId);
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.SendAsync(request, ct);
        var status = (int)response.StatusCode;

        if (status == 200)
        {
            _log.LogDebug("APNs push sent to token {TokenPrefix}... ({Platform})", tokenPrefix, platform);
            return (200, null);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        // 403 with ExpiredProviderToken/InvalidProviderToken → force-refresh JWT + retry once.
        if (status == 403 && !forceNewJwt &&
            (responseBody.Contains("ExpiredProviderToken", StringComparison.OrdinalIgnoreCase) ||
             responseBody.Contains("InvalidProviderToken", StringComparison.OrdinalIgnoreCase)))
        {
            _log.LogWarning(
                "APNs 403 {Reason} for token {TokenPrefix}... — clearing JWT cache and retrying once.",
                responseBody, tokenPrefix);
            return await SendOnceAsync(deviceToken, platform, headers, body, ct, forceNewJwt: true);
        }

        return (status, responseBody);
    }

    /// <summary>
    /// Returns a cached JWT or generates a new one when the cache is expired. ES256-signed token
    /// with claims {iss: TeamId, iat: now} and header {alg: ES256, kid: KeyId}. Cached for 45 min.
    /// </summary>
    private string GetOrRefreshJwt()
    {
        lock (_jwtLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cachedJwt is not null && now - _jwtIssuedAt < JwtCacheWindow)
                return _cachedJwt;

            _cachedJwt = BuildJwt(now);
            _jwtIssuedAt = now;
            return _cachedJwt;
        }
    }

    private string BuildJwt(DateTimeOffset issuedAt)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "ES256",
            kid = _options.KeyId
        }));

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _options.TeamId,
            iat = issuedAt.ToUnixTimeSeconds()
        }));

        var signingInput = $"{header}.{payload}";
        var signingInputBytes = Encoding.ASCII.GetBytes(signingInput);
        var signature = _ecdsa.SignData(signingInputBytes, HashAlgorithmName.SHA256);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public void Dispose() => _ecdsa.Dispose();
}
