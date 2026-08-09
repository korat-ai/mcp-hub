using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Korat.Cli.Commands;
using Korat.Protocol;
using Korat.Relay.V1;
using Korat.Mcp;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>
/// Owns N relay sessions to granted backend MCP servers over ONE agent gRPC
/// connection. A single background drain loop reads <see cref="IGatewayConnection.IncomingMessages"/>
/// and demultiplexes every inbound message by request-id (session-open correlation)
/// or session-id (frames / closes). Callers never touch the channel directly; they
/// await <see cref="TaskCompletionSource{T}"/>s the drain loop completes.
/// </summary>
internal sealed class BackendSessionManager : IBackendSessions, IAsyncDisposable
{
    private enum OutcomeKind { Opened, Pending, Denied }

    // Deferred-fix (latency): PeerSupportsE2e carries the cloud's ADVISORY
    // SessionOpened.peer_supports_e2e with presence — null when the (old) cloud did not stamp
    // the field, otherwise the explicit value. Only an explicit false skips the key offer.
    private sealed record OpenOutcome(OutcomeKind Kind, string Value, bool? PeerSupportsE2e = null);

    private readonly IGatewayConnection _conn;
    private readonly string _agentClientId;

    // Timeout overrides — production uses BackendSession.*Timeout constants; tests inject small values.
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _toolCallTimeout;

    // MAJOR-5: E2E policy forwarded from --e2e flag. Defaults to Prefer.
    private readonly ConnectCommand.E2ePolicy _e2ePolicy;

    // Sessions that have completed the handshake and are available for tools/call routing.
    private readonly ConcurrentDictionary<string, BackendSession> _sessionsById = new();
    private readonly ConcurrentDictionary<string, BackendSession> _sessionsBySlug = new();

    // Sessions mid-handshake: registered so the drain loop can route inbound frames to them,
    // but NOT yet in _sessionsById/_sessionsBySlug so tools/call cannot route to them.
    private readonly ConcurrentDictionary<string, BackendSession> _handshakingSessions = new();

    /// <summary>Number of live sessions tracked by this manager. Exposed for testing.</summary>
    internal int SessionCount => _sessionsById.Count;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OpenOutcome>> _openWaiters = new();

    // 031 (MAJOR-3): E2E key exchange waiters — per-session TCS routing E2eKeyAnswer/E2eNotSupported
    // from the drain loop to the handshake waiting in OpenAsync.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GatewayToNodeMessage>> _e2eWaiters = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;
    private bool _disposed;

    /// <summary>
    /// Initialises the manager. <paramref name="handshakeTimeout"/> and
    /// <paramref name="toolCallTimeout"/> default to the production constants on
    /// <see cref="BackendSession"/> when not supplied; tests pass small values so they
    /// don't wait seconds for timeout assertions.
    /// </summary>
    public BackendSessionManager(
        IGatewayConnection conn,
        string agentClientId,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? toolCallTimeout = null,
        ConnectCommand.E2ePolicy e2ePolicy = ConnectCommand.E2ePolicy.Prefer)
    {
        _conn = conn;
        _agentClientId = agentClientId;
        _handshakeTimeout = handshakeTimeout ?? BackendSession.HandshakeTimeout;
        _toolCallTimeout = toolCallTimeout ?? BackendSession.ToolCallTimeout;
        _e2ePolicy = e2ePolicy;
        _drainTask = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    /// <summary>Raised when a backend session is closed by the cloud. Argument is the serverId.</summary>
    public event Action<string>? SessionClosed;

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _conn.IncomingMessages.WaitToReadAsync(ct))
            {
                while (_conn.IncomingMessages.TryRead(out var msg))
                {
                    try { Dispatch(msg); }
                    catch (OperationCanceledException) { throw; } // let the loop's CTS unwind
                    catch (Exception) { /* swallow: a single malformed message or misbehaving handler must not kill the demux loop */ }
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception) { /* channel faulted / closed */ }

        // On any termination, unblock everyone waiting.
        FaultAllOnShutdown();
    }

