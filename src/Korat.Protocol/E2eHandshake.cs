// 031-relay-confidentiality: per-session ECDH key exchange primitives.
// Uses only System.Security.Cryptography (P-256 + HKDF-SHA256 + HMAC-SHA256).
// NO X25519 — not available in net10.0 ref assemblies; curve field reserved for future.
// Key material never logged, persisted, or grain-stored — lives in-process only.
using System.Security.Cryptography;
using System.Text;

namespace Korat.Protocol;

/// <summary>
/// Per-session ephemeral ECDH (P-256) key exchange primitives for the 031 relay E2E layer.
///
/// <para>Typical agent-side usage:</para>
/// <code>
///   using var ep = E2eHandshake.CreateEphemeral();
///   var salt  = E2eHandshake.GenerateSalt();
///   // → send E2eKeyOffer{pub_key = ep.ExportSpki(), salt = salt}
///   // ← receive E2eKeyAnswer{pub_key = peerSpki, confirm_tag = peerTag}
///   var th    = E2eHandshake.BuildTranscriptHash(sessionId, agentId, publisherId, salt, ep.ExportSpki(), peerSpki);
///   var keys  = E2eHandshake.Derive(ep, peerSpki, salt, th);
///   E2eHandshake.VerifyConfirm(keys.KConf, E2eHandshake.PublisherConfirmLabel, th, peerTag);
///   var myTag = E2eHandshake.ComputeConfirm(keys.KConf, E2eHandshake.AgentConfirmLabel, th);
///   // → send E2eKeyConfirm{confirm_tag = myTag}
///   using var cipher = new E2eSessionCipher(keys.KPayload);
/// </code>
///
/// <para>Trust model (passive cloud / residual active-MITM — see protocol/CRYPTO.md §2):</para>
/// <list type="bullet">
///   <item>Passive cloud (DB/log/sniff) sees only public keys, salt, confirm tags,
///     ciphertexts and metadata — cannot derive K_payload.</item>
///   <item>Active cloud (key swap) is a DOCUMENTED RESIDUAL: confirm tags bind the
///     transcript but a key-swapping relay produces a consistent forged transcript.
///     Upgrade path: publisher long-term identity keypair; TOFU/owner pinning (later leg).</item>
/// </list>
/// </summary>
public sealed class E2eHandshake : IDisposable
{
    // ── Labels ──────────────────────────────────────────────────────────────────────────────────

    public const string PublisherConfirmLabel = "publisher-confirm";
    public const string AgentConfirmLabel     = "agent-confirm";

    // ── HKDF info prefix ────────────────────────────────────────────────────────────────────────

    private const string HkdfInfoPrefix = "korat-relay-e2e-v1";

    // ── Key derivation output sizes ──────────────────────────────────────────────────────────────

    private const int KPayloadBytes  = 32;
    private const int KConfBytes     = 32;
    private const int KReservedBytes = 32;
    private const int TotalOkmBytes  = KPayloadBytes + KConfBytes + KReservedBytes;

    private const int SaltSize = 16;
    private const int ConfirmTagSize = 32; // HMAC-SHA256

    // ── Internal state ───────────────────────────────────────────────────────────────────────────

    private readonly ECDiffieHellman _ecdh;
    private bool _disposed;

    private E2eHandshake(ECDiffieHellman ecdh) => _ecdh = ecdh;

    // ── Factory ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Create a new ephemeral P-256 ECDH key pair. Caller owns the returned instance (IDisposable).</summary>
    public static E2eHandshake CreateEphemeral()
        => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Generate a 16-byte random salt for the HKDF call.</summary>
    public static byte[] GenerateSalt()
        => RandomNumberGenerator.GetBytes(SaltSize);

    // ── Export ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Export this party's ephemeral public key in SPKI DER encoding for wire transmission.</summary>
    public byte[] ExportSpki()
    {
        ThrowIfDisposed();
        return _ecdh.ExportSubjectPublicKeyInfo();
    }

