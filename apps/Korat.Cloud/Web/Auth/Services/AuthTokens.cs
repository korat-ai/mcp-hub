using System.Security.Cryptography;
using System.Text;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Shared helpers for hashed single-use token generation used by
/// <see cref="SessionService"/>, <see cref="CliTokenService"/>, and
/// <see cref="EmailChangeService"/>. Centralising here ensures the hashing
/// algorithm is provably identical across all token types and cannot drift.
/// </summary>
internal static class AuthTokens
{
    /// <summary>
    /// Generates a 32-byte CSPRNG token encoded as base64url (no padding).
    /// Only the SHA-256 hex of this value should be persisted; the raw value
    /// is sent once to the user and never stored.
    /// </summary>
    public static string GenerateRawBase64Url()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Returns the uppercase hex-encoded SHA-256 digest of the UTF-8 encoded <paramref name="raw"/> string.
    /// This is the value stored in the database as <c>TokenHash</c>.
    /// </summary>
    public static string Sha256Hex(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
