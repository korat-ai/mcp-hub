using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Korat.Cloud.Security.Envelope;

/// <summary>
/// Pure, stateless AES-256-GCM cipher used by the envelope encryption layer.
/// No I/O, no DI dependencies — designed for easy exhaustive unit testing.
///
/// Envelope format stored in the EncryptedSecret text column:
///   kenv1.{kek_id}.{dek_version}.{base64url(nonce 12B)}.{base64url(ciphertext||tag 16B)}
///
/// The "kenv1." prefix is the format discriminator vs legacy DataProtection ciphertext
/// (which is dot-free base64url). Any future version uses a different prefix.
/// </summary>
public static class EnvelopeCipher
{
    // AES-256-GCM constants
    public const int NonceSize = 12;   // bytes — AES-GCM standard
    public const int TagSize   = 16;   // bytes — full GCM tag
    public const int KeySize   = 32;   // bytes — AES-256

    private const string EnvelopeVersion = "kenv1";
    private const string DekAadPrefix    = "korat.dek.v1\x1F";
    private const string SecretAadPrefix = "korat.secret.v1\x1F";
    private const char   Sep             = '\x1F';  // unit-separator for AAD fields

    // ── DEK wrap / unwrap ─────────────────────────────────────────────────────

    /// <summary>
    /// Wrap a 256-bit DEK under the given KEK using AES-256-GCM.
    /// AAD binds the wrapped blob to (spaceId, kekId, dekVersion).
    /// Returns (nonce, wrappedDek) where wrappedDek = ciphertext || tag.
    /// </summary>
    public static (byte[] Nonce, byte[] WrappedDek) WrapDek(
        byte[] kek, byte[] dek, string spaceId, string kekId, int dekVersion)
    {
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(dek);
        if (kek.Length != KeySize) throw new ArgumentException("KEK must be 32 bytes.", nameof(kek));
        if (dek.Length != KeySize) throw new ArgumentException("DEK must be 32 bytes.", nameof(dek));

        var nonce      = GenerateNonce();
        var aad        = BuildDekAad(spaceId, kekId, dekVersion);
        var ciphertext = new byte[dek.Length];
        var tag        = new byte[TagSize];

        using var aesGcm = new AesGcm(kek, TagSize);
        aesGcm.Encrypt(nonce, dek, ciphertext, tag, aad);

        // Store as ciphertext || tag
        var wrapped = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(wrapped, 0);
        tag.CopyTo(wrapped, ciphertext.Length);

        return (nonce, wrapped);
    }

    /// <summary>
    /// Unwrap a DEK from its wrapped form.
    /// Throws <see cref="CryptographicException"/> if the KEK, nonce, wrapped blob, or AAD is invalid.
    /// </summary>
    public static byte[] UnwrapDek(
        byte[] kek, byte[] nonce, byte[] wrappedDek, string spaceId, string kekId, int dekVersion)
    {
        ArgumentNullException.ThrowIfNull(kek);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(wrappedDek);
        if (kek.Length != KeySize)   throw new ArgumentException("KEK must be 32 bytes.", nameof(kek));
        if (nonce.Length != NonceSize) throw new ArgumentException("Nonce must be 12 bytes.", nameof(nonce));
        if (wrappedDek.Length != KeySize + TagSize)
            throw new ArgumentException($"WrappedDek must be {KeySize + TagSize} bytes.", nameof(wrappedDek));

        var ciphertext = wrappedDek[..KeySize];
        var tag        = wrappedDek[KeySize..];
        var aad        = BuildDekAad(spaceId, kekId, dekVersion);
        var plainDek   = new byte[KeySize];

        using var aesGcm = new AesGcm(kek, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plainDek, aad);  // throws on auth failure

        return plainDek;
    }

    // ── Secret encrypt / decrypt ──────────────────────────────────────────────

