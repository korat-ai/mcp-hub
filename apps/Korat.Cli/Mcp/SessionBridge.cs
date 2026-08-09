using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cli.Mcp;

/// <summary>
/// Specifies which MCP server to spawn when a new session opens on the publisher side.
/// </summary>
internal sealed record McpServerSpec(string DisplayName, string LaunchCommand, string LaunchArguments);

/// <summary>
/// Lazily spawns one <see cref="McpServerProcess"/> per relay session and wires
/// frame↔stdio in both directions.
///
/// Multi-server routing: the cloud stamps the first inbound frame for each session
/// with <c>RelayFrame.mcp_server_id</c>. The bridge uses this to pick the matching
/// <see cref="McpServerSpec"/> from the routing map, then caches the resolved spec
/// for the session so subsequent frames (which may have an empty <c>mcp_server_id</c>)
/// are dispatched correctly.
///
/// The routing map is updated live (add/remove) via <see cref="UpdateRoutingMap"/>
/// without tearing down sessions for unchanged servers.
///
/// 031: Publisher-side E2E. When the cloud forwards an E2eKeyOffer for a session,
/// <see cref="HandleE2eKeyOfferAsync"/> performs the publisher-side ECDH handshake:
/// sends E2eKeyAnswer, waits for E2eKeyConfirm (arrives as a GatewayToNodeMessage),
/// then installs a per-session cipher. Outgoing frames are encrypted; incoming
/// E2E frames are decrypted before forwarding to the subprocess stdin.
///
/// Direction notes:
///   - <see cref="OnFrameReceivedAsync"/> is called for every inbound Frame on the
///     gateway stream. On first frame for a given session_id, the bridge resolves
///     the target spec, spawns the subprocess, and starts a stdout→frame pump.
///   - Outbound frames carry server-to-client direction and an incrementing sequence
///     per session.
/// </summary>
internal sealed class SessionBridge : IAsyncDisposable
{
    private readonly ISessionBridgeGateway _gateway;

    // Routing map: mcp_server_id → spec. Replaced atomically on reconcile.
    private volatile IReadOnlyDictionary<string, McpServerSpec> _routingMap;

    // RelaySession cache: session_id → resolved spec (set on first frame, used for subsequent
    // frames that may arrive with empty mcp_server_id).
    private readonly ConcurrentDictionary<string, McpServerSpec> _sessionSpecs = new();

    // ARCH-HIGH-4: hold Lazy<SessionContext> so the factory function is invoked at most
    // once per session, even when multiple frames for the same session_id arrive
    // concurrently and race on GetOrAdd.
    private readonly ConcurrentDictionary<string, Lazy<SessionContext>> _sessions = new();

    // 031: per-session E2E ciphers on the publisher side.
    // Installed after the three-way handshake completes.
    private readonly ConcurrentDictionary<string, E2eSessionCipher> _e2eCiphers = new();

    // 031: pending E2eKeyConfirm data: session_id → record holding the pre-computed cipher
    // and expected agent tag so HandleE2eKeyConfirm (which runs INLINE on the dispatch loop)
    // can install the cipher synchronously before the next frame is dispatched.
    // MAJOR-1 fix: cipher installation must happen on the dispatch loop to close the
    // confirm-to-cipher-install race window (detached Task.Run was too late).
    private sealed record E2ePendingConfirm(
        E2eSessionCipher Cipher,
        byte[] ExpectedAgentTag,
        TaskCompletionSource ConfirmSignal);

    private readonly ConcurrentDictionary<string, E2ePendingConfirm> _e2eConfirmPending = new();

    private volatile bool _disposed;

    public SessionBridge(ISessionBridgeGateway gateway, IReadOnlyDictionary<string, McpServerSpec> routingMap)
    {
        _gateway = gateway;
        _routingMap = routingMap;
    }

    /// <summary>
    /// Backward-compat constructor for single-server use (e.g. <c>korat up --serve</c>).
    /// </summary>
    public SessionBridge(ISessionBridgeGateway gateway, McpServerSpec spec)
        : this(gateway, new Dictionary<string, McpServerSpec> { [spec.DisplayName] = spec })
    {
    }

