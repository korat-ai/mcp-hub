using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Korat.Cloud.Web.Auth.Security;

/// <param name="ReturnUrl">Safe return URL after OAuth sign-in completes.</param>
/// <param name="InviteCode">Optional invite code carried through the OAuth flow.</param>
/// <param name="Nonce">
/// Random value included for future replay-store wiring. Currently retained but
/// not validated against a replay store — the <see cref="OAuthStateProtector.StateMaxAge"/>
/// TTL is the sole freshness guarantee in this cycle.
/// </param>
/// <param name="IssuedAt">
/// UTC timestamp when the state was protected. <see cref="OAuthStateProtector.TryUnprotect"/>
/// rejects values older than <see cref="OAuthStateProtector.StateMaxAge"/> (10 minutes).
/// </param>
/// <param name="LinkUserId">
/// When set, this signin is a "connect provider" flow (027): the OAuth-proven identity is
/// linked to this already-authenticated user instead of signing in / creating an account.
/// Tamper-proof (the payload is signed); re-checked against the live session at /finish.
/// </param>
public sealed record OAuthStatePayload(
    string ReturnUrl,
    Guid Nonce,
    DateTimeOffset IssuedAt,
    Guid? LinkUserId = null);

public interface IOAuthStateProtector
{
    string Protect(OAuthStatePayload payload);
    OAuthStatePayload? TryUnprotect(string protectedValue);
}

public sealed class OAuthStateProtector : IOAuthStateProtector
{
    /// <summary>
    /// Maximum age of a valid OAuth state token. OAuth redirects complete within seconds
    /// under normal conditions; 10 minutes is generous headroom while bounding replay
    /// of captured state values.
    /// </summary>
    public static readonly TimeSpan StateMaxAge = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public OAuthStateProtector(IDataProtectionProvider provider, TimeProvider time)
    {
        _protector = provider.CreateProtector("Korat.Auth.OAuthState.v1");
        _time = time;
    }

    public string Protect(OAuthStatePayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encrypted = _protector.Protect(json);
        return Convert.ToBase64String(encrypted).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public OAuthStatePayload? TryUnprotect(string protectedValue)
    {
        // DoS guard: TryUnprotect is reachable on an unauthenticated OAuth callback.
        // A realistic protected payload (returnUrl <= 2048 + InviteCode + Guid + IssuedAt)
        // round-trips to ~700-900 base64url chars; 4096 is generous headroom and bounds
        // attacker amplification (cheap inbound string → expensive base64 decode + AES unwrap).
        if (string.IsNullOrEmpty(protectedValue) || protectedValue.Length > 4096) return null;

        try
        {
            var padded = protectedValue.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            var json = _protector.Unprotect(bytes);
            var payload = JsonSerializer.Deserialize<OAuthStatePayload>(json);
            if (payload is null) return null;

            // Freshness check: reject state older than StateMaxAge (10 minutes).
            // This provides a TTL guarantee without a server-side replay store.
            // The Nonce field is retained as a future hook for replay-store wiring
            // but is NOT currently validated against one.
            if (_time.GetUtcNow() - payload.IssuedAt > StateMaxAge) return null;

            return payload;
        }
        catch
        {
            // TryUnprotect contract: never throw on tamper, garbage, version mismatch, or
            // foreign-key ciphertext. A non-null return proves authenticity AND freshness
            // (DataProtection HMAC + IssuedAt TTL check above).
            return null;
        }
    }
}
