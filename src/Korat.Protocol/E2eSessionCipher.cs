// 031-relay-confidentiality: per-session AES-256-GCM payload cipher.
// Deterministic nonce eliminates per-message randomness while keeping the NIST soundness
// requirement (fresh key per session ⇒ nonce can be deterministic without repetition).
// Wire format: tag(16) || ciphertext (nonce NOT transmitted; derived deterministically).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Korat.Protocol;

/// <summary>
/// AES-256-GCM encrypt/decrypt for a single relay session direction pair.
///
/// <para><b>Nonce scheme</b> (deterministic; nonce NOT sent on the wire):</para>
/// <code>nonce = dir(1B) || 0x000000(3B) || seq(8B big-endian) = 12 bytes</code>
/// <list type="bullet">
///   <item><c>dir</c>: 0x00 = agent→publisher ("client_to_server"), 0x01 = publisher→agent.</item>
///   <item><c>seq</c>: strict per-direction monotonic counter starting at 0.</item>
/// </list>
/// A fresh K_payload per session guarantees nonce uniqueness across all frames in both
/// directions (same key, different dir byte).
///
/// <para><b>AAD</b> (protects metadata from tampering and frames from cross-session/direction/seq replay):</para>
/// <code>AAD = "korat-frame-v1" || session_id_utf8 || 0x00 || dir(1B) || seq(8BE) || SHA256(meta_bytes)</code>
/// Where <c>meta_bytes</c> is the serialized <see cref="Korat.Relay.V1.FrameMetadata"/> proto bytes (or
/// an all-zero 32-byte hash when no metadata is present, for legacy compatibility).
///
/// <para><b>Wire format</b>: <c>tag(16) || ciphertext</c>. No length prefix — the caller owns framing.</para>
///
/// <para><b>Replay / reorder protection</b>: the receiver enforces strict monotonic seq per direction;
/// any frame with seq ≤ last-seen is rejected with <see cref="CryptographicException"/>.</para>
///
/// <para><b>Memory</b>: K_payload is zeroed on <see cref="Dispose"/>.</para>
///
/// <para><b>Concurrency</b>: <see cref="Seal"/> is safe for concurrent calls from multiple threads
/// — each call atomically claims its own sequence number via <see cref="Interlocked"/>
/// so two concurrent <c>Seal</c> calls can never reuse a nonce. <see cref="Open"/> enforces
/// strict monotonic receive-seq per direction; callers are expected to serialize <see cref="Open"/>
/// per direction (one reader per direction).</para>
/// </summary>
public sealed class E2eSessionCipher : IDisposable
{
    // Direction bytes (wire)
    public const byte DirClientToServer = 0x00; // agent → publisher
    public const byte DirServerToClient = 0x01; // publisher → agent

    // Map direction strings (matching RelayFrame.direction proto field values) to direction bytes
    public const string DirectionClientToServer = "client_to_server";
    public const string DirectionServerToClient = "server_to_client";

    private const int TagSize   = 16;
    private const int NonceSize = 12;
    private const int AadPrefixLen = 14 + 1 + 1 + 8; // "korat-frame-v1"(14) + NUL(1) + dir(1) + seq(8)
    private const int MetaHashSize = 32;

    private static readonly byte[] AadPreamble = "korat-frame-v1"u8.ToArray();

    private readonly byte[] _key;
    private readonly string _sessionId;
    private readonly byte[] _sessionIdUtf8;

    // Per-direction send/receive sequence counters.
    // Send counters as long so Interlocked.Increment works; cast to ulong at use site.
    private long _sendSeqC2SL; // agent→publisher outgoing (Interlocked)
    private long _sendSeqS2CL; // publisher→agent outgoing (Interlocked)
    private long  _lastRecvSeqC2S = -1; // -1 = not yet received
    private long  _lastRecvSeqS2C = -1;

    private bool _disposed;