    private void Dispatch(GatewayToNodeMessage msg)
    {
        switch (msg.PayloadCase)
        {
            case GatewayToNodeMessage.PayloadOneofCase.SessionOpened:
                if (_openWaiters.TryGetValue(msg.SessionOpened.RequestId, out var ow))
                    ow.TrySetResult(new OpenOutcome(
                        OutcomeKind.Opened,
                        msg.SessionOpened.SessionId,
                        ConnectCommand.GetPeerSupportsE2eAdvisory(msg.SessionOpened)));
                break;

            case GatewayToNodeMessage.PayloadOneofCase.AccessPending:
                if (_openWaiters.TryGetValue(msg.AccessPending.RequestId, out var ap))
                    ap.TrySetResult(new OpenOutcome(OutcomeKind.Pending, msg.AccessPending.AccessRequestId));
                break;

            case GatewayToNodeMessage.PayloadOneofCase.AccessDenied:
                if (_openWaiters.TryGetValue(msg.AccessDenied.RequestId, out var ad))
                    ad.TrySetResult(new OpenOutcome(OutcomeKind.Denied, msg.AccessDenied.Reason));
                break;

            case GatewayToNodeMessage.PayloadOneofCase.Frame:
                // Check established sessions first; fall back to sessions still in the handshake.
                if (_sessionsById.TryGetValue(msg.Frame.SessionId, out var fs) ||
                    _handshakingSessions.TryGetValue(msg.Frame.SessionId, out fs))
                    // 031 (MAJOR-3): pass enc/meta/seq so BackendSession can decrypt E2E frames.
                    fs.OnInboundBytes(
                        msg.Frame.Ciphertext.ToByteArray(),
                        msg.Frame.Enc,
                        msg.Frame.Meta,
                        msg.Frame.SequenceNumber);
                break;

            case GatewayToNodeMessage.PayloadOneofCase.CloseSession:
                var sid = msg.CloseSession.SessionId;
                if (_sessionsById.TryRemove(sid, out var cs))
                {
                    _sessionsBySlug.TryRemove(cs.Slug, out _);
                    cs.OnClosed(msg.CloseSession.Reason);
                    SessionClosed?.Invoke(cs.ServerId);
                }
                break;

            case GatewayToNodeMessage.PayloadOneofCase.E2EKeyAnswer:
                // 031 (MAJOR-3): route E2eKeyAnswer to the awaiting handshake.
                if (_e2eWaiters.TryGetValue(msg.E2EKeyAnswer.SessionId, out var ea))
                    ea.TrySetResult(msg);
                break;

            case GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported:
                // 031 (MAJOR-3): route E2eNotSupported to the awaiting handshake.
                if (_e2eWaiters.TryGetValue(msg.E2ENotSupported.SessionId, out var ens))
                    ens.TrySetResult(msg);
                break;

            default:
                // HeartbeatAck / Hello / PublishMcpServerAck / unknown — not ours.
                break;
        }
    }

    private void FaultAllOnShutdown()
    {
        foreach (var kv in _openWaiters)
        {
            if (_openWaiters.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException("gateway connection closed"));
        }
        foreach (var kv in _handshakingSessions)
        {
            if (_handshakingSessions.TryRemove(kv.Key, out var s))
                s.OnClosed("gateway connection closed");
        }
        foreach (var kv in _sessionsById)
        {
            if (_sessionsById.TryRemove(kv.Key, out var s))
            {
                _sessionsBySlug.TryRemove(s.Slug, out _);
                s.OnClosed("gateway connection closed");
            }
        }
    }