    /// <summary>
    /// Encrypt a plaintext secret under the given DEK, binding the ciphertext to
    /// (spaceId, pointId, fieldTag) via AAD so it cannot be spliced to another record.
    ///
    /// Returns the envelope string to store in EncryptedSecret.
    /// </summary>
    public static string EncryptSecret(
        byte[] dek, string plaintext, string spaceId, string pointId,
        string kekId, int dekVersion, string fieldTag = "provider_secret")
    {
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (dek.Length != KeySize) throw new ArgumentException("DEK must be 32 bytes.", nameof(dek));

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce          = GenerateNonce();
        var aad            = BuildSecretAad(spaceId, pointId, fieldTag);
        var ciphertext     = new byte[plaintextBytes.Length];
        var tag            = new byte[TagSize];

        using var aesGcm = new AesGcm(dek, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

        // ctTag = ciphertext || tag
        var ctTag = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(ctTag, 0);
        tag.CopyTo(ctTag, ciphertext.Length);

        return BuildEnvelopeString(kekId, dekVersion, nonce, ctTag);
    }

    /// <summary>
    /// Decrypt an envelope string.
    /// Returns the plaintext, or throws <see cref="CryptographicException"/> on auth failure.
    /// Throws <see cref="FormatException"/> if the string is not a valid envelope.
    /// </summary>
    public static string DecryptSecret(
        byte[] dek, string envelope, string spaceId, string pointId,
        string fieldTag = "provider_secret")
    {
        ArgumentNullException.ThrowIfNull(dek);
        ArgumentNullException.ThrowIfNull(envelope);
        if (dek.Length != KeySize) throw new ArgumentException("DEK must be 32 bytes.", nameof(dek));

        var (_, _, nonce, ctTag) = ParseEnvelope(envelope);

        if (ctTag.Length < TagSize)
            throw new FormatException("Envelope ciphertext+tag too short.");

        var ciphertext  = ctTag[..^TagSize];
        var tag         = ctTag[^TagSize..];
        var aad         = BuildSecretAad(spaceId, pointId, fieldTag);
        var plainBytes  = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(dek, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes, aad);  // throws on auth failure

        return Encoding.UTF8.GetString(plainBytes);
    }

    // ── Envelope format ───────────────────────────────────────────────────────

    /// <summary>True if the string uses the envelope format (vs legacy DataProtection).</summary>
    public static bool IsEnvelope(string ciphertext) =>
        ciphertext.StartsWith(EnvelopeVersion + ".", StringComparison.Ordinal);

    /// <summary>
    /// Parse an envelope string into (kekId, dekVersion, nonce, ctTag).
    /// Throws <see cref="FormatException"/> on any structural issue.
    /// </summary>
    public static (string KekId, int DekVersion, byte[] Nonce, byte[] CtTag) ParseEnvelope(string envelope)
    {
        // Format: kenv1.{kek_id}.{dek_version}.{b64url(nonce)}.{b64url(ct||tag)}
        var parts = envelope.Split('.');
        if (parts.Length != 5 || parts[0] != EnvelopeVersion)
            throw new FormatException($"Invalid envelope format: expected 5 dot-separated parts starting with '{EnvelopeVersion}'.");

        var kekId = parts[1];
        if (!int.TryParse(parts[2], out var dekVersion))
            throw new FormatException("Invalid dek_version in envelope.");

        byte[] nonce, ctTag;
        try
        {
            nonce = Base64UrlDecode(parts[3]);
            ctTag = Base64UrlDecode(parts[4]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Envelope contains invalid base64url data.", ex);
        }

        if (nonce.Length != NonceSize)
            throw new FormatException($"Envelope nonce must be {NonceSize} bytes.");

        return (kekId, dekVersion, nonce, ctTag);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string BuildEnvelopeString(string kekId, int dekVersion, byte[] nonce, byte[] ctTag) =>
        $"{EnvelopeVersion}.{kekId}.{dekVersion}.{Base64UrlEncode(nonce)}.{Base64UrlEncode(ctTag)}";

    private static byte[] BuildDekAad(string spaceId, string kekId, int dekVersion) =>
        Encoding.UTF8.GetBytes($"{DekAadPrefix}{spaceId}{Sep}{kekId}{Sep}{dekVersion}");

    private static byte[] BuildSecretAad(string spaceId, string pointId, string fieldTag) =>
        Encoding.UTF8.GetBytes($"{SecretAadPrefix}{spaceId}{Sep}{pointId}{Sep}{fieldTag}");

    private static byte[] GenerateNonce()
    {
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
