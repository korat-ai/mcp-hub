// 031-relay-confidentiality: agent-side E2E session management.
// Per-session: performs the ECDH key offer/answer handshake after SessionOpened,
// exposes Seal/Open wrappers, and zeroes key material on dispose.
using System.Security.Cryptography;
using Google.Protobuf;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cli.Gateway;

/// <summary>
/// Outcome of the agent-side E2E handshake attempt.
/// </summary>
internal enum E2eHandshakeResult
{
    /// <summary>Handshake completed successfully; <see cref="E2eAgentSession.Cipher"/> is ready.</summary>
    Established,
    /// <summary>Publisher sent E2eNotSupported (or timeout); session falls back to plaintext.</summary>
    FellBackToPlaintext,
    /// <summary>Handshake failed due to a crypto or protocol error (confirm tag mismatch, bad curve, etc.).</summary>
    Failed,
}

/// <summary>
/// 031-relay-confidentiality: holds one agent-side E2E session cipher for a relay session.
/// Thread-safety: Seal/Open calls are serialized by the pump loops (one writer, one reader
/// per session) — no external locking needed in normal bridge flow.
/// </summary>
internal sealed class E2eAgentSession : IDisposable
{
    private E2eSessionCipher? _cipher;
    private bool _disposed;

    private E2eAgentSession(E2eSessionCipher cipher)
    {
        _cipher = cipher;
    }

    /// <summary>The per-session AES-256-GCM cipher. Null until the handshake completes.</summary>
    public E2eSessionCipher? Cipher => _disposed ? null : _cipher;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cipher?.Dispose();
        _cipher = null;
    }

    // ── Handshake ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 031: Perform the agent-side E2E handshake for a new relay session.
    ///
    /// <para>Flow:</para>
    /// <list type="number">
    ///   <item>Generate ephemeral P-256 key pair + 16-byte salt.</item>
    ///   <item>Send E2eKeyOffer to the cloud (which forwards to the publisher).</item>
    ///   <item>Wait up to <paramref name="timeout"/> for E2eKeyAnswer or E2eNotSupported
    ///         on <paramref name="incomingMessages"/>. Any non-E2E message is put back via
    ///         <paramref name="unconsumedMessages"/> for the bridge pumps to process normally.</item>
    ///   <item>Verify publisher's confirm tag (constant-time HMAC check).</item>
    ///   <item>Send E2eKeyConfirm with our own tag.</item>
    ///   <item>Return an <see cref="E2eAgentSession"/> wrapping the derived cipher.</item>
    /// </list>
    ///
    /// <para>Anti-downgrade: explicit E2eNotSupported → plaintext warning (stdout-quiet,
    /// stderr-loud). Timeout → same downgrade path. --e2e=require → close session instead
    /// (implemented by the caller checking the returned result).</para>
    ///
    /// <para>Trust model: passive cloud cannot derive K_payload. Active cloud (key-swap MITM)
    /// is a DOCUMENTED RESIDUAL; confirm tags bind the transcript but a key-swapping relay
    /// produces consistent forged tags. See CRYPTO.md §2 for the upgrade path.</para>
    /// </summary>
    public static async Task<(E2eHandshakeResult Result, E2eAgentSession? RelaySession)> EstablishAsync(
        string sessionId,
        string agentClientId,
        string publisherNodeId,
        NodeGatewayConnection connection,
        System.Collections.Generic.Queue<GatewayToNodeMessage> unconsumedMessages,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var handshake = E2eHandshake.CreateEphemeral();
        var agentSpki = handshake.ExportSpki();
        var salt = E2eHandshake.GenerateSalt();

        // Send the offer.
        await connection.SendE2eKeyOfferAsync(
            sessionId, version: 1, curve: "p256",
            pubKey: agentSpki, salt: salt, cancellationToken: ct);

        // Wait for E2eKeyAnswer or E2eNotSupported.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        GatewayToNodeMessage? answerWrapper = null;
        try
        {
            while (true)
            {
                GatewayToNodeMessage msg;
                try
                {
                    if (!await connection.IncomingMessages.WaitToReadAsync(timeoutCts.Token))
                        return (E2eHandshakeResult.Failed, null);
                    connection.IncomingMessages.TryRead(out msg!);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    E2eConsole.FellBackToPlaintext(
                        sessionId, "handshake timed out — the relay could not complete the key exchange");
                    return (E2eHandshakeResult.FellBackToPlaintext, null);
                }

                switch (msg.PayloadCase)
                {
                    case GatewayToNodeMessage.PayloadOneofCase.E2EKeyAnswer
                        when msg.E2EKeyAnswer.SessionId == sessionId:
                        answerWrapper = msg;
                        goto doneWaiting;

                    case GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported
                        when msg.E2ENotSupported.SessionId == sessionId:
                        E2eConsole.FellBackToPlaintext(
                            sessionId, $"publisher does not support E2E encryption ({msg.E2ENotSupported.Reason})");
                        return (E2eHandshakeResult.FellBackToPlaintext, null);

                    default:
                        unconsumedMessages.Enqueue(msg);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (E2eHandshakeResult.Failed, null);
        }

        doneWaiting:
        var answerMsg = answerWrapper!.E2EKeyAnswer;

        if (answerMsg.Curve != "p256")
        {
            E2eConsole.Detail($"unsupported curve '{answerMsg.Curve}', aborting handshake");
            return (E2eHandshakeResult.Failed, null);
        }

        var publisherSpki = answerMsg.PubKey.ToByteArray();

        // MAJOR-3 fix: prefer the cloud-stamped publisher_node_id from the answer so both
        // the direct-connect path and the aggregator path derive the same transcript hash.
        // Fall back to the caller-provided publisherNodeId for old clouds that omit the field.
        var effectivePublisherNodeId = string.IsNullOrEmpty(answerMsg.PublisherNodeId)
            ? publisherNodeId
            : answerMsg.PublisherNodeId;

        byte[] kPayload;
        try
        {
            var transcriptHash = E2eHandshake.BuildTranscriptHash(
                sessionId, agentClientId, effectivePublisherNodeId, salt, agentSpki, publisherSpki);

            using var keys = handshake.Derive(publisherSpki, salt, transcriptHash);

            // Verify publisher confirm tag BEFORE sending our confirm (fail-first).
            E2eHandshake.VerifyConfirm(
                keys.KConf,
                E2eHandshake.PublisherConfirmLabel,
                transcriptHash,
                answerMsg.ConfirmTag.ToByteArray());

            // Compute our confirm tag from KConf.
            var agentTag = E2eHandshake.ComputeConfirm(
                keys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);

            // Extract KPayload before keys is disposed.
            kPayload = (byte[])keys.KPayload.Clone();

            // Send our confirm (closes the handshake from our side).
            await connection.SendE2eKeyConfirmAsync(sessionId, agentTag, ct);
            // keys.Dispose() zeroes KConf + original KPayload here (using block end).
        }
        catch (CryptographicException ex)
        {
            E2eConsole.Detail($"handshake crypto failure: {ex.Message}, aborting");
            return (E2eHandshakeResult.Failed, null);
        }

        // Build cipher from extracted KPayload, then zero it.
        var cipher = new E2eSessionCipher(kPayload, sessionId);
        CryptographicOperations.ZeroMemory(kPayload);

        return (E2eHandshakeResult.Established, new E2eAgentSession(cipher));
    }
}