    /// <summary>Total number of live subprocess-backed sessions.</summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// Atomically replaces the routing map with a new snapshot. Existing sessions
    /// (already resolved to a spec) are not affected — their cached spec remains valid.
    /// </summary>
    public void UpdateRoutingMap(IReadOnlyDictionary<string, McpServerSpec> newMap)
    {
        _routingMap = newMap;
    }

    // ── 031: E2E publisher-side handshake ────────────────────────────────────────────────────────

    /// <summary>
    /// 031: Called (via a detached Task.Run) when the cloud forwards an E2eKeyOffer for a
    /// session that this publisher owns. Performs the publisher-side ECDH key derivation and
    /// stashes the pre-computed cipher + expected agent tag into <c>_e2eConfirmPending</c>.
    /// The cipher is NOT installed here — installation happens synchronously in
    /// <see cref="HandleE2eKeyConfirm"/> which runs inline on the dispatch loop.
    ///
    /// <para>MAJOR-1 fix: the prior design installed the cipher inside this detached task
    /// (after awaiting the confirm TCS). A frame dispatched immediately after the confirm
    /// arrived on the loop could race the pool continuation and see <c>cipher==null</c> →
    /// fail-closed → session killed. By splitting responsibilities — derivation here, install
    /// there — the install is guaranteed to happen before any subsequent frame is dispatched.</para>
    ///
    /// <para>Trust model: passive cloud cannot derive K_payload. Active cloud (key-swap MITM)
    /// is a DOCUMENTED RESIDUAL; confirm tags bind the transcript but a key-swapping relay
    /// produces consistent forged tags. See CRYPTO.md §2.</para>
    ///
    /// <para>Threading: called from a detached Task.Run (NOT from the dispatch loop directly).
    /// The dispatch loop fires this off and immediately returns to consuming IncomingMessages,
    /// so E2eKeyConfirm can arrive on the same channel and be delivered inline without
    /// deadlock. <c>_e2eConfirmPending</c> reads/writes are safe via ConcurrentDictionary;
    /// only one offer per session in normal operation so the write path has no contention.</para>
    /// </summary>
    public async Task HandleE2eKeyOfferAsync(
        E2eKeyOffer offer,
        string? agentClientId,
        string publisherNodeId,
        CancellationToken ct)
    {
        if (_disposed) return;
        var sessionId = offer.SessionId;

        if (offer.Curve != "p256")
        {
            Console.Error.WriteLine(
                $"[e2e] Unsupported curve '{offer.Curve}' in offer for session {sessionId}.");
            return;
        }

        if (_e2eConfirmPending.ContainsKey(sessionId) || _e2eCiphers.ContainsKey(sessionId))
        {
            Console.Error.WriteLine(
                $"[e2e] Duplicate E2eKeyOffer for session {sessionId} — ignoring.");
            return;
        }

        var agentSpki = offer.PubKey.ToByteArray();
        var salt      = offer.Salt.ToByteArray();

        byte[] kPayloadFinal;
        byte[] publisherTagFinal;
        byte[] expectedAgentTag;
        byte[] publisherSpki;

        using var handshake = E2eHandshake.CreateEphemeral();
        publisherSpki = handshake.ExportSpki();

        var transcriptHash = E2eHandshake.BuildTranscriptHash(
            sessionId, agentClientId ?? string.Empty, publisherNodeId,
            salt, agentSpki, publisherSpki);

        try
        {
            // Compute both confirm tags AND extract KPayload inside a single using block
            // so K_conf is still valid for both confirm computations.
            using var keys = handshake.Derive(agentSpki, salt, transcriptHash);
            publisherTagFinal = E2eHandshake.ComputeConfirm(
                keys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash);
            expectedAgentTag = E2eHandshake.ComputeConfirm(
                keys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);
            kPayloadFinal = (byte[])keys.KPayload.Clone();
            // keys.Dispose() zeroes KConf + original KPayload here.
        }
        catch (CryptographicException ex)
        {
            Console.Error.WriteLine(
                $"[e2e] ECDH derivation failed for session {sessionId}: {ex.Message}");
            return;
        }

        // Build the cipher NOW (while kPayload is in scope) so the dispatch-loop
        // HandleE2eKeyConfirm can install it without touching raw key material.
        var cipher = new E2eSessionCipher(kPayloadFinal, sessionId);
        CryptographicOperations.ZeroMemory(kPayloadFinal);

        // ConfirmSignal: HandleE2eKeyConfirm (inline on the dispatch loop) signals this
        // when it has verified the agent tag and installed (or rejected) the cipher.
        // We wait here only to honour the timeout and log the outcome; the critical
        // cipher installation has already happened synchronously on the loop by then.
        var confirmSignal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = new E2ePendingConfirm(cipher, expectedAgentTag, confirmSignal);
        if (!_e2eConfirmPending.TryAdd(sessionId, pending))
        {
            Console.Error.WriteLine(
                $"[e2e] Race on E2eKeyOffer registration for session {sessionId}.");
            cipher.Dispose();
            return;
        }

        // Send the answer.
        try
        {
            await _gateway.SendE2eKeyAnswerAsync(
                sessionId,
                version: 1,
                curve: "p256",
                pubKey: publisherSpki,
                confirmTag: publisherTagFinal,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _e2eConfirmPending.TryRemove(sessionId, out _);
            cipher.Dispose();
            Console.Error.WriteLine(
                $"[e2e] Failed to send E2eKeyAnswer for session {sessionId}: {ex.Message}");
            return;
        }

        // Wait for HandleE2eKeyConfirm to signal completion (up to 10 seconds).
        // The signal fires after the cipher has already been installed (or rejected)
        // synchronously on the dispatch loop — we are just waiting for the outcome log.
        using var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        confirmCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await confirmSignal.Task.WaitAsync(confirmCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout: remove the pending entry and dispose the pre-built cipher
            // (it was never installed — HandleE2eKeyConfirm never fired or was too slow).
            if (_e2eConfirmPending.TryRemove(sessionId, out var timedOutPending))
                timedOutPending.Cipher.Dispose();
            Console.Error.WriteLine(
                $"[e2e] Timed out waiting for E2eKeyConfirm for session {sessionId} — falling back to plaintext.");
        }
    }

    /// <summary>
    /// 031: Called INLINE on the dispatch loop when the cloud delivers an E2eKeyConfirm
    /// from the agent. Verifies the agent confirm tag (constant-time) and, on success,
    /// installs the pre-built cipher synchronously before returning.
    ///
    /// <para>MAJOR-1 fix: installation happens HERE (on the dispatch loop), not in the
    /// detached <see cref="HandleE2eKeyOfferAsync"/> continuation. Any frame dispatched
    /// immediately after this call sees the installed cipher.</para>
    ///
    /// <para>MINOR-1 fix: uses <c>TrySetResult</c> to avoid an exception if a second
    /// completer races (e.g. timeout + late confirm).</para>
    /// </summary>
    public void HandleE2eKeyConfirm(E2eKeyConfirm confirm)
    {
        var sessionId = confirm.SessionId;
        if (!_e2eConfirmPending.TryRemove(sessionId, out var pending))
        {
            Console.Error.WriteLine(
                $"[e2e] Unexpected E2eKeyConfirm for session {sessionId} — no pending handshake.");
            return;
        }

        var agentTag = confirm.ConfirmTag.ToByteArray();

        // Verify agent confirm tag (constant-time) synchronously on the dispatch loop.
        if (!CryptographicOperations.FixedTimeEquals(agentTag, pending.ExpectedAgentTag))
        {
            pending.Cipher.Dispose();
            Console.Error.WriteLine(
                $"[e2e] Agent confirm tag FAILED for session {sessionId} — discarding.");
            pending.ConfirmSignal.TrySetResult();
            return;
        }

        // Install the cipher synchronously — the next frame on this loop will see it.
        _e2eCiphers[sessionId] = pending.Cipher;
        Korat.Cli.Gateway.E2eConsole.Detail($"session {sessionId} is E2E-encrypted (publisher side)");

        // Signal the detached HandleE2eKeyOfferAsync that we're done (for timeout accounting).
        pending.ConfirmSignal.TrySetResult();
    }

    // ── Frame dispatch ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes one inbound frame to the matching subprocess's stdin. Lazily spawns
    /// the subprocess on first frame for a previously-unseen session_id.
    ///
    /// On the FIRST frame for a session, <paramref name="mcpServerId"/> must be
    /// non-empty (stamped by the cloud) so the bridge can resolve the target spec.
    /// Subsequent frames for the same session may have an empty <paramref name="mcpServerId"/>;
    /// the cached spec is used instead.
    ///
    /// If the mcp_server_id is unknown (not in the routing map), the session is logged
    /// and dropped gracefully.
    ///
    /// 031: if the frame has enc==1 and a cipher is installed for this session, decrypt
    /// before forwarding to stdin.
    /// </summary>
    public async Task OnFrameReceivedAsync(
        string sessionId,
        string mcpServerId,
        ReadOnlyMemory<byte> bytes,
        uint enc,
        FrameMetadata? meta,
        ulong sequenceNumber,
        CancellationToken cancellationToken)
    {
        if (_disposed) return;

        // Resolve spec: prefer cache (subsequent frames), fall back to routing map (first frame).
        if (!_sessionSpecs.TryGetValue(sessionId, out var spec))
        {
            if (string.IsNullOrEmpty(mcpServerId) || !_routingMap.TryGetValue(mcpServerId, out spec))
            {
                Console.Error.WriteLine(
                    $"[bridge] session={sessionId} unknown mcp_server_id='{mcpServerId}' — dropping session.");
                return;
            }
            // Cache the resolved spec for this session so later frames route without
            // a routing-map lookup even if mcp_server_id is empty.
            _sessionSpecs[sessionId] = spec;
        }

        var capturedSpec = spec;

        // cli-m4: validate enc/cipher BEFORE materializing the subprocess.
        // A forged/mismatched frame must NOT spawn a subprocess before being rejected.
        // BLOCKING-2 fix: fail-closed on enc/cipher mismatch — never forward raw bytes when
        // the enc indicator and installed cipher disagree (injection/downgrade prevention).
        ReadOnlyMemory<byte> plaintext;
        _e2eCiphers.TryGetValue(sessionId, out var cipher);

        if (enc == 1 && cipher is not null)
        {
            // Expected path: E2E established, frame is encrypted.
            try
            {
                var metaBytes = meta?.ToByteArray() ?? Array.Empty<byte>();
                var decrypted = cipher.Open(
                    bytes.Span,
                    E2eSessionCipher.DirClientToServer,
                    sequenceNumber,
                    metaBytes);
                plaintext = decrypted;
            }
            catch (CryptographicException ex)
            {
                Console.Error.WriteLine(
                    $"[e2e] AEAD decryption failed session={sessionId}: {ex.Message} — closing session.");
                await CloseSessionAsync(sessionId);
                return;
            }
        }
        else if (enc == 0 && cipher is null)
        {
            // Plaintext path — no E2E negotiated (legacy or --e2e=off).
            plaintext = bytes;
        }
        else
        {
            // Any mismatch: enc==1 but no cipher installed (injection attempt on non-E2E session),
            // enc==0 but cipher installed (downgrade/injection on established E2E session),
            // or unexpected enc value. Fail closed without spawning any subprocess.
            Console.Error.WriteLine(
                $"[e2e] enc/cipher mismatch session={sessionId} enc={enc} cipher={cipher is not null} — closing session (fail-closed).");
            await CloseSessionAsync(sessionId);
            return;
        }

        // Now that the frame is validated (and decrypted if needed), materialize the session.
        // Lazy<T> with ExecutionAndPublication makes the factory idempotent under
        // concurrent calls — at most one McpServerProcess is spawned per session_id
        // even if multiple frames arrive in parallel for a previously-unseen session.
        var lazy = _sessions.GetOrAdd(
            sessionId,
            id => new Lazy<SessionContext>(() => CreateSession(id, capturedSpec), LazyThreadSafetyMode.ExecutionAndPublication));
        var ctx = lazy.Value;

        try
        {
            await ctx.Process.WriteStdinAsync(plaintext, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[bridge] session={sessionId} stdin write failed errorType={ex.GetType().Name}");
            await CloseSessionAsync(sessionId);
        }
    }

    /// <summary>
    /// Backward-compat overload: no E2E metadata (plaintext frames).
    /// </summary>
    public async Task OnFrameReceivedAsync(
        string sessionId,
        string mcpServerId,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await OnFrameReceivedAsync(sessionId, mcpServerId, bytes, 0, null, 0, cancellationToken);
    }

    /// <summary>
    /// Backward-compat overload for single-server callers that don't supply mcp_server_id.
    /// Uses the first (and only) entry in the routing map.
    /// </summary>
    public Task OnFrameReceivedAsync(
        string sessionId,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        // For single-server usage the routing map has exactly one entry; use its key.
        var firstKey = _routingMap.Keys.FirstOrDefault() ?? string.Empty;
        return OnFrameReceivedAsync(sessionId, firstKey, bytes, 0, null, 0, cancellationToken);
    }

    private SessionContext CreateSession(string sessionId, McpServerSpec spec)
    {
        Console.WriteLine(
            $"[bridge] spawning MCP server '{spec.DisplayName}' for session {sessionId}");

        var process = new McpServerProcess(spec.LaunchCommand, spec.LaunchArguments);
        var ctx = new SessionContext(sessionId, process);
        ctx.PumpTask = Task.Run(() => StdoutToFramePumpAsync(ctx));
        return ctx;
    }

    private async Task StdoutToFramePumpAsync(SessionContext ctx)
    {
        try
        {
            await foreach (var chunk in ctx.Process.StdoutChunks.ReadAllAsync())
            {
                // 031: encrypt if E2E is established for this session.
                // Use the out-seqUsed overload so the wire SequenceNumber matches
                // the cipher's internal counter (starting at 0) rather than the
                // independent ctx.NextSequence (which starts at 1 after the first
                // increment and would cause a nonce mismatch on the receiver).
                if (_e2eCiphers.TryGetValue(ctx.SessionId, out var cipher))
                {
                    var meta = FrameMetadataFactory.FromPlaintext(
                        chunk.AsSpan(), E2eSessionCipher.DirectionServerToClient, (ulong)chunk.Length);
                    var metaBytes = meta.ToByteArray();
                    var wire = cipher.Seal(
                        chunk.AsSpan(),
                        E2eSessionCipher.DirServerToClient,
                        metaBytes,
                        out var seqUsed);
                    await _gateway.SendE2eFrameAsync(
                        sessionId: ctx.SessionId,
                        wirePayload: wire,
                        sequenceNumber: seqUsed,
                        direction: E2eSessionCipher.DirectionServerToClient,
                        meta: meta,
                        cancellationToken: CancellationToken.None);
                }
                else
                {
                    ctx.NextSequence++;
                    await _gateway.SendFrameAsync(
                        sessionId: ctx.SessionId,
                        ciphertext: chunk,
                        sequenceNumber: ctx.NextSequence,
                        direction: "server_to_client",
                        cancellationToken: CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[bridge] session={ctx.SessionId} stdout pump terminated errorType={ex.GetType().Name}");
        }
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        _sessionSpecs.TryRemove(sessionId, out _);
        // MAJOR-1: pending record holds a pre-built cipher; cancel the signal so the
        // detached HandleE2eKeyOfferAsync unblocks, and dispose the unused cipher.
        if (_e2eConfirmPending.TryRemove(sessionId, out var pending))
        {
            pending.ConfirmSignal.TrySetCanceled();
            pending.Cipher.Dispose();
        }
        if (_e2eCiphers.TryRemove(sessionId, out var cipher))
            cipher.Dispose();
        // cli-m8: notify the cloud that we are closing this session so it can tear
        // down the cloud-side routing entry instead of waiting for the reaper.
        // CLI-MINOR-1: TryRemove guard first — only send the cloud notification when
        // this call actually owned and removed the session entry.  A redundant
        // SendCloseSessionAsync on an already-removed (or never-existed) session
        // wastes a round-trip and can confuse the cloud reaper.
        if (!_sessions.TryRemove(sessionId, out var lazy))
            return;
        try { await _gateway.SendCloseSessionAsync(sessionId, "publisher-initiated", CancellationToken.None); }
        catch { /* best-effort — don't let send failure block local teardown */ }
        // If the Lazy was never materialized (rare — TryRemove won the race against the
        // first frame), there is nothing to dispose. Otherwise dispose the subprocess.
        if (lazy.IsValueCreated)
            await lazy.Value.Process.DisposeAsync();
    }

    public async Task ShutdownAllAsync()
    {
        foreach (var key in _sessions.Keys.ToArray())
            await CloseSessionAsync(key);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ShutdownAllAsync();
    }

    private sealed class SessionContext(string sessionId, McpServerProcess process)
    {
        public string SessionId { get; } = sessionId;
        public McpServerProcess Process { get; } = process;
        public ulong NextSequence;
        public Task? PumpTask;
    }
}
