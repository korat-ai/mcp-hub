namespace Korat.Domain;

/// <summary>
/// Well-known Kind discriminator strings for InferencePoint.
/// Data-change only — no enum migration needed (stored as varchar 32).
/// </summary>
public static class InferencePointKinds
{
    public const string HeadlessAgent = "headless_agent";
    public const string Byok          = "byok";
    public const string ByoEndpoint   = "byo_endpoint";
    public const string HostedAgent   = "hosted_agent";

    /// <summary>
    /// True when <paramref name="kind"/> is a known InferencePoint kind discriminator.
    /// NOT an authorization predicate: hosted_agent is a valid stored kind but cannot be
    /// created via POST /api/inference-points (that surface accepts IsOutbound kinds only)
    /// and its lifecycle is managed exclusively through /api/agents.
    /// </summary>
    public static bool IsValid(string kind) =>
        kind is HeadlessAgent or Byok or ByoEndpoint or HostedAgent;

    public static bool IsOutbound(string kind) =>
        kind is Byok or ByoEndpoint;

    // NOTE: IsOutbound must remain (kind is Byok or ByoEndpoint) — hosted_agent is NOT outbound.
    public static bool IsHostedAgent(string kind) => kind == HostedAgent;
}

/// <summary>
/// Well-known provider identifiers for the byok inference kind.
/// All are OpenAI-compatible (passthrough) except "anthropic" which uses
/// Anthropic's OpenAI-compatibility surface (api.anthropic.com/v1/chat/completions, Bearer).
/// "generic" requires an explicit base_url override.
/// </summary>
public static class InferenceProviders
{
    public const string OpenAi      = "openai";
    public const string OpenRouter  = "openrouter";
    public const string Qwen        = "qwen";
    public const string Anthropic   = "anthropic";
    public const string Generic     = "generic";

    /// <summary>Canonical base URLs for known providers (lowercase provider id → base url, no trailing slash).</summary>
    public static readonly IReadOnlyDictionary<string, string> CanonicalBaseUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OpenAi]     = "https://api.openai.com",
            [OpenRouter] = "https://openrouter.ai/api",
            [Qwen]       = "https://dashscope.aliyuncs.com/compatible-mode",
            [Anthropic]  = "https://api.anthropic.com",
        };

    public static bool IsValid(string provider) =>
        provider is OpenAi or OpenRouter or Qwen or Anthropic or Generic;

    /// <summary>Returns the effective base URL for the provider: explicit override wins,
    /// then canonical, then null (generic without override = validation error).</summary>
    public static string? ResolveBaseUrl(string provider, string? baseUrlOverride)
    {
        if (!string.IsNullOrWhiteSpace(baseUrlOverride))
            return baseUrlOverride.TrimEnd('/');
        return CanonicalBaseUrls.TryGetValue(provider, out var url) ? url : null;
    }
}

/// <summary>
/// Validation rules for outbound inference point configuration (T3/T4).
/// Returns an error string on failure, null on success.
/// </summary>
public static class OutboundInferenceValidation
{
    // RFC 7230 token characters (excluding whitespace and separators)
    private static readonly System.Text.RegularExpressions.Regex HeaderNameRx =
        new(@"^[!#$%&'*+\-.0-9A-Z^_`a-z|~]+$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Forbidden headers that would corrupt the proxy or SSRF-amplify
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Type", "Content-Length", "Transfer-Encoding",
        "Connection", "Keep-Alive", "Upgrade", "Proxy-Authenticate",
        "Proxy-Authorization", "TE", "Trailer"
    };

    public static string? ValidateByok(string provider, string? baseUrlOverride)
    {
        if (!InferenceProviders.IsValid(provider))
            return $"Unknown provider '{provider}'. Valid: openai, openrouter, qwen, anthropic, generic.";
        if (provider == InferenceProviders.Generic && string.IsNullOrWhiteSpace(baseUrlOverride))
            return "provider=generic requires an explicit base_url.";
        return null;
    }

    public static string? ValidateByoEndpoint(string baseUrl, string? authHeaderName)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "base_url is required for byo_endpoint.";
        return ValidateHeaderName(authHeaderName);
    }

    /// <summary>
    /// Single source of truth for validating an owner-supplied outbound auth header name —
    /// shared by every http surface that lets an owner name a header injected into a proxied
    /// request (byo_endpoint inference points here; http_cloud MCP servers via
    /// McpServerEndpoints in apps/Korat.Cloud). The header name is SSRF-untrusted input: an
    /// unvalidated name reaches `HttpRequestMessage.Headers.TryAddWithoutValidation`, which
    /// bypasses .NET's built-in header-name validation, so a value like "Host" can override the
    /// Host header on an SSRF-pinned connection, and "Transfer-Encoding"/"Content-Length" can
    /// enable request smuggling. Do NOT duplicate <see cref="HeaderNameRx"/> or
    /// <see cref="ForbiddenHeaders"/> elsewhere — always route through this method.
    /// Returns null when <paramref name="authHeaderName"/> is omitted (null) or valid; otherwise
    /// a caller-facing error string.
    /// </summary>
    public static string? ValidateHeaderName(string? authHeaderName)
    {
        if (authHeaderName is null)
            return null;
        if (!HeaderNameRx.IsMatch(authHeaderName))
            return $"auth_header_name '{authHeaderName}' is not a valid RFC 7230 token.";
        if (ForbiddenHeaders.Contains(authHeaderName))
            return $"auth_header_name '{authHeaderName}' is forbidden.";
        return null;
    }
}