    /// <summary>
    /// 031 (MAJOR-3): performs the agent-side E2E handshake for a backend session via the
    /// drain loop's routing of E2eKeyAnswer/E2eNotSupported messages.
    ///
    /// Returns a cipher on success, or null if the publisher declined (E2eNotSupported) or
    /// the handshake timed out. A failed handshake → plaintext session with a loud warning.
    /// </summary>
    private async Task<E2eSessionCipher?> TryEstablishE2eAsync(
        string sessionId,
        string publisherNodeId,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<GatewayToNodeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _e2eWaiters[sessionId] = tcs;
        try
        {
            using var handshake = E2eHandshake.CreateEphemeral();
            var agentSpki = handshake.ExportSpki();
            var salt = E2eHandshake.GenerateSalt();

            await _conn.SendE2eKeyOfferAsync(sessionId, version: 1, curve: "p256", pubKey: agentSpki, salt: salt, ct);

            GatewayToNodeMessage answerMsg;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_handshakeTimeout);
            try
            {
                answerMsg = await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout (not outer cancel) → plaintext fallback.
                Korat.Cli.Gateway.E2eConsole.FellBackToPlaintext(
                    sessionId, "handshake timed out — the relay could not complete the key exchange");
                return null;
            }

            if (answerMsg.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported)
            {
                Korat.Cli.Gateway.E2eConsole.FellBackToPlaintext(
                    sessionId, $"publisher does not support E2E encryption ({answerMsg.E2ENotSupported.Reason})");
                return null;
            }

            var answer = answerMsg.E2EKeyAnswer;
            if (answer.Curve != "p256")
            {
                Korat.Cli.Gateway.E2eConsole.Detail($"unsupported curve '{answer.Curve}' in aggregator session {sessionId}");
                return null;
            }

            var publisherSpki = answer.PubKey.ToByteArray();
            // MAJOR-3 fix: prefer the cloud-stamped publisher_node_id from the answer so
            // the aggregator computes the same transcript hash as the direct-connect path.
            // Fall back to the caller-provided publisherNodeId for old clouds.
            var effectivePublisherNodeId = string.IsNullOrEmpty(answer.PublisherNodeId)
                ? publisherNodeId
                : answer.PublisherNodeId;
            var transcriptHash = E2eHandshake.BuildTranscriptHash(
                sessionId, _agentClientId, effectivePublisherNodeId, salt, agentSpki, publisherSpki);

            byte[] kPayload;
            using (var keys = handshake.Derive(publisherSpki, salt, transcriptHash))
            {
                E2eHandshake.VerifyConfirm(keys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash, answer.ConfirmTag.ToByteArray());
                var agentTag = E2eHandshake.ComputeConfirm(keys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);
                kPayload = (byte[])keys.KPayload.Clone();
                await _conn.SendE2eKeyConfirmAsync(sessionId, agentTag, ct);
            }

            var cipher = new E2eSessionCipher(kPayload, sessionId);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(kPayload);
            return cipher;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Korat.Cli.Gateway.E2eConsole.HandshakeFailedClosing(sessionId, $"aggregator handshake crypto failure: {ex.Message}");
            throw new E2eHandshakeTamperingException(
                $"E2E confirm-tag mismatch for session {sessionId} — active tampering detected.", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _e2eWaiters.TryRemove(sessionId, out _);
        }
    }