    /// <param name="kPayload">32-byte AES-256 key. Caller must zero after construction if it no longer needs it; this class keeps its own copy.</param>
    /// <param name="sessionId">Relay-session identifier; included in AAD for cross-session isolation.</param>
    public E2eSessionCipher(byte[] kPayload, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(kPayload);
        if (kPayload.Length != 32) throw new ArgumentException("K_payload must be 32 bytes.", nameof(kPayload));
        ArgumentNullException.ThrowIfNull(sessionId);

        _key          = (byte[])kPayload.Clone();
        _sessionId    = sessionId;
        _sessionIdUtf8 = Encoding.UTF8.GetBytes(sessionId);
    }

    // ── Seal (encrypt) ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> for the given direction, stamping the next sequence number.
    /// Returns <c>tag(16) || ciphertext</c>.
    /// Also outputs the <paramref name="seqUsed"/> so the caller can stamp the same value
    /// as <c>RelayFrame.SequenceNumber</c> for the receiver to reconstruct the nonce.
    /// </summary>
    /// <param name="direction">One of <see cref="DirClientToServer"/> or <see cref="DirServerToClient"/>.</param>
    /// <param name="metaBytes">Serialized FrameMetadata proto bytes (or empty/null for no metadata).</param>
    /// <param name="seqUsed">Outputs the sequence number consumed for this frame.</param>
    public byte[] Seal(ReadOnlySpan<byte> plaintext, byte direction, ReadOnlySpan<byte> metaBytes, out ulong seqUsed)
    {
        ThrowIfDisposed();
        ValidateDirection(direction);

        var seq = NextSendSeq(direction);
        seqUsed = seq;
        var nonce = BuildNonce(direction, seq);
        var aad   = BuildAad(_sessionIdUtf8, direction, seq, metaBytes);

        var ct  = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ct, tag, aad);

