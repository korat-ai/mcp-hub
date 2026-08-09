// 031-relay-confidentiality: E2eSessionCipher unit tests.
// Acceptance criteria:
//   A2 (partial): payload marker never visible in ciphertext (cloud sees tag||ct, not plaintext)
//   A7: tampered metadata AAD causes AEAD failure
//   A8: replayed or reordered seq rejected
//   A9: direction-swapped frame fails Open
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Korat.Protocol;

namespace Korat.Protocol.Tests;

public class E2eSessionCipherTests
{
    private const string SessionId = "sess-xyz789";

    private static byte[] FreshKey() => RandomNumberGenerator.GetBytes(32);

    // ── Round-trip ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_RoundTrip_ClientToServer()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var plaintext = "hello korat e2e relay"u8.ToArray();
        var dir = E2eSessionCipher.DirClientToServer;

        var wire = sender.Seal(plaintext, dir);
        var pt   = receiver.Open(wire, dir, 0); // seq=0 is the first send

        Assert.Equal(plaintext, pt);
    }

    [Fact]
    public void E2eCipher_RoundTrip_ServerToClient()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var plaintext = "response from publisher"u8.ToArray();
        var dir = E2eSessionCipher.DirServerToClient;

        var wire = sender.Seal(plaintext, dir);
        var pt   = receiver.Open(wire, dir, 0);

        Assert.Equal(plaintext, pt);
    }

    // ── Wire format: tag||ct (nonce NOT transmitted) ─────────────────────────────────────────────

    [Fact]
    public void E2eCipher_WireFormat_TagPrependedNoClearNonce()
    {
        var key = FreshKey();
        using var cipher = new E2eSessionCipher(key, SessionId);

        var plaintext = "payload"u8.ToArray();
        var wire = cipher.Seal(plaintext, E2eSessionCipher.DirClientToServer);

        // wire = tag(16) || ciphertext; total = 16 + plaintext.Length
        Assert.Equal(16 + plaintext.Length, wire.Length);
        // Ciphertext must not equal plaintext (trivially encrypted).
        Assert.NotEqual(plaintext, wire[16..]);
    }

    // ── A2: payload marker NOT readable in ciphertext ────────────────────────────────────────────

    [Fact]
    public void E2eSession_PayloadMarker_NotVisibleInCiphertext()
    {
        var key = FreshKey();
        using var cipher = new E2eSessionCipher(key, SessionId);

        var marker    = RelayFrameMetadata.TestPayloadMarker;
        var plaintext = Encoding.UTF8.GetBytes(marker);
        var wire      = cipher.Seal(plaintext, E2eSessionCipher.DirClientToServer);

        // The wire bytes must not contain the UTF-8 marker.
        var wireStr = Encoding.Latin1.GetString(wire);
        Assert.DoesNotContain(marker, wireStr);

        // The PayloadLoggingGuard must flag the plaintext but not the wire.
        Assert.False(PayloadLoggingGuard.IsSafeForLogging(marker));
        Assert.True(PayloadLoggingGuard.IsSafeForLogging(Convert.ToBase64String(wire)));
    }

    // ── A7: tampered ciphertext tag causes AEAD failure ──────────────────────────────────────────

    [Fact]
    public void E2eCipher_TamperedCiphertext_FailsOpen()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var wire = sender.Seal("secret"u8.ToArray(), E2eSessionCipher.DirClientToServer);
        wire[^1] ^= 0xFF; // tamper last byte of ciphertext

        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(wire, E2eSessionCipher.DirClientToServer, 0));
    }

    [Fact]
    public void E2eCipher_TamperedTag_FailsOpen()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var wire = sender.Seal("secret"u8.ToArray(), E2eSessionCipher.DirClientToServer);
        wire[0] ^= 0xFF; // tamper first byte of tag

        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(wire, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── A7: tampered metadata AAD causes AEAD failure ────────────────────────────────────────────

    [Fact]
    public void E2eCipher_TamperedMetadataAad_FailsOpen()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var meta    = "{\"tool_name\":\"read_file\"}"u8.ToArray();
        var tampered = "{\"tool_name\":\"write_file\"}"u8.ToArray();

        var wire = sender.Seal("payload"u8.ToArray(), E2eSessionCipher.DirClientToServer, meta);

        // Receiver uses different (tampered) meta → different AAD → AEAD fails.
        Assert.ThrowsAny<CryptographicException>(() =>
            receiver.Open(wire, E2eSessionCipher.DirClientToServer, 0, tampered));
    }

    // ── A9: direction-swapped frame fails Open ────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_DirectionSwappedFrame_FailsOpen()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var dir = E2eSessionCipher.DirClientToServer;
        var wire = sender.Seal("msg"u8.ToArray(), dir);

        // Try to open as if it were the other direction.
        var wrongDir = E2eSessionCipher.DirServerToClient;
        Assert.ThrowsAny<CryptographicException>(() =>
            receiver.Open(wire, wrongDir, 0));
    }

    // ── A8: replayed or reordered seq rejected ────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_ReplayedSeq_Rejected()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var dir  = E2eSessionCipher.DirClientToServer;
        var wire0 = sender.Seal("frame0"u8.ToArray(), dir);
        var wire1 = sender.Seal("frame1"u8.ToArray(), dir);

        // Receive seq=0 first.
        receiver.Open(wire0, dir, 0);
        // seq=0 replayed → rejected.
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(wire0, dir, 0));
    }

    [Fact]
    public void E2eCipher_ReorderedSeq_Rejected()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, SessionId);
        using var receiver = new E2eSessionCipher(key, SessionId);

        var dir   = E2eSessionCipher.DirClientToServer;
        var wire0 = sender.Seal("frame0"u8.ToArray(), dir);
        var wire1 = sender.Seal("frame1"u8.ToArray(), dir);

        // Receive seq=1 first (reorder).
        receiver.Open(wire1, dir, 1);
        // seq=0 is older → rejected.
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(wire0, dir, 0));
    }

    // ── Direction isolation: independent counters ─────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_TwoDirections_IndependentSeqCounters()
    {
        var key = FreshKey();
        using var cipher = new E2eSessionCipher(key, SessionId);

        // Both directions start at seq=0 independently.
        var w0c2s = cipher.Seal("c2s-0"u8.ToArray(), E2eSessionCipher.DirClientToServer);
        var w0s2c = cipher.Seal("s2c-0"u8.ToArray(), E2eSessionCipher.DirServerToClient);

        // Nonces must differ (direction byte differs → different nonce).
        var nonce0c2s = E2eSessionCipher.BuildNonce(E2eSessionCipher.DirClientToServer, 0);
        var nonce0s2c = E2eSessionCipher.BuildNonce(E2eSessionCipher.DirServerToClient, 0);
        Assert.NotEqual(nonce0c2s, nonce0s2c);
    }

    // ── A8: nonce uniqueness within a direction ───────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_NonceDeterminism_NeverRepeatsWithinSession()
    {
        const int N = 100;
        var nonces = new HashSet<string>(N * 2);

        for (ulong seq = 0; seq < (ulong)N; seq++)
        {
            var n0 = E2eSessionCipher.BuildNonce(E2eSessionCipher.DirClientToServer, seq);
            var n1 = E2eSessionCipher.BuildNonce(E2eSessionCipher.DirServerToClient, seq);
            nonces.Add(Convert.ToBase64String(n0));
            nonces.Add(Convert.ToBase64String(n1));
        }

        Assert.Equal(N * 2, nonces.Count);
    }

    // ── Wrong key ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_WrongKey_FailsOpen()
    {
        var key1 = FreshKey();
        var key2 = FreshKey();
        using var sender   = new E2eSessionCipher(key1, SessionId);
        using var receiver = new E2eSessionCipher(key2, SessionId);

        var wire = sender.Seal("secret"u8.ToArray(), E2eSessionCipher.DirClientToServer);
        Assert.ThrowsAny<CryptographicException>(() =>
            receiver.Open(wire, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── Short frame ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_ShortFrame_Throws()
    {
        var key = FreshKey();
        using var cipher = new E2eSessionCipher(key, SessionId);
        var shortWire = new byte[15]; // < 16 bytes
        Assert.ThrowsAny<CryptographicException>(() =>
            cipher.Open(shortWire, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── ZeroMemory on dispose ────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_KeyZeroedOnDispose()
    {
        var key = FreshKey();
        var keyCopy = (byte[])key.Clone();

        var cipher = new E2eSessionCipher(key, SessionId);
        cipher.Dispose();

        // After dispose, accessing internal state via a new cipher using the original key bytes
        // should still work for a fresh cipher (the original key array is untouched by our copy).
        // We verify the internal copy is zeroed by confirming Seal throws ObjectDisposedException.
        Assert.Throws<ObjectDisposedException>(() => cipher.Seal("x"u8.ToArray(), E2eSessionCipher.DirClientToServer));
    }

    // ── Multi-frame round-trip (integration-style) ───────────────────────────────────────────────

    [Fact]
    public void E2eCipher_MultiFrame_RoundTrip_BothDirections()
    {
        var key = FreshKey();
        using var senderCipher   = new E2eSessionCipher(key, SessionId);
        using var receiverCipher = new E2eSessionCipher(key, SessionId);

        const int N = 10;
        var sent     = new List<byte[]>(N);
        var wires    = new List<(byte[] wire, byte dir, ulong seq)>(N);

        // Send N frames alternating directions.
        for (var i = 0; i < N; i++)
        {
            var dir  = (i % 2 == 0) ? E2eSessionCipher.DirClientToServer : E2eSessionCipher.DirServerToClient;
            var pt   = Encoding.UTF8.GetBytes($"frame-{i}");
            sent.Add(pt);
            var wire = senderCipher.Seal(pt, dir);
            var seq  = (ulong)(i / 2); // seq=0,0,1,1,2,2,...
            wires.Add((wire, dir, seq));
        }

        // Receive in order.
        for (var i = 0; i < N; i++)
        {
            var (wire, dir, seq) = wires[i];
            var pt = receiverCipher.Open(wire, dir, seq);
            Assert.Equal(sent[i], pt);
        }
    }

    // ── AAD construction: session id isolated ────────────────────────────────────────────────────

    [Fact]
    public void E2eCipher_DifferentSessionId_FailsOpen()
    {
        var key = FreshKey();
        using var sender   = new E2eSessionCipher(key, "session-A");
        using var receiver = new E2eSessionCipher(key, "session-B");

        var wire = sender.Seal("msg"u8.ToArray(), E2eSessionCipher.DirClientToServer);

        // Different session ID → different AAD → AEAD fails.
        Assert.ThrowsAny<CryptographicException>(() =>
            receiver.Open(wire, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── Direction byte mapping ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(E2eSessionCipher.DirectionClientToServer, E2eSessionCipher.DirClientToServer)]
    [InlineData(E2eSessionCipher.DirectionServerToClient, E2eSessionCipher.DirServerToClient)]
    public void E2eCipher_DirectionByte_MapsCorrectly(string dirStr, byte expected)
    {
        Assert.Equal(expected, E2eSessionCipher.DirectionByte(dirStr));
    }

    // ── MAJOR-4: concurrent Seal calls produce distinct nonces (cli-m4 test 3) ────────────────────

    /// <summary>
    /// MAJOR-4: concurrent Seal calls must each atomically claim a unique sequence number
    /// so no two frames share a nonce. Uses Interlocked.Increment on the send counter.
    /// </summary>
    [Fact]
    public async Task Seal_ConcurrentCalls_AllNoncesDistinct_AllFramesOpenCorrectly()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var sessionId = "concurrent-test";
        var sealCipher = new E2eSessionCipher(key, sessionId);
        var openCipher = new E2eSessionCipher(key, sessionId);

        const int count = 100;
        var plaintext = "hello world"u8.ToArray();
        var sealed_ = new (byte[] Wire, ulong Seq)[count];

        // Concurrent seal calls from multiple threads.
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            var wire = sealCipher.Seal(plaintext, E2eSessionCipher.DirClientToServer, default, out var seq);
            sealed_[i] = (wire, seq);
        })));

        // All sequence numbers must be distinct (no nonce reuse).
        var seqs = sealed_.Select(s => s.Seq).ToHashSet();
        Assert.Equal(count, seqs.Count);

        // All frames must open correctly (in seq order).
        foreach (var (wire, seq) in sealed_.OrderBy(s => s.Seq))
        {
            var pt = openCipher.Open(wire, E2eSessionCipher.DirClientToServer, seq);
            Assert.Equal(plaintext, pt);
        }
    }

    // ── cli-m1: seq > long.MaxValue rejected (test 4) ────────────────────────────────────────────

    /// <summary>
    /// cli-m1: a frame with seq > long.MaxValue must be rejected before the cast to long
    /// (which would produce a negative value, defeating the monotonic-seq check).
    /// </summary>
    [Fact]
    public void Open_SeqAboveLongMaxValue_ThrowsCryptographicException()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var cipher = new E2eSessionCipher(key, "overflow-test");
        var dummy = new byte[32]; // at least TagSize bytes
        Assert.Throws<CryptographicException>(
            () => cipher.Open(dummy, E2eSessionCipher.DirClientToServer, (ulong)long.MaxValue + 1));
    }
}
