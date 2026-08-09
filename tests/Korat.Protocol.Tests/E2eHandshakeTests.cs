// 031-relay-confidentiality: E2eHandshake unit tests.
// Acceptance criteria covered:
//   A1: cloud observes transcript but cannot derive session key (RelayObservedTranscript_CannotDeriveSessionKey)
//   A7: tampered AAD metadata fails Open (in E2eSessionCipherTests — shared key context)
using System.Security.Cryptography;
using Korat.Protocol;

namespace Korat.Protocol.Tests;

public class E2eHandshakeTests
{
    private const string SessionId   = "sess-abc123";
    private const string AgentId     = "agent-001";
    private const string PublisherId = "publisher-007";

    // ── A1 / T7: Two parties derive the same key ──────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_TwoParties_DeriveSameKey()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();

        var th = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        using var agentKeys     = agentEp.Derive(publisherSpki, salt, th);
        using var publisherKeys = publisherEp.Derive(agentSpki, salt, th);

        Assert.Equal(agentKeys.KPayload, publisherKeys.KPayload);
        Assert.Equal(agentKeys.KConf,    publisherKeys.KConf);
    }

    // ── A1: Relay (third party) observing transcript cannot derive session key ──────────────────

    [Fact]
    public void E2eHandshake_RelayObservedTranscript_CannotDeriveSessionKey()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();

        var th = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        // Cloud (relay) only has agentSpki, publisherSpki, salt, transcriptHash.
        // It does NOT have either private key → cannot call DeriveRawSecretAgreement.
        // Instead it tries to make up a different key derivation — the derived key
        // will NOT match the legitimate parties' shared key.
        using var agentKeys = agentEp.Derive(publisherSpki, salt, th);

        // Simulate relay attempting to derive key with a fresh ephemeral it controls.
        using var relayEp   = E2eHandshake.CreateEphemeral();
        var relaySpki       = relayEp.ExportSpki();
        var fakeTh          = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, relaySpki);
        using var relayAttempt = relayEp.Derive(agentSpki, salt, fakeTh);

        // Relay's derived K_payload must differ from the legitimate shared key.
        Assert.NotEqual(agentKeys.KPayload, relayAttempt.KPayload);
    }

    // ── HKDF info is transcript-bound ────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_DifferentTranscript_DifferentKeys()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();

        var th1 = E2eHandshake.BuildTranscriptHash(SessionId, AgentId,     PublisherId, salt, agentSpki, publisherSpki);
        var th2 = E2eHandshake.BuildTranscriptHash("other-sess", AgentId,  PublisherId, salt, agentSpki, publisherSpki);

        using var keys1 = agentEp.Derive(publisherSpki, salt, th1);
        using var keys2 = agentEp.Derive(publisherSpki, salt, th2);

        Assert.NotEqual(keys1.KPayload, keys2.KPayload);
    }

    // ── Salt randomness ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_GenerateSalt_ProducesUniqueSalts()
    {
        const int N = 50;
        var salts = new HashSet<string>(N);
        for (var i = 0; i < N; i++)
            salts.Add(Convert.ToBase64String(E2eHandshake.GenerateSalt()));
        Assert.Equal(N, salts.Count);
    }

    // ── Confirm tags ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_ConfirmTag_RoundTrip()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();
        var th            = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        using var publisherKeys = publisherEp.Derive(agentSpki, salt, th);
        using var agentKeys     = agentEp.Derive(publisherSpki, salt, th);

        // Publisher sends confirm; agent verifies.
        var pubTag = E2eHandshake.ComputeConfirm(publisherKeys.KConf, E2eHandshake.PublisherConfirmLabel, th);
        E2eHandshake.VerifyConfirm(agentKeys.KConf, E2eHandshake.PublisherConfirmLabel, th, pubTag);

        // Agent sends confirm; publisher verifies.
        var agentTag = E2eHandshake.ComputeConfirm(agentKeys.KConf, E2eHandshake.AgentConfirmLabel, th);
        E2eHandshake.VerifyConfirm(publisherKeys.KConf, E2eHandshake.AgentConfirmLabel, th, agentTag);
    }

    [Fact]
    public void E2eHandshake_ConfirmTag_WrongKey_Throws()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();
        var th            = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        using var agentKeys = agentEp.Derive(publisherSpki, salt, th);

        var tag = E2eHandshake.ComputeConfirm(agentKeys.KConf, E2eHandshake.AgentConfirmLabel, th);
        // tamper key
        var badKey = (byte[])agentKeys.KConf.Clone();
        badKey[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            E2eHandshake.VerifyConfirm(badKey, E2eHandshake.AgentConfirmLabel, th, tag));
    }

    [Fact]
    public void E2eHandshake_ConfirmTag_WrongLabel_Throws()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();
        var th            = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        using var agentKeys = agentEp.Derive(publisherSpki, salt, th);

        var tag = E2eHandshake.ComputeConfirm(agentKeys.KConf, E2eHandshake.AgentConfirmLabel, th);

        // Wrong label → different HMAC input → tag mismatch.
        Assert.ThrowsAny<CryptographicException>(() =>
            E2eHandshake.VerifyConfirm(agentKeys.KConf, E2eHandshake.PublisherConfirmLabel, th, tag));
    }

    // ── SPKI round-trip ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_ExportSpki_CanBeImportedByPeer()
    {
        using var ep = E2eHandshake.CreateEphemeral();
        var spki = ep.ExportSpki();
        // Must be importable (would throw if invalid).
        using var peer = System.Security.Cryptography.ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(spki, out var read);
        Assert.Equal(spki.Length, read);
    }

    // ── Dispose safety ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_DisposedInstance_ThrowsOnUse()
    {
        var ep = E2eHandshake.CreateEphemeral();
        ep.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ep.ExportSpki());
    }

    // ── DerivedKeys zero on dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void E2eHandshake_DerivedKeys_ZeroedOnDispose()
    {
        using var agentEp     = E2eHandshake.CreateEphemeral();
        using var publisherEp = E2eHandshake.CreateEphemeral();

        var agentSpki     = agentEp.ExportSpki();
        var publisherSpki = publisherEp.ExportSpki();
        var salt          = E2eHandshake.GenerateSalt();
        var th            = E2eHandshake.BuildTranscriptHash(SessionId, AgentId, PublisherId, salt, agentSpki, publisherSpki);

        var keys = agentEp.Derive(publisherSpki, salt, th);
        var kp = keys.KPayload;
        var kc = keys.KConf;
        keys.Dispose();

        // After dispose the arrays should be all-zero.
        Assert.All(kp, b => Assert.Equal(0, b));
        Assert.All(kc, b => Assert.Equal(0, b));
    }
}
