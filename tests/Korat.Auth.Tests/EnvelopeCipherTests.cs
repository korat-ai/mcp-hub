using System.Security.Cryptography;
using System.Text;
using Korat.Cloud.Security.Envelope;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for EnvelopeCipher (pure AES-256-GCM, no I/O).
///
/// Security acceptance covered:
///   - Round-trip: EncryptSecret / DecryptSecret produces original plaintext.
///   - DEK wrap / unwrap round-trip.
///   - Wrong KEK → CryptographicException (GCM auth failure).
///   - Tampered ciphertext → CryptographicException.
///   - Tampered nonce → CryptographicException.
///   - Tampered AAD (wrong spaceId, wrong pointId, wrong fieldTag) → CryptographicException.
///   - Cross-space: envelope from spaceA cannot be decrypted as spaceB's.
///   - Cross-point: envelope cannot be transferred to a sibling point in the same space.
///   - Nonce uniqueness: 1000 encryptions of same plaintext produce distinct nonces.
///   - Envelope format discriminator: IsEnvelope() / ParseEnvelope().
///   - No plaintext in returned envelope string.
/// </summary>
public sealed class EnvelopeCipherTests
{
    // 256-bit test keys
    private static byte[] Key32() { var k = new byte[32]; RandomNumberGenerator.Fill(k); return k; }
    private const string SpaceA = "space-aaaa";
    private const string SpaceB = "space-bbbb";
    private const string PointA = "point-1111";
    private const string PointB = "point-2222";
    private const string KekId1 = "k1";

    // ── DEK wrap / unwrap ─────────────────────────────────────────────────────

    [Fact]
    public void WrapDek_UnwrapDek_RoundTrip()
    {
        var kek = Key32();
        var dek = Key32();
        var (nonce, wrapped) = EnvelopeCipher.WrapDek(kek, dek, SpaceA, KekId1, 1);

        Assert.Equal(EnvelopeCipher.NonceSize, nonce.Length);
        Assert.Equal(EnvelopeCipher.KeySize + EnvelopeCipher.TagSize, wrapped.Length);

        var unwrapped = EnvelopeCipher.UnwrapDek(kek, nonce, wrapped, SpaceA, KekId1, 1);
        Assert.Equal(dek, unwrapped);
    }