        var wire = new byte[TagSize + ct.Length];
        tag.CopyTo(wire.AsSpan(0, TagSize));
        ct.CopyTo(wire.AsSpan(TagSize));
        return wire;
    }

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> for the given direction, stamping the next sequence number.
    /// Returns <c>tag(16) || ciphertext</c>.
    /// </summary>
    /// <param name="direction">One of <see cref="DirClientToServer"/> or <see cref="DirServerToClient"/>.</param>
    /// <param name="metaBytes">Serialized FrameMetadata proto bytes (or empty/null for no metadata).</param>
    public byte[] Seal(ReadOnlySpan<byte> plaintext, byte direction, ReadOnlySpan<byte> metaBytes = default)
    {
        ThrowIfDisposed();
        ValidateDirection(direction);

        var seq = NextSendSeq(direction);
        var nonce = BuildNonce(direction, seq);
        var aad   = BuildAad(_sessionIdUtf8, direction, seq, metaBytes);

        var ct  = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ct, tag, aad);

        var wire = new byte[TagSize + ct.Length];
        tag.CopyTo(wire.AsSpan(0, TagSize));
        ct.CopyTo(wire.AsSpan(TagSize));
        return wire;
    }

    // ── Open (decrypt) ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypt a received frame for the given direction and sequence number.
    /// Enforces strict monotonic seq (replay/reorder protection).
    /// Returns plaintext on success; throws <see cref="CryptographicException"/> on any failure.
    /// </summary>
    /// <param name="direction">Direction byte of the RECEIVED frame (opposite of our send direction).</param>
    /// <param name="seq">Sequence number carried in the nonce (derived from the AAD reconstruction).</param>
    /// <param name="wire">Received bytes: <c>tag(16) || ciphertext</c>.</param>
    /// <param name="metaBytes">Serialized FrameMetadata proto bytes matching what the sender stamped.</param>
    public byte[] Open(ReadOnlySpan<byte> wire, byte direction, ulong seq, ReadOnlySpan<byte> metaBytes = default)
    {
        ThrowIfDisposed();
        ValidateDirection(direction);

        if (wire.Length < TagSize)
            throw new CryptographicException("Sealed frame too short (< 16 bytes for tag).");

        EnforceMonotonicSeq(direction, seq);

        var nonce = BuildNonce(direction, seq);
        var aad   = BuildAad(_sessionIdUtf8, direction, seq, metaBytes);

        var tag = wire[..TagSize];
        var ct  = wire[TagSize..];
        var pt  = new byte[ct.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ct, tag, pt, aad);

        UpdateLastRecvSeq(direction, seq);
        return pt;
    }

    // ── Nonce construction ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the deterministic 12-byte nonce: <c>dir(1) || 0x00 0x00 0x00(3) || seq(8 big-endian)</c>.
    /// </summary>
    public static byte[] BuildNonce(byte direction, ulong seq)
    {
        var nonce = new byte[NonceSize];
        nonce[0] = direction;
        // bytes 1–3: zero padding
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), seq);
        return nonce;
    }

    // ── AAD construction ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build AAD: <c>"korat-frame-v1" || sessionId_utf8 || 0x00 || dir(1B) || seq(8BE) || SHA256(metaBytes)</c>.
    /// When <paramref name="metaBytes"/> is empty, SHA256(empty) is used.
    /// Note: SHA256("") = e3b0c44298fc1c14... (the standard SHA-256 hash of the empty byte sequence; not all-zeros).
    /// </summary>
    public static byte[] BuildAad(byte[] sessionIdUtf8, byte direction, ulong seq, ReadOnlySpan<byte> metaBytes)
    {
        var metaHash = metaBytes.IsEmpty
            ? SHA256.HashData(ReadOnlySpan<byte>.Empty)
            : SHA256.HashData(metaBytes);

        // "korat-frame-v1"(14) + sessionId + NUL(1) + dir(1) + seq(8) + metaHash(32)
        var aad = new byte[AadPreamble.Length + sessionIdUtf8.Length + 1 + 1 + 8 + MetaHashSize];
        var span = aad.AsSpan();

        AadPreamble.CopyTo(span);
        span = span[AadPreamble.Length..];

        sessionIdUtf8.CopyTo(span);
        span = span[sessionIdUtf8.Length..];

        span[0] = 0x00; // NUL separator
        span = span[1..];

        span[0] = direction;
        span = span[1..];

        BinaryPrimitives.WriteUInt64BigEndian(span[..8], seq);
        span = span[8..];

        metaHash.CopyTo(span);

        return aad;
    }

    // ── Direction helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>Map a proto direction string to the wire byte.</summary>
    public static byte DirectionByte(string direction) => direction switch
    {
        DirectionClientToServer => DirClientToServer,
        DirectionServerToClient => DirServerToClient,
        _ => throw new ArgumentException($"Unknown direction: {direction}", nameof(direction))
    };

    // ── Internal seq tracking ────────────────────────────────────────────────────────────────────

    private ulong NextSendSeq(byte direction)
    {
        // Interlocked.Increment returns the new value; we want pre-increment semantics
        // (seq-before-increment), so subtract 1 from the incremented value.
        long prev = direction == DirClientToServer
            ? Interlocked.Increment(ref _sendSeqC2SL) - 1L
            : Interlocked.Increment(ref _sendSeqS2CL) - 1L;
        return (ulong)prev;
    }

    private void EnforceMonotonicSeq(byte direction, ulong seq)
    {
        // Reject seq values that would overflow long: a frame with seq > long.MaxValue would
        // cast to a negative long, pass the last >= 0 guard, and disable monotonic enforcement.
        if (seq > (ulong)long.MaxValue)
            throw new CryptographicException(
                $"Sequence number out of range: seq={seq} exceeds long.MaxValue. Rejecting frame.");

        ref var last = ref (direction == DirClientToServer ? ref _lastRecvSeqC2S : ref _lastRecvSeqS2C);
        if (last >= 0 && (long)seq <= last)
            throw new CryptographicException(
                $"Replay or reorder detected: seq={seq} last={last} dir={direction}");
    }

    private void UpdateLastRecvSeq(byte direction, ulong seq)
    {
        if (direction == DirClientToServer)
            _lastRecvSeqC2S = (long)seq;
        else
            _lastRecvSeqS2C = (long)seq;
    }

    private static void ValidateDirection(byte direction)
    {
        if (direction != DirClientToServer && direction != DirServerToClient)
            throw new ArgumentException($"Invalid direction byte: {direction}");
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(E2eSessionCipher));
    }
}