    // ── Transcript hash ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the transcript hash that binds both parties' identities and ephemeral keys.
    /// <c>transcript = SHA256(session_id || '\0' || agent_client_id || '\0' || publisher_node_id || '\0'
    ///                        || salt || agent_spki || publisher_spki)</c>
    /// Byte-boundary NUL separators prevent field concatenation collisions.
    /// </summary>
    public static byte[] BuildTranscriptHash(
        string sessionId,
        string agentClientId,
        string publisherNodeId,
        byte[] salt,
        byte[] agentSpki,
        byte[] publisherSpki)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(agentClientId);
        ArgumentNullException.ThrowIfNull(publisherNodeId);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(agentSpki);
        ArgumentNullException.ThrowIfNull(publisherSpki);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(sessionId));
        sha.AppendData([0x00]);
        sha.AppendData(Encoding.UTF8.GetBytes(agentClientId));
        sha.AppendData([0x00]);
        sha.AppendData(Encoding.UTF8.GetBytes(publisherNodeId));
        sha.AppendData([0x00]);
        sha.AppendData(salt);
        sha.AppendData(agentSpki);
        sha.AppendData(publisherSpki);
        return sha.GetHashAndReset();
    }

    // ── Key derivation ───────────────────────────────────────────────────────────────────────────

    /// <summary>Derived session keys.</summary>
    /// <param name="KPayload">32-byte AES-256 key for payload encryption.</param>
    /// <param name="KConf">32-byte key for HMAC-based confirm tags.</param>
    public sealed record DerivedKeys(byte[] KPayload, byte[] KConf) : IDisposable
    {
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(KPayload);
            CryptographicOperations.ZeroMemory(KConf);
        }
    }

    /// <summary>
    /// Perform ECDH with <paramref name="peerSpki"/> and derive session keys via HKDF-SHA256.
    /// <c>info = UTF8("korat-relay-e2e-v1") || transcript_hash</c>.
    /// Outputs 96 bytes: K_payload(32) | K_conf(32) | reserved(32).
    /// </summary>
    public DerivedKeys Derive(byte[] peerSpki, byte[] salt, byte[] transcriptHash)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peerSpki);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(transcriptHash);

        byte[] sharedSecret;
        using (var peerEcdh = ECDiffieHellman.Create())
        {
            try
            {
                peerEcdh.ImportSubjectPublicKeyInfo(peerSpki, out _);
                // DeriveRawSecretAgreement throws ArgumentException when curves differ (e.g. P-384 key
                // tagged p256). Map to CryptographicException so callers see a clean handshake failure.
                sharedSecret = _ecdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);
            }
            catch (ArgumentException ex)
            {
                throw new CryptographicException(
                    "ECDH key agreement failed: curve mismatch or invalid peer key.", ex);
            }
        }
        try
        {
            // info = "korat-relay-e2e-v1" || transcriptHash
            var info = BuildHkdfInfo(transcriptHash);

            var okm = new byte[TotalOkmBytes];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, okm, salt, info);

            var kPayload = okm[..KPayloadBytes];
            var kConf    = okm[KPayloadBytes..(KPayloadBytes + KConfBytes)];
            // reserved slice zeroed immediately (not returned)
            CryptographicOperations.ZeroMemory(okm.AsSpan(KPayloadBytes + KConfBytes));

            return new DerivedKeys(kPayload, kConf);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    // ── Confirm tags ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute HMAC-SHA256(K_conf, UTF8(label) || transcript_hash).
    /// Result is 32 bytes.
    /// </summary>
    public static byte[] ComputeConfirm(byte[] kConf, string label, byte[] transcriptHash)
    {
        ArgumentNullException.ThrowIfNull(kConf);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(transcriptHash);

        var labelBytes = Encoding.UTF8.GetBytes(label);
        var input = new byte[labelBytes.Length + transcriptHash.Length];
        labelBytes.CopyTo(input, 0);
        transcriptHash.CopyTo(input, labelBytes.Length);
        return HMACSHA256.HashData(kConf, input);
    }

    /// <summary>
    /// Constant-time verify of a confirm tag. Throws <see cref="CryptographicException"/>
    /// if the tag does not match (wrong key or forged transcript).
    /// </summary>
    public static void VerifyConfirm(byte[] kConf, string label, byte[] transcriptHash, byte[] expectedTag)
    {
        ArgumentNullException.ThrowIfNull(expectedTag);
        if (expectedTag.Length != ConfirmTagSize)
            throw new CryptographicException($"Confirm tag must be {ConfirmTagSize} bytes.");

        var actual = ComputeConfirm(kConf, label, transcriptHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedTag))
            throw new CryptographicException("E2E confirm tag verification failed.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static byte[] BuildHkdfInfo(byte[] transcriptHash)
    {
        var prefix = Encoding.UTF8.GetBytes(HkdfInfoPrefix);
        var info   = new byte[prefix.Length + transcriptHash.Length];
        prefix.CopyTo(info, 0);
        transcriptHash.CopyTo(info, prefix.Length);
        return info;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(E2eHandshake));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ecdh.Dispose();
    }
}