    [Fact]
    public void UnwrapDek_Wrong_Kek_Throws_CryptographicException()
    {
        var kek = Key32();
        var wrongKek = Key32();
        var dek = Key32();
        var (nonce, wrapped) = EnvelopeCipher.WrapDek(kek, dek, SpaceA, KekId1, 1);

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.UnwrapDek(wrongKek, nonce, wrapped, SpaceA, KekId1, 1));
    }

    [Fact]
    public void UnwrapDek_Tampered_WrappedDek_Throws()
    {
        var kek = Key32();
        var dek = Key32();
        var (nonce, wrapped) = EnvelopeCipher.WrapDek(kek, dek, SpaceA, KekId1, 1);
        wrapped[0] ^= 0xFF; // flip first byte

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.UnwrapDek(kek, nonce, wrapped, SpaceA, KekId1, 1));
    }

    [Fact]
    public void UnwrapDek_Wrong_SpaceId_In_Aad_Throws()
    {
        var kek = Key32();
        var dek = Key32();
        var (nonce, wrapped) = EnvelopeCipher.WrapDek(kek, dek, SpaceA, KekId1, 1);

        // AAD mismatch: wrong space
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.UnwrapDek(kek, nonce, wrapped, SpaceB, KekId1, 1));
    }

    // ── Secret encrypt / decrypt ──────────────────────────────────────────────

    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var dek = Key32();
        const string plaintext = "sk-test-openai-key-12345";

        var envelope = EnvelopeCipher.EncryptSecret(dek, plaintext, SpaceA, PointA, KekId1, 1);
        var result   = EnvelopeCipher.DecryptSecret(dek, envelope, SpaceA, PointA);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void EncryptDecrypt_EmptyString_Works()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "", SpaceA, PointA, KekId1, 1);
        var result   = EnvelopeCipher.DecryptSecret(dek, envelope, SpaceA, PointA);
        Assert.Equal("", result);
    }

    [Fact]
    public void DecryptSecret_Wrong_Dek_Throws_CryptographicException()
    {
        var dek      = Key32();
        var wrongDek = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "secret", SpaceA, PointA, KekId1, 1);

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(wrongDek, envelope, SpaceA, PointA));
    }

    [Fact]
    public void DecryptSecret_Tampered_Ciphertext_Throws()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "my-secret", SpaceA, PointA, KekId1, 1);

        // Tamper the last part (ctTag in base64url) by flipping a char
        var parts = envelope.Split('.');
        var ctTagBytes = Convert.FromBase64String(
            parts[4].Replace('-', '+').Replace('_', '/').PadRight(parts[4].Length + (4 - parts[4].Length % 4) % 4, '='));
        ctTagBytes[0] ^= 0xFF;
        var tampered = string.Join('.', parts[..4]) + "." +
            Convert.ToBase64String(ctTagBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, tampered, SpaceA, PointA));
    }

    [Fact]
    public void DecryptSecret_Wrong_SpaceId_Aad_Throws()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "secret", SpaceA, PointA, KekId1, 1);

        // SpaceB AAD mismatch
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, envelope, SpaceB, PointA));
    }

    [Fact]
    public void DecryptSecret_Wrong_PointId_Aad_Throws()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "secret", SpaceA, PointA, KekId1, 1);

        // PointB AAD mismatch — cross-point in same space
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, envelope, SpaceA, PointB));
    }

    [Fact]
    public void DecryptSecret_Wrong_FieldTag_Aad_Throws()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "secret", SpaceA, PointA, KekId1, 1,
            fieldTag: "provider_secret");

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, envelope, SpaceA, PointA, fieldTag: "other_field"));
    }

    // ── Cross-space isolation ─────────────────────────────────────────────────

    [Fact]
    public void SpaceA_Envelope_Cannot_Decrypt_As_SpaceB()
    {
        // Simulates: attacker copies ciphertext from spaceA row to spaceB row in DB dump.
        var dekA     = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dekA, "spaceA-secret", SpaceA, PointA, KekId1, 1);

        // SpaceB has the SAME DEK material (worst case: DEK leaked) but different spaceId in AAD
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dekA, envelope, SpaceB, PointA));
    }

    [Fact]
    public void Envelope_Cross_Point_Same_Space_Fails()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "my-secret", SpaceA, PointA, KekId1, 1);

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, envelope, SpaceA, PointB));
    }

    // ── Nonce uniqueness ──────────────────────────────────────────────────────

    [Fact]
    public void EncryptSecret_Nonces_Are_Unique_Across_Many_Calls()
    {
        var dek    = Key32();
        var nonces = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var envelope = EnvelopeCipher.EncryptSecret(dek, "secret", SpaceA, PointA, KekId1, 1);
            var (_, _, nonce, _) = EnvelopeCipher.ParseEnvelope(envelope);
            nonces.Add(Convert.ToBase64String(nonce));
        }
        Assert.Equal(1000, nonces.Count);
    }

    // ── Envelope format ───────────────────────────────────────────────────────

    [Fact]
    public void IsEnvelope_DetectsEnvelopeFormat()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "x", SpaceA, PointA, KekId1, 1);
        Assert.True(EnvelopeCipher.IsEnvelope(envelope));
    }

    [Fact]
    public void IsEnvelope_ReturnsFalse_For_LegacyDp()
    {
        // Legacy DataProtection output is base64 without dots — "kenv1." prefix will not be present.
        const string dpCiphertext = "CfDJ8KF7...legacy...base64==";
        Assert.False(EnvelopeCipher.IsEnvelope(dpCiphertext));
    }

    [Fact]
    public void ParseEnvelope_Returns_Correct_Kek_And_DekVersion()
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "val", SpaceA, PointA, "kek-v2", 3);
        var (kekId, dekVersion, _, _) = EnvelopeCipher.ParseEnvelope(envelope);
        Assert.Equal("kek-v2", kekId);
        Assert.Equal(3, dekVersion);
    }

    [Fact]
    public void ParseEnvelope_ThrowsFormatException_On_Invalid_String()
    {
        Assert.Throws<FormatException>(() => EnvelopeCipher.ParseEnvelope("not-an-envelope"));
        Assert.Throws<FormatException>(() => EnvelopeCipher.ParseEnvelope("kenv1.only.three.parts"));
    }

    // ── No plaintext in envelope string ──────────────────────────────────────

    [Fact]
    public void Envelope_String_Does_Not_Contain_Plaintext()
    {
        var dek      = Key32();
        const string plaintext = "super-secret-api-key-do-not-store-clear";
        var envelope = EnvelopeCipher.EncryptSecret(dek, plaintext, SpaceA, PointA, KekId1, 1);

        Assert.DoesNotContain(plaintext, envelope, StringComparison.Ordinal);
        // Also check base64url of the plaintext doesn't appear (belt-and-suspenders)
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        Assert.DoesNotContain(b64, envelope, StringComparison.Ordinal);
    }

    // ── Tamper matrix: all components must individually fail auth ─────────────

    [Theory]
    [InlineData("tamper_ct")]
    [InlineData("tamper_nonce")]
    public void TamperMatrix_All_Fail_Auth(string tamperKind)
    {
        var dek      = Key32();
        var envelope = EnvelopeCipher.EncryptSecret(dek, "secret-value", SpaceA, PointA, KekId1, 1);
        var parts    = envelope.Split('.');

        if (tamperKind == "tamper_ct")
        {
            // Flip a byte in ctTag (part[4])
            var bytes = Base64UrlDecode(parts[4]);
            bytes[0] ^= 0xFF;
            parts[4] = Base64UrlEncode(bytes);
        }
        else // tamper_nonce
        {
            var bytes = Base64UrlDecode(parts[3]);
            bytes[0] ^= 0xFF;
            parts[3] = Base64UrlEncode(bytes);
        }

        var tampered = string.Join('.', parts);
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeCipher.DecryptSecret(dek, tampered, SpaceA, PointA));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "=";  break;
        }
        return Convert.FromBase64String(padded);
    }
}
