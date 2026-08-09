using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// A person, as asserted by the Korat SSO provider at id.korat.*.
/// </summary>
/// <param name="Subject">The SSO account id. Stable across email changes and across sign-in
/// methods — the whole reason sign-in moved out of this app.</param>
/// <param name="Email">Present only when the token carries the <c>email</c> scope.</param>
/// <param name="DeviceId">
/// The SSO authorization this token belongs to (<c>oi_au_id</c>). Two CLI installs of the same
/// person get different values, so this — not the subject — is what a durable per-device
/// consumer identity is derived from. Absent for browser sign-ins that do not go through the
/// device flow.
/// </param>
public sealed record SsoPrincipal(string Subject, string? Email, string? DeviceId, IReadOnlyList<string> Scopes);

public interface ISsoTokenValidator
{
    /// <summary>Whether an SSO issuer is configured at all. False means this app is not yet an SSO client.</summary>
    bool Enabled { get; }

    /// <summary>Returns null for anything that is not a valid, unexpired token from the configured issuer.</summary>
    Task<SsoPrincipal?> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>
/// Validates access tokens issued by Korat SSO.
///
/// Deliberately independent of the OpenIddict validation registered elsewhere in this app.
/// That one is <c>UseLocalServer()</c> — it validates tokens THIS app issued, against its own
/// keys and token store, and it stays exactly as it is: the MCP OAuth surface is still ours.
/// SSO tokens are a different audience with a different issuer and different keys, and mixing
/// the two configurations would make each harder to reason about than both are apart.
///
/// Signature keys come from the issuer's JWKS and are refreshed on their own schedule, so key
/// rotation at the provider needs no deploy here. Nothing but the issuer URL is configured:
/// the point of moving sign-in out was that services know only an address and a public key.
/// </summary>
public sealed class SsoTokenValidator : ISsoTokenValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private readonly string? _issuer;
    private readonly HashSet<string> _allowedClients = new(StringComparer.Ordinal);
    private readonly ILogger<SsoTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public SsoTokenValidator(IConfiguration configuration, ILogger<SsoTokenValidator> logger)
    {
        _logger = logger;

        var issuer = configuration["Sso:Issuer"];
        if (string.IsNullOrWhiteSpace(issuer))
        {
            // Not configured is a valid state: until this app is switched over, its own
            // credentials remain the only way in. Silence here, not a startup failure —
            // a missing optional integration must not stop the app from serving.
            return;
        }

        // Which client the token was issued to is checked, and it is not optional.
        //
        // The provider can register third-party clients. Without this list, a token minted
        // for ANY of them would resolve to a person here and walk through every permission
        // gate this app has — the provider would be deciding who gets into this app, which
        // is not what "verify the signature" was ever supposed to mean.
        //
        // Audience is deliberately not used for this: access to a Space is decided per-Space
        // by this app, and a static audience list would be a second, weaker copy of that.
        // The client is a different question — "whose software is holding this token".
        foreach (var client in configuration.GetSection("Sso:AllowedClients").Get<string[]>() ?? [])
            if (!string.IsNullOrWhiteSpace(client))
                _allowedClients.Add(client.Trim());

        if (_allowedClients.Count == 0)
        {
            // Refusing to start beats starting permissive. Half a configuration here is
            // worse than none: with no issuer the validator is inert and this app's own
            // credentials still work, but with an issuer and no client list it accepts
            // strangers — and nothing about that is visible until someone walks in.
            throw new InvalidOperationException(
                "Sso:Issuer is set but Sso:AllowedClients is empty. List the client ids whose " +
                "tokens this app accepts, or remove Sso:Issuer to leave SSO turned off.");
        }

        // Trailing slash matters: the `iss` claim has to match character for character, and
        // the provider emits it with the slash. Normalising here means a value written either
        // way in configuration works, instead of failing at the first token validation.
        _issuer = issuer.EndsWith('/') ? issuer : issuer + "/";

        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{_issuer}.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = _issuer.StartsWith("https://", StringComparison.Ordinal) });
    }

    public bool Enabled => _configuration is not null;

    /// <summary>
    /// The same rules on the first attempt and on the retry after a key refresh. Spelling them
    /// twice is how a retry quietly ends up more permissive than the original.
    /// </summary>
    private TokenValidationParameters Parameters(OpenIdConnectConfiguration config) => new()
    {
        ValidIssuer = _issuer,
        IssuerSigningKeys = config.SigningKeys,
        // No audience check here on purpose. Access to a Space is decided per-Space by this
        // app, against its own grants; a static audience list would be a second, weaker copy
        // of that decision, and the two would drift.
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    public async Task<SsoPrincipal?> ValidateAsync(string token, CancellationToken ct)
    {
        if (_configuration is null || string.IsNullOrWhiteSpace(token))
            return null;

        // A JWT has exactly two dots. Checking that first keeps this off the hot path for
        // our own opaque credentials, which would otherwise cost a full parse attempt each.
        if (token.AsSpan().Count('.') != 2)
            return null;

        OpenIdConnectConfiguration config;
        try
        {
            config = await _configuration.GetConfigurationAsync(ct);
        }
        catch (Exception exception)
        {
            // The provider being unreachable must not read as "this token is invalid" —
            // that would silently downgrade an outage into an authorization failure.
            _logger.LogWarning(exception, "SSO discovery unavailable at {Issuer}", _issuer);
            return null;
        }

        var result = await _handler.ValidateTokenAsync(token, Parameters(config));

        if (!result.IsValid)
        {
            // An unknown signing key means the provider rotated it, and waiting for the
            // cache to expire on its own would reject every fresh token until it does —
            // the provider publishes one key at a time, so there is no overlap to ride out.
            // Ask for a refresh and try once more; this is what JwtBearerHandler does, and
            // omitting it turned "rotation needs no deploy here" into a half-truth.
            if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                _configuration.RequestRefresh();
                config = await _configuration.GetConfigurationAsync(ct);

                result = await _handler.ValidateTokenAsync(token, Parameters(config));
                if (!result.IsValid)
                {
                    _logger.LogWarning(result.Exception, "SSO token rejected after key refresh");
                    return null;
                }
            }
            else
            {
                _logger.LogDebug(result.Exception, "SSO token rejected");
                return null;
            }
        }

        var claims = result.ClaimsIdentity;

        // `client_id` is the standard claim; `oi_prst` is the provider's own "presenters"
        // list and carries the same answer. Both were observed in a live token, so either
        // arriving is enough — but one of them has to.
        var client = claims.FindFirst("client_id")?.Value ?? claims.FindFirst("oi_prst")?.Value;
        if (client is null || !_allowedClients.Contains(client))
        {
            _logger.LogWarning("SSO token rejected: client {Client} is not on the allowed list", client ?? "(absent)");
            return null;
        }

        var subject = claims.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var scopes = claims.FindFirst("scope")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        return new SsoPrincipal(
            Subject: subject,
            Email: claims.FindFirst("email")?.Value,
            DeviceId: claims.FindFirst("oi_au_id")?.Value,
            Scopes: scopes);
    }
}