    /// <summary>
    /// Issues a RequestSession and awaits the cloud's outcome (SessionOpened / AccessPending / AccessDenied),
    /// correlated by a fresh request id. Registers the waiter before sending and removes it in a finally.
    /// Uses the handshake timeout so a non-responding gateway does not block bring-up indefinitely.
    /// </summary>
    private async Task<OpenOutcome> RequestSessionAndAwaitAsync(string serverId, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<OpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _openWaiters[requestId] = tcs;

        try
        {
            await _conn.SendRequestSessionAsync(requestId, _agentClientId, serverId, ct);
            return await tcs.Task.WaitAsync(_handshakeTimeout, ct);
        }
        finally
        {
            _openWaiters.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Opens a session to <paramref name="server"/> (a granted server): issues a RequestSession,
    /// awaits SessionOpened, runs the MCP initialize handshake, fetches tools/list, and returns
    /// the namespaced tools.
    /// </summary>
    public async Task<IReadOnlyList<ToolInfo>> OpenAsync(ServerDescriptor server, string slug, CancellationToken ct)
    {
        var outcome = await RequestSessionAndAwaitAsync(server.Id, ct);

        switch (outcome.Kind)
        {
            case OutcomeKind.Denied:
                // A granted server being denied is unexpected — surface it.
                throw new InvalidOperationException($"access denied for {server.DisplayName}: {outcome.Value}");
            case OutcomeKind.Pending:
                // Granted servers shouldn't be pending; treat as not-yet-available.
                return Array.Empty<ToolInfo>();
            default:
                var session = new BackendSession(_conn, server.Id, slug, outcome.Value);
                // Register in handshaking table so the drain loop can route inbound frames
                // to this session during initialize/tools/list, without exposing it to
                // tools/call routing (which uses _sessionsById/_sessionsBySlug).
                _handshakingSessions[session.SessionId] = session;
                try
                {
                    // 031 (MAJOR-3): attempt E2E handshake before MCP initialize.
                    // The drain loop routes E2eKeyAnswer/E2eNotSupported to the TCS registered
                    // by TryEstablishE2eAsync. A null return means plaintext (warning printed).
                    // PublisherNodeId is not stored in ServerDescriptor (it's resolved at connect
                    // time in the direct-agent path); we pass empty string here — acceptable
                    // because the transcript hash still binds session_id and the public keys.
                    //
                    // Deferred-fix (latency): when the cloud EXPLICITLY advised the publisher
                    // cannot do E2E (peer_supports_e2e present AND false), skip the offer —
                    // it can only end in E2eNotSupported or a handshake-timeout wait. Old clouds
                    // omit the field (advisory == null) and keep the normal offer path. Under
                    // --e2e=require the existing null-cipher branch below fails closed, now fast.
                    E2eSessionCipher? cipher;
                    if (ConnectCommand.ShouldSkipE2eOffer(outcome.PeerSupportsE2e))
                    {
                        Korat.Cli.Gateway.E2eConsole.FellBackToPlaintext(
                            session.SessionId,
                            $"cloud advises the publisher does not support E2E encryption ({server.DisplayName})");
                        cipher = null;
                    }
                    else
                    {
                        cipher = await TryEstablishE2eAsync(session.SessionId, string.Empty, ct);
                    }
                    if (cipher is not null)
                    {
                        session.InstallCipher(cipher);
                        Korat.Cli.Gateway.E2eConsole.Detail($"aggregator session {session.SessionId} is E2E-encrypted");
                    }
                    else if (_e2ePolicy == ConnectCommand.E2ePolicy.Require)
                    {
                        // MAJOR-5 fix: --e2e=require must fail-closed in space/aggregator mode too.
                        Korat.Cli.Gateway.E2eConsole.RequireFailedForServer(session.SessionId, server.DisplayName);
                        throw new InvalidOperationException(
                            $"E2E required but handshake failed for {server.DisplayName}");
                    }

                    await session.InitializeAsync(ct, _handshakeTimeout);
                    var tools = await session.ListToolsAsync(ct, _handshakeTimeout);
                    // MINOR-2 fix: insert into live routing tables BEFORE removing from
                    // _handshakingSessions to close the gap where a frame arriving between
                    // the two operations would be dropped (not found in either dictionary).
                    _sessionsById[session.SessionId] = session;
                    _sessionsBySlug[slug] = session;
                    _handshakingSessions.TryRemove(session.SessionId, out _);
                    return tools;
                }
                catch
                {
                    // Remove from handshaking table and mark dead so pending requests fault.
                    _handshakingSessions.TryRemove(session.SessionId, out _);
                    session.OnClosed("handshake failed");
                    // cli-m8: notify cloud to close the session (best-effort).
                    try { await _conn.SendCloseSessionAsync(session.SessionId, "handshake-failed", ct); }
                    catch { /* best-effort */ }
                    throw;
                }
        }
    }

    /// <summary>
    /// Triggers an access request for an ungranted server: issues a RequestSession with no active
    /// grant, so the cloud creates an access request and replies AccessPending. Does not open a
    /// session or fetch tools — the SpaceWatcher reopens the server properly once it's granted.
    /// </summary>
    public async Task<AccessRequestResult> RequestAccessAsync(string serverId, CancellationToken ct)
    {
        var outcome = await RequestSessionAndAwaitAsync(serverId, ct);

        switch (outcome.Kind)
        {
            case OutcomeKind.Opened:
                // A grant already exists (unexpected for a request-access tool). Don't track the
                // session here — SpaceWatcher reopens it properly. Tell the caller it's granted.
                return new AccessRequestResult(AlreadyGranted: true, AccessRequestId: null);
            case OutcomeKind.Pending:
                return new AccessRequestResult(AlreadyGranted: false, AccessRequestId: outcome.Value);
            default: // Denied
                return new AccessRequestResult(AlreadyGranted: false, AccessRequestId: null);
        }
    }

    /// <summary>
    /// Routes a tools/call to the session owning <paramref name="namespacedName"/>'s slug
    /// and returns the backend's raw JSON-RPC response (caller extracts the result).
    /// </summary>
    /// <remarks>
    /// <paramref name="idNode"/> is the Claude-facing request id used by the outbound
    /// aggregator correlation (T9). The backend uses the session's own monotonic id space,
    /// independent of the Claude id, so it is not used for routing here.
    /// </remarks>
    public async Task<string> CallAsync(string namespacedName, string argsJson, JsonNode idNode, CancellationToken ct)
    {
        if (!ToolNamespacer.TrySplit(namespacedName, out var slug, out var tool))
            throw new InvalidOperationException($"not a namespaced tool name: {namespacedName}");

        if (!_sessionsBySlug.TryGetValue(slug, out var session) || !session.IsAlive)
            throw new InvalidOperationException($"server unavailable: {slug}");

        var arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var @params = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments,
        };
        var resp = await session.SendRequestAsync("tools/call", @params, ct, _toolCallTimeout);
        return resp.Raw();
    }

    /// <summary>Closes the session for the given server id, if any.</summary>
    public Task CloseAsync(string serverId)
    {
        // Close established sessions.
        foreach (var kv in _sessionsById)
        {
            if (kv.Value.ServerId == serverId && _sessionsById.TryRemove(kv.Key, out var s))
            {
                _sessionsBySlug.TryRemove(s.Slug, out _);
                s.OnClosed("closed by manager");
            }
        }
        // cli-m7: also cancel sessions still mid-handshake for this server.
        // Without this, a revoked server's handshaking session completes and promotes
        // into the live tables, briefly re-surfacing the server's tools.
        foreach (var kv in _handshakingSessions)
        {
            if (kv.Value.ServerId == serverId && _handshakingSessions.TryRemove(kv.Key, out var s))
                s.OnClosed("server closed mid-handshake");
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { await _drainTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { /* expected */ }
        catch (TimeoutException) { /* best-effort */ }

        FaultAllOnShutdown();
        _cts.Dispose();
    }
}

/// <summary>
/// Thrown when an E2E handshake fails due to a cryptographic error (e.g. confirm-tag mismatch),
/// indicating active tampering. Distinguished from a benign absence-of-E2E so callers can
/// abort even under --e2e=prefer.
/// </summary>
internal sealed class E2eHandshakeTamperingException : Exception
{
    public E2eHandshakeTamperingException(string message) : base(message) { }
    public E2eHandshakeTamperingException(string message, Exception inner) : base(message, inner) { }
}
