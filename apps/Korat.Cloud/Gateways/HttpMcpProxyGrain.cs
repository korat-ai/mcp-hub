using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Threading.Channels; // Finding 16, M6
using System.Threading.RateLimiting; // Increment 2 (HTTP MCP OAuth), Task 5: per-server egress cap
using Korat.Cloud.Mcp.Http;
using Korat.Cloud.Mcp.Oauth; // Increment 2 (HTTP MCP OAuth), Task 5
using Korat.Cloud.Security.Envelope;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;
using Korat.Mcp;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): see IHttpMcpProxyGrain. Loads the McpServer's
/// RemoteUrl/AuthMode/AuthHeaderName/secret ciphertext once per activation (OnActivateAsync) —
/// the secret/auth identity IS shared across all of this server's consumers (spec §11 decision
/// 1), decrypts it via IEnvelopeCrypto (fail-closed on decrypt failure), and holds ONE
/// HttpMcpClient PER CONSUMER SESSION (Crux Finding 14) in `_consumers`.
///
/// One-way dispatch (Crux Finding 13): DispatchFrameAsync itself does almost no work — it
/// get-or-creates the consumer's FIFO channel + worker task (starting one on first use) and
/// synchronously enqueues the frame, extending this activation's lifetime past Orleans' idle-GC
/// window (DelayDeactivation) — then returns immediately. The actual upstream call + push runs on
/// that consumer's own long-lived worker task (Finding 16, M6 — see RunConsumerWorkerAsync),
/// never on this activation's Orleans turn, so one consumer's slow upstream call cannot block
/// Orleans from processing this grain's NEXT call (another consumer's DispatchFrameAsync,
/// CloseConsumerSessionAsync, or EvictAsync).
///
/// Finding 16, M6 — per-consumer FIFO replaces the pre-review SemaphoreSlim gate. The old design
/// detached EACH FRAME onto its own independent Task.Run, gated only by a SemaphoreSlim inside
/// BuildResponseAsync — SemaphoreSlim.WaitAsync continuations are not a documented FIFO queue, so
/// a consumer's "notifications/initialized" (sent first) could lose the race to the very next
/// frame ("tools/list") reaching the gate first. Now each ConsumerUpstream owns a Channel and
/// EXACTLY ONE worker task draining it in arrival order — DispatchFrameAsync's synchronous
/// enqueue (this activation processes its own calls one at a time, so enqueue order == arrival
/// order) is what makes ordering reliable, which Finding 16's B2 pass-through-initialize design
/// depends on. This also removes the SemaphoreSlim entirely — nothing left to dispose racily
/// (Crux Finding S6 is subsumed by this fix).
///
/// Finding 16, B2 — pass-through initialize. The consumer already speaks real MCP end-to-end
/// (BackendSession.cs:293-294 confirms its first two frames are always "initialize" then
/// "notifications/initialized"). BuildResponseAsync forwards the consumer's OWN "initialize"
/// request AS the upstream initialize (HttpMcpClient.InitializeAsync(HttpMcpMessage, ...)) —
/// never a second, protocol-violating one — and keeps a lazy own-initialize fallback
/// (InitializeWithOwnRequestAsync) ONLY for a consumer that skips the handshake entirely. A
/// request between "initialize" and "notifications/initialized" is rejected with a JSON-RPC
/// error; a notification (no "id") is forwarded upstream via SendNotificationAsync (which
/// tolerates the spec's own 202/empty-body response) and never gets a push back.
///
/// Response-path limits (Crux Finding 15; Finding 16 M3 — corrected ownership): the PER-MESSAGE
/// cap is enforced at the HTTP layer (HttpMcpClient's bounded read, incl. the SSE reader,
/// PayloadLimitPolicy.DefaultPerMessageBytes). The SESSION-hard-limit (250 MB) cap is now
/// GRAIN-OWNED — see ConsumerUpstream.BytesPushed and PushResponseWithGrainOwnedCapAsync — rather
/// than relying on SessionRoutingTable's `_payloadTrackers`, which only has an entry on a silo
/// that has already handled THIS session's OpenSession/GetRouteAsync; under 2-silo random Orleans
/// placement, this grain's activation silo routinely has neither, so a silo-side-only check would
/// silently not apply to roughly half of all sessions. SessionRoutingTable.PushHttpCloudResponseAsync
/// is still called for delivery + best-effort byte-accounting parity with stdio_node sessions.
///
/// DI note: SessionRoutingTable is injected directly (not IRelayBackplane) so the push goes
/// through the local-fast-path-then-backplane delivery every other frame in this codebase uses —
/// calling IRelayBackplane.PublishToConnectionAsync directly would silently no-op under
/// NullRelayBackplane (single-silo/no-NATS — the mode this repo's own test suite runs in).
/// </summary>
public sealed class HttpMcpProxyGrain(
    IMetadataRepository repository,
    IEnvelopeCrypto envelopeCrypto,
    IOutboundHttpClientFactory httpClientFactory,
    SessionRoutingTable routingTable,
    IGrainFactory grainFactory,
    ILogger<HttpMcpProxyGrain> logger) : Grain, IHttpMcpProxyGrain
{
    // Bound on a single upstream HTTP call (initialize or send). Generous, independently chosen —
    // NOT the old 30s figure, which was implicitly calibrated to Orleans' grain-call ceiling that
    // one-way dispatch removes (Crux Finding 15). This codebase's own hosted-agent tools commonly
    // run minutes; 5 minutes leaves headroom without letting a truly hung remote leak forever.
    private static readonly TimeSpan UpstreamCallTimeout = TimeSpan.FromMinutes(5);
    // Keeps this activation alive past Orleans' idle-GC window while a consumer's worker task is
    // actively processing — comfortably above UpstreamCallTimeout. Refreshed on every
    // DispatchFrameAsync call, so a session with live traffic never deactivates mid-flight; a
    // genuinely idle session may still deactivate (its worker task then sits idle forever,
    // blocked on an empty channel read — a pre-existing characteristic of this design this
    // finding does not change or worsen, not a new leak M6 introduces).
    private static readonly TimeSpan ActivationKeepAlive = TimeSpan.FromMinutes(10);

    private McpServer? _server;
    private string? _decryptedSecret;
    private readonly ConcurrentDictionary<string, ConsumerUpstream> _consumers = new();

    // Increment 2 (HTTP MCP OAuth), Task 5.
    private McpOAuthTokenDocument? _oauthToken;
    private readonly object _refreshLock = new();
    private Task<bool>? _refreshTask;

    // Increment 2 egress cap (spec §"Abuse / egress protection", FOLLOWUPS #4): bounds the number
    // of concurrent in-flight CONSUMER-DRIVEN upstream MCP calls per server (across all of this
    // server's per-consumer workers). Token refresh calls do NOT acquire a permit (see
    // BuildResponseAsync — refresh happens INSIDE an already-acquired lease's retry path, never as
    // a second dispatched frame). One ConcurrencyLimiter (not a PartitionedRateLimiter) because
    // there is exactly one partition: "this grain activation."
    private const int EgressConcurrencyCeiling = 8;
    private const int EgressQueueLimit = 32;
    private readonly ConcurrencyLimiter _egressLimiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = EgressConcurrencyCeiling,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = EgressQueueLimit,
    });

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var serverId = new McpServerId(this.GetPrimaryKeyString());
        _server = await repository.GetMcpServerAsync(serverId, cancellationToken);
        if (_server is not null && _server.AuthMode is McpServerAuthModes.Bearer or McpServerAuthModes.Header)
        {
            var ciphertext = await repository.GetMcpServerSecretCiphertextAsync(serverId, cancellationToken);
            if (ciphertext is not null)
            {
                try
                {
                    _decryptedSecret = await envelopeCrypto.DecryptAsync(
                        _server.SpaceId, McpServerSecretCrypto.Aad(serverId), ciphertext, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fail-closed: leave _decryptedSecret null. BuildResponseAsync below refuses
                    // to call upstream without the required auth rather than falling back to
                    // unauthenticated.
                    logger.LogWarning(ex, "HttpMcpProxyGrain: secret decrypt failed serverId={ServerId}", serverId.Value);
                }
            }
        }
        else if (_server is not null && McpServerAuthModes.IsOAuth(_server.AuthMode))
        {
            // Increment 2 (HTTP MCP OAuth), Task 5: load the stored token document once per
            // activation, same fail-closed shape as the Bearer/Header branch above — a decrypt
            // failure leaves _oauthToken null, and BuildResponseAsync's oauth-missing-token guard
            // refuses to dial upstream unauthenticated.
            var ciphertext = await repository.GetMcpServerOAuthTokenCiphertextAsync(serverId, cancellationToken);
            if (ciphertext is not null)
            {
                try
                {
                    var json = await envelopeCrypto.DecryptAsync(
                        _server.SpaceId, McpServerSecretCrypto.OAuthAad(serverId), ciphertext, cancellationToken);
                    _oauthToken = McpOAuthTokenDocument.Deserialize(json);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "HttpMcpProxyGrain: oauth token decrypt failed serverId={ServerId}", serverId.Value);
                }
            }
        }
        await base.OnActivateAsync(cancellationToken);
    }

    // Fable review (Task 5 hardening) Finding 3: intentionally NO OnDeactivateAsync override to
    // dispose `_egressLimiter`. It holds no timer/unmanaged resource (verified against the
    // ConcurrencyLimiter source — the MINOR #8 "queue/timer state" rationale that first motivated a
    // dispose was inaccurate), so it is fully GC-reclaimed with the activation. Disposing it on
    // deactivation would RACE the detached per-consumer workers, which run off the Orleans turn and
    // are NOT drained by EvictAsync (inc-1 M7 deliberately does not await them): a worker calling
    // `_egressLimiter.AcquireAsync` after a deactivation-time dispose would throw
    // ObjectDisposedException, surfacing to the consumer as a misleading "Internal error." for every
    // frame still draining. Leaving it undisposed is both leak-free and race-free.

    public Task DispatchFrameAsync(byte[] frameBytes, ConnectionId consumerConnectionId, SessionId consumerSessionId, CancellationToken cancellationToken)
    {
        // Crux Finding 13: keep this activation alive past the idle-GC window while this
        // consumer's worker task is in flight, then return immediately.
        DelayDeactivation(ActivationKeepAlive);

        // Finding 16, M6: get-or-start this consumer's FIFO worker, then synchronously enqueue.
        // Orleans processes THIS grain's calls one at a time, so the enqueue order below always
        // matches the order frames actually arrived at the grain — ordering is preserved even
        // though this method itself returns before the frame is processed.
        var consumer = _consumers.GetOrAdd(consumerSessionId.Value,
            _ => StartConsumer(consumerSessionId, consumerConnectionId));
        if (!consumer.Inbox.Writer.TryWrite(frameBytes))
            logger.LogWarning("HttpMcpProxyGrain: consumer inbox closed, frame dropped serverId={ServerId} session={SessionId}",
                this.GetPrimaryKeyString(), consumerSessionId.Value);

        return Task.CompletedTask;
    }

    private ConsumerUpstream StartConsumer(SessionId consumerSessionId, ConnectionId consumerConnectionId)
    {
        var consumer = new ConsumerUpstream(
            new HttpMcpClient(httpClientFactory.CreateClient("mcp-http-cloud"), _server?.RemoteUrl ?? string.Empty),
            consumerConnectionId);
        // Detached — runs independently of this grain's Orleans turn (Crux Finding 13); NOT
        // awaited here. Exactly one such task exists per consumer session for its whole lifetime.
        consumer.WorkerTask = Task.Run(() => RunConsumerWorkerAsync(consumerSessionId, consumer));
        return consumer;
    }

    /// <summary>
    /// Finding 16, M6: the FIFO drain loop — exactly one of these runs per consumer session,
    /// processing that consumer's frames strictly in arrival order. Never throws unhandled (any
    /// exception from BuildResponseAsync is already caught internally; this loop additionally
    /// guards against a truly unexpected failure so the worker task itself never dies silently).
    ///
    /// Fable gate FIX 2 (T4 unhappy-path hardening, [BLOCKER]): a push failure is handled
    /// differently depending on WHY it failed — see <see cref="PushOutcome"/> and
    /// <see cref="PushResponseWithGrainOwnedCapAsync"/>. Only a <see cref="PushOutcome.CapExceeded"/>
    /// stops this worker (the session really is closing); a <see cref="PushOutcome.Undeliverable"/>
    /// is routine (NATS reconnect window, or a response over NATS max_payload — normal for
    /// http_cloud under 2-silo placement) and must NOT kill the pipeline, or every later frame for
    /// this consumer would silently pile into a channel nobody drains once this loop exits.
    /// </summary>
    private async Task RunConsumerWorkerAsync(SessionId consumerSessionId, ConsumerUpstream consumer)
    {
        try
        {
            await foreach (var frameBytes in consumer.Inbox.Reader.ReadAllAsync())
            {
                byte[]? responseBytes;
                try
                {
                    responseBytes = await BuildResponseAsync(frameBytes, consumer);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "HttpMcpProxyGrain: unhandled failure serverId={ServerId} session={SessionId}",
                        this.GetPrimaryKeyString(), consumerSessionId.Value);
                    responseBytes = HttpMcpMessage.Error(null, -32000, "Internal error.").ToUtf8Bytes();
                }

                if (responseBytes is null)
                    continue; // Finding 16, B2: a notification (no "id") — nothing to push back.

                var outcome = await PushResponseWithGrainOwnedCapAsync(consumerSessionId, consumer, responseBytes);
                if (outcome == PushOutcome.CapExceeded)
                {
                    // Fable gate FIX 2: the session really is closing (CloseForResponsePayloadLimitAsync
                    // already tore down SessionRoutingTable's own route + notified the consumer) —
                    // un-wedge THIS grain's own bookkeeping too, so a LATER frame for the same
                    // consumerSessionId (a stray/racing frame from before the consumer processes
                    // the close) starts a FRESH upstream instead of silently piling into a channel
                    // nobody is left draining once this loop exits below.
                    consumer.Inbox.Writer.TryComplete();
                    _consumers.TryRemove(consumerSessionId.Value, out _);
                    break;
                }
                // Fable gate FIX 2: PushOutcome.Undeliverable (a plain, transient delivery failure)
                // must NOT stop this worker — the session is still alive as far as this grain
                // knows; it gets torn down through the normal close paths (SessionTerminator /
                // bridge-disconnect / peer-initiated CloseSession), not here. Keep draining.
            }
        }
        finally
        {
            // Finding 16, M7 (this review pass): the worker OWNS its client's disposal and disposes
            // only AFTER its loop has fully drained (writer completed + the in-flight frame
            // finished). This is exactly what lets ShutdownConsumer be non-blocking — the close path
            // never has to await this task on the grain turn (see ShutdownConsumer). No
            // disposed-client race remains: nothing touches consumer.Client once the loop exits, and
            // the loop only exits after the last in-flight upstream call has returned.
            consumer.Client.Dispose();
        }
    }

    /// <summary>
    /// Finding 16, B2: pass-through "initialize", handshake-ordering enforcement, and notification
    /// handling. Returns null for a notification (nothing to push back); returns the JSON-RPC
    /// response bytes for a request.
    /// </summary>
    private async Task<byte[]?> BuildResponseAsync(byte[] frameBytes, ConsumerUpstream consumer)
    {
        if (_server is null)
            return HttpMcpMessage.Error(null, -32000, "MCP server not found.").ToUtf8Bytes();

        // Fable holistic review FIX 2 [SHOULD-FIX]: a still-open consumer session can reactivate
        // this grain AFTER the owner disabled the server. McpServerGrain.DisableAsync calls
        // EvictAsync() on THIS grain, but a later frame on the same still-open session simply
        // reactivates it — OnActivateAsync reloads `_server` fresh from the repository every
        // activation, so it loads the row with Status = Disabled, yet nothing below checked Status
        // at all (only `_server is null`), so it would serve the frame anyway — dialing the remote
        // with the owner's decrypted secret after the owner turned the server "off" (spec §6).
        // Deny for ANY non-Published status here, before ever touching consumer.Client — this also
        // makes the reserved NeedsReauth status (Increment 2) fail closed for free, and covers
        // Unavailable too. Mirrors the existing "-32000 / generic message" shape immediately below
        // so the consumer never learns WHICH state the server is actually in.
        if (_server.Status != McpServerStatus.Published)
            return HttpMcpMessage.Error(null, -32000, "MCP server is not available.").ToUtf8Bytes();

        // Increment 2 (HTTP MCP OAuth), Task 5: the oauth analogue of the Bearer/Header
        // authRequiredButMissing guard immediately below — no stored (or decryptable) token means
        // fail closed, never dial the remote unauthenticated.
        var authRequiredButMissing =
            (_server.AuthMode is McpServerAuthModes.Bearer or McpServerAuthModes.Header && _decryptedSecret is null)
            || (McpServerAuthModes.IsOAuth(_server.AuthMode) && _oauthToken is null);
        if (authRequiredButMissing)
            return HttpMcpMessage.Error(null, -32000, "Server configuration error.").ToUtf8Bytes();

        HttpMcpMessage incoming;
        try
        {
            incoming = HttpMcpMessage.Parse(frameBytes);
        }
        catch (Exception)
        {
            return HttpMcpMessage.Error(null, -32700, "Parse error.").ToUtf8Bytes();
        }

        void InjectAuth(HttpRequestMessage request)
        {
            if (_server.AuthMode == McpServerAuthModes.Bearer && _decryptedSecret is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _decryptedSecret);
            else if (_server.AuthMode == McpServerAuthModes.Header && _decryptedSecret is not null && _server.AuthHeaderName is not null)
                request.Headers.TryAddWithoutValidation(_server.AuthHeaderName, _decryptedSecret);
            else if (McpServerAuthModes.IsOAuth(_server.AuthMode) && _oauthToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _oauthToken.AccessToken);
        }

        var isNotification = incoming.Id is null;

        try
        {
            using var cts = new CancellationTokenSource(UpstreamCallTimeout);

            // Increment 2 (HTTP MCP OAuth), Task 5 (Grounding Note 8): one egress-cap lease per
            // DISPATCHED FRAME, acquired before any upstream call this frame makes (initialize
            // pass-through, own-initialize fallback, notification, or send) and released when this
            // whole try block exits (success, early return, or exception) — bounds concurrent
            // in-flight upstream calls per server without gating the refresh call itself (refresh
            // runs INSIDE this already-acquired lease's retry path below, never as a second
            // dispatched frame).
            using var egressLease = await _egressLimiter.AcquireAsync(1, cts.Token);
            if (!egressLease.IsAcquired)
                return isNotification ? null : HttpMcpMessage.Error(incoming.Id, -32000,
                    "Server is at its concurrent-request limit; try again shortly.").ToUtf8Bytes();

            // Proactive refresh: if the stored access token is within 30s of expiry (or already
            // expired), refresh BEFORE dialing upstream at all. Single-flight — see
            // RefreshOAuthTokenSingleFlightAsync's own doc comment for why concurrent dispatches
            // across this server's OTHER consumer sessions all await the SAME refresh rather than
            // each starting their own.
            //
            // Fable review (Task 5 hardening) Finding 1: `doc` is captured here, BEFORE the
            // near-expiry check, and passed to the single-flight gate — if another consumer's
            // refresh already moved `_oauthToken` on by the time this call runs, the gate short-
            // circuits to `true` with no second rotation. When `refreshed` comes back `true`,
            // `_oauthToken` has moved past `doc`, so the post-refresh re-check below intentionally
            // keeps using `doc`'s (now-stale) expiry rather than re-reading the field — it is only
            // consulted in the `!refreshed` branch, where the field is guaranteed to still equal
            // `doc` (no rotation happened), so `doc` is current there.
            var doc = _oauthToken;
            if (McpServerAuthModes.IsOAuth(_server.AuthMode) && doc is not null
                && doc.AccessExpiry <= DateTimeOffset.UtcNow.AddSeconds(30))
            {
                var refreshed = await RefreshOAuthTokenSingleFlightAsync(doc, cts.Token);
                if (!refreshed && doc.AccessExpiry <= DateTimeOffset.UtcNow)
                    return isNotification ? null : HttpMcpMessage.Error(incoming.Id, -32000, "Server configuration error.").ToUtf8Bytes();
            }

            if (incoming.Method == "initialize")
            {
                // Finding 16, B2: THIS request IS the upstream initialize — genuine pass-through,
                // not a second/synthetic one. The response already carries the consumer's own id.
                var initResponse = await SendWithOAuthRetryAsync(
                    () => consumer.Client.InitializeAsync(incoming, InjectAuth, PayloadLimitPolicy.DefaultPerMessageBytes, cts.Token), cts.Token);
                consumer.Initialized = true;
                return initResponse.ToUtf8Bytes();
            }

            if (isNotification)
            {
                // Fable gate FIX 3 (T4 unhappy-path hardening, [MUST-FIX]): lift the barrier from
                // the CONSUMER's own handshake state BEFORE attempting upstream delivery, not
                // after. The barrier tracks whether THIS consumer has completed its handshake —
                // it does not depend on whether the upstream notification POST actually
                // succeeded. The consumer sends "notifications/initialized" exactly once; if this
                // ordering were reversed (set only after SendNotificationAsync returns) a single
                // transient upstream failure on that one call would mean the barrier never lifts,
                // rejecting every subsequent request -32002 forever.
                if (incoming.Method == "notifications/initialized")
                    consumer.PastInitializedBarrier = true;

                // Forward every notification upstream (including "notifications/initialized" —
                // the UPSTREAM server's own handshake state machine needs to see it too, since
                // this grain forwarded the consumer's real "initialize" as ITS real initialize
                // above). Correction (Task 5 hardening, fable-flagged): an HTTP-STATUS failure
                // here (e.g. a stale/revoked token drawing a 401) is NOT "caught by the outer
                // catch below" — nothing throws for it. HttpMcpClient.SendNotificationAsync never
                // inspects the response status code at all (see its own doc comment), so a
                // non-2xx status is silently dropped with no refresh/signal (best-effort by
                // design — a notification has no reply to fail). Only a NETWORK-level failure
                // (HttpRequestException/OperationCanceledException) throws HttpMcpUpstreamException,
                // which the outer catch below DOES handle. See FOLLOWUPS.md (Finding 5) for the
                // deferred fix (classify 401 + one single-flight refresh + one re-send) — the
                // barrier above has already lifted by then regardless.
                await consumer.Client.SendNotificationAsync(incoming, InjectAuth, cts.Token);
                return null;
            }

            if (!consumer.Initialized)
            {
                // Finding 16, B2 (own-initialize fallback — NOT the normal path): this consumer
                // sent a request before ever sending its own "initialize" (unusual, but a
                // hand-rolled or misbehaving consumer could). Lazily establish the upstream
                // session ourselves so it exists at all, then continue below. No
                // "notifications/initialized" will ever arrive from a consumer that skipped
                // "initialize" in the first place, so lift the barrier immediately too.
                await SendWithOAuthRetryAsync(
                    () => consumer.Client.InitializeWithOwnRequestAsync(InjectAuth, PayloadLimitPolicy.DefaultPerMessageBytes, cts.Token), cts.Token);
                consumer.Initialized = true;
                consumer.PastInitializedBarrier = true;
            }

            if (!consumer.PastInitializedBarrier)
            {
                // Finding 16, B2: a request arrived between the consumer's own "initialize" and
                // its "notifications/initialized" — reject per the MCP handshake's own ordering
                // requirement. M6's FIFO ordering is what makes this check meaningful rather than
                // a source of spurious rejections (without it, "notifications/initialized" could
                // race behind this very request under the old per-frame-Task.Run design).
                return HttpMcpMessage.Error(incoming.Id, -32002,
                    "Server not initialized: notifications/initialized not yet received.").ToUtf8Bytes();
            }

            var response = await SendWithOAuthRetryAsync(
                () => consumer.Client.SendAsync(incoming, InjectAuth, PayloadLimitPolicy.DefaultPerMessageBytes, cts.Token), cts.Token);
            return response.ToUtf8Bytes();

            // Increment 2 (HTTP MCP OAuth), Task 5: reactive refresh-then-retry-once on a 401.
            // Wraps EVERY upstream request/response call this method makes (initialize
            // pass-through, own-initialize fallback, and the final send) — not just the final
            // send — since a stale token can just as easily surface on the FIRST upstream call a
            // freshly-activated consumer ever makes (the own-initialize fallback) as on a later
            // one. Safe for non-oauth servers too: the `when` guard means the catch simply
            // doesn't match for Bearer/Header/None, so the exception passes straight through
            // exactly as if this wrapper did not exist.
            async Task<HttpMcpMessage> SendWithOAuthRetryAsync(Func<Task<HttpMcpMessage>> send, CancellationToken retryCt)
            {
                // Finding 1 (fable Task-5 hardening): capture the token this attempt uses BEFORE the
                // send, so a 401 refreshes against THAT document. If another consumer's worker already
                // rotated past it (reference moved), the single-flight gate short-circuits with no
                // redundant rotation and the retry re-reads the fresh _oauthToken via InjectAuth.
                var observed = _oauthToken;
                try
                {
                    return await send();
                }
                catch (HttpMcpUnauthorizedException) when (McpServerAuthModes.IsOAuth(_server.AuthMode))
                {
                    var refreshed = await RefreshOAuthTokenSingleFlightAsync(observed, retryCt);
                    if (!refreshed)
                        throw;
                    return await send(); // retry ONCE with the refreshed token (InjectAuth re-reads _oauthToken)
                }
            }
        }
        catch (HttpMcpUnauthorizedException)
        {
            // Upstream 401, either: (oauth) SendWithOAuthRetryAsync's own refresh-then-retry ALSO
            // got a fresh 401 (a token bad even after refresh), or (static Bearer/Header) no refresh
            // applies at all — the `when IsOAuth` retry filter skipped it, so a static-auth 401 lands
            // here directly with no refresh ever attempted. Same safe/generic shape as any other
            // upstream error, never the raw status/body.
            logger.LogWarning("HttpMcpProxyGrain upstream 401 serverId={ServerId}", this.GetPrimaryKeyString());
            return isNotification ? null : HttpMcpMessage.Error(incoming.Id, -32000, "Upstream MCP server error.").ToUtf8Bytes();
        }
        catch (HttpMcpUpstreamException ex)
        {
            // Fail-closed, no upstream body/secret ever included — HttpMcpUpstreamException.Message
            // is always a pre-sanitized, safe-to-return string (see HttpMcpClient). Also covers the
            // oversized-response case (Crux Finding 15's bounded read throws this same type).
            logger.LogWarning("HttpMcpProxyGrain upstream error serverId={ServerId} reason={Reason}",
                this.GetPrimaryKeyString(), ex.Message);
            return isNotification ? null : HttpMcpMessage.Error(incoming.Id, -32000, "Upstream MCP server error.").ToUtf8Bytes();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "HttpMcpProxyGrain unexpected error serverId={ServerId}", this.GetPrimaryKeyString());
            return isNotification ? null : HttpMcpMessage.Error(incoming.Id, -32000, "Internal error.").ToUtf8Bytes();
        }
    }

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth), Task 5: thread-safe single-flight refresh. N concurrent
    /// per-consumer worker tasks (real OS threads via Task.Run — NOT serialized by Orleans'
    /// single-threaded turn model, since BuildResponseAsync runs on each consumer's own detached
    /// worker — see this class's own doc comment) calling this concurrently all await the SAME
    /// in-flight refresh Task rather than each starting their own (spec §"Token lifecycle":
    /// "single-flight per server ... N simultaneous 401s trigger one refresh"). This is the
    /// GENUINE single-flight mechanism here — NOT Orleans grain-call non-reentrancy, which only
    /// serializes the fast DispatchFrameAsync enqueue, not the actual upstream/refresh work that
    /// runs on detached per-consumer workers.
    ///
    /// Deliberately does NOT forward the calling frame's own `ct` into the shared refresh work
    /// itself — see DoRefreshOAuthTokenAsync's doc comment for why. `ct` is still put to use, just
    /// at a safe layer: `Task.WaitAsync(ct)` lets THIS caller stop waiting on its own frame-scoped
    /// timeout (surfacing as OperationCanceledException, which BuildResponseAsync's existing
    /// exception handling already covers) WITHOUT cancelling the underlying shared `_refreshTask`
    /// out from under any OTHER concurrent caller still awaiting it.
    ///
    /// Fable review (Task 5 hardening) Finding 1+2 — generation-check + clear-by-identity: the
    /// caller passes the `McpOAuthTokenDocument` IT observed before deciding a refresh is needed
    /// (proactive: the field read before the near-expiry check; reactive: read at the top of
    /// `SendWithOAuthRetryAsync`, before the first send). If `_oauthToken` has already moved on
    /// from `observed` by the time this runs, some OTHER caller's refresh already installed a new
    /// record (every refresh installs a NEW instance via `with` — reference equality is the
    /// correct comparison, never a value/expiry comparison) — this caller's own trigger condition
    /// is stale, so it returns `Task.FromResult(true)` immediately: NO second rotation. This closes
    /// Finding 1 (a caller arriving just after a refresh completes previously started a full,
    /// unnecessary second rotation — both a latent test-timing-flake and a token-endpoint
    /// amplification vector for an attacker-registered RemoteUrl that always 401s).
    ///
    /// Finding 2 — clearing `_refreshTask` is now done by IDENTITY via a `ContinueWith` scheduled
    /// AFTER the field assignment, never a blanket `finally` inside `DoRefreshOAuthTokenAsync`
    /// itself. The bug a blanket finally-clear risks: `_refreshTask ??= DoRefreshOAuthTokenAsync()`
    /// evaluates the RHS before the assignment; if `DoRefreshOAuthTokenAsync` ever completed
    /// synchronously, its `finally` would run BEFORE the assignment (Monitor is reentrant on the
    /// same thread inside this same lock), clearing a field that is about to be overwritten with
    /// the now-completed (and therefore never-cleared) task — refresh permanently wedged for the
    /// rest of this activation's lifetime. `ContinueWith` cannot run until AFTER `task` has been
    /// both created and assigned to `_refreshTask` below, so this ordering hazard cannot occur; the
    /// `ReferenceEquals(_refreshTask, t)` check inside it additionally guards against clearing a
    /// LATER task that has since replaced this one.
    /// </summary>
    private Task<bool> RefreshOAuthTokenSingleFlightAsync(McpOAuthTokenDocument? observed, CancellationToken ct)
    {
        Task<bool> shared;
        lock (_refreshLock)
        {
            if (!ReferenceEquals(_oauthToken, observed))
                return Task.FromResult(true); // already refreshed since this caller read the token — no second rotation.
            if (_refreshTask is null)
            {
                var task = DoRefreshOAuthTokenAsync();
                _refreshTask = task;
                // Clear by IDENTITY when THIS task finishes (Finding 2): never a blanket
                // finally-clear inside DoRefreshOAuthTokenAsync, which — if DoRefresh ever completed
                // synchronously — would clear before the assignment above and wedge refresh
                // permanently. ContinueWith is scheduled here, after `_refreshTask = task`, so it can
                // only run once that assignment has already happened.
                _ = task.ContinueWith(
                    t => { lock (_refreshLock) { if (ReferenceEquals(_refreshTask, t)) _refreshTask = null; } },
                    TaskScheduler.Default);
            }
            shared = _refreshTask;
        }
        return shared.WaitAsync(ct);
    }

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth), Task 5: runs under CancellationToken.None, NEVER the winning
    /// caller's own per-dispatch `cts.Token` (BuildResponseAsync's `using var cts = new
    /// CancellationTokenSource(UpstreamCallTimeout)`). The bug this avoids: the single-flight gate
    /// memoizes whichever frame calls RefreshOAuthTokenSingleFlightAsync FIRST — if that frame's
    /// own `using var cts` disposes (or, worse, actually CANCELS via its own UpstreamCallTimeout
    /// firing) as soon as THAT ONE frame's BuildResponseAsync call returns, every OTHER concurrent
    /// frame still awaiting the SAME shared `_refreshTask` would have its refresh cancelled out
    /// from under it too — a single fast/short-lived frame's timeout would silently break refresh
    /// for every other in-flight consumer. The refresh is a grain-activation-scoped operation, not
    /// a per-frame one, so it must not be bound to any one frame's lifetime.
    /// `McpOAuthTokenExchange.RefreshAsync` still has its OWN independent bounded timeout (20s,
    /// internally linked off CancellationToken.None) — this does not make refresh calls unbounded,
    /// only decouples them from any single caller's disposable CTS.
    ///
    /// GOTCHA discovered while making this method's own tests green (not in the plan's reference
    /// code): this method calls `IMcpServerGrain.MarkNeedsReauthAsync()` and MUST use the
    /// constructor-injected `grainFactory` field, NOT the inherited `Grain.GrainFactory` property.
    /// `Grain.GrainFactory`'s getter calls `GrainRuntime.CheckRuntimeContext`, which throws
    /// `InvalidOperationException("Activation access violation. A non-activation thread attempted
    /// to access activation services.")` whenever accessed off the grain's own Orleans-scheduled
    /// turn — and this WHOLE method runs on a detached per-consumer `Task.Run` worker (see this
    /// class's own doc comment: BuildResponseAsync is NOT serialized by Orleans' turn model),
    /// never on a turn. Confirmed by direct repro: the pre-fix code (`GrainFactory.GetGrain<...>()`)
    /// threw exactly that exception, silently swallowed by this method's own catch-all, so
    /// MarkNeedsReauthAsync never actually ran and Status never flipped — a real bug, not a typo,
    /// caught only because the invalid_grant/no-refresh-token tests kept failing after the rest of
    /// this task was otherwise green. `grainFactory` (constructor-injected `IGrainFactory`, the
    /// same DI-registered instance non-grain background services like SessionReaperService already
    /// use to call grains from their own non-turn execution) has no such ambient-context
    /// requirement — it is designed to be called from anywhere.
    /// </summary>
    private async Task<bool> DoRefreshOAuthTokenAsync()
    {
        try
        {
            if (_oauthToken?.RefreshToken is null)
            {
                // No refresh token — expiry means re-consent (spec §"Token lifecycle": "If the AS
                // issues no refresh token, access-token expiry maps to NeedsReauth"). An access
                // token with no refresh token, once refresh is triggered at all, is dead going
                // forward — clear the stored ciphertext too (final fable gate Finding 1), not just
                // the Status flip, for the same "dead ciphertext must not survive as re-publishable
                // storage" reason documented on the invalid_grant branch below.
                var deadServerId = new McpServerId(this.GetPrimaryKeyString());
                await repository.ClearMcpServerOAuthTokenAsync(deadServerId, CancellationToken.None);
                _oauthToken = null;
                await grainFactory.GetGrain<IMcpServerGrain>(this.GetPrimaryKeyString()).MarkNeedsReauthAsync();
                return false;
            }

            var result = await McpOAuthTokenExchange.RefreshAsync(
                httpClientFactory, _oauthToken.TokenEndpoint, _oauthToken.RefreshToken,
                _oauthToken.ClientId, _oauthToken.ClientSecret, _server!.RemoteUrl!, CancellationToken.None);

            var updated = _oauthToken with
            {
                AccessToken = result.AccessToken,
                // Rotation: use the newly-issued refresh token if the AS rotated it; otherwise
                // keep the existing one (the AS did not rotate this time).
                RefreshToken = result.RefreshToken ?? _oauthToken.RefreshToken,
                AccessExpiry = result.AccessExpiry,
            };

            // Rotation persist-before-use (spec §"Token lifecycle"): the ciphertext is written
            // BEFORE _oauthToken is swapped in for any caller to use — a crash between rotation
            // and persist loses the grant rather than serving a rotated token that was never
            // durably stored.
            var serverId = new McpServerId(this.GetPrimaryKeyString());
            var json = McpOAuthTokenDocument.Serialize(updated);
            var ciphertext = await envelopeCrypto.EncryptAsync(_server.SpaceId, McpServerSecretCrypto.OAuthAad(serverId), json, CancellationToken.None);
            await repository.SetMcpServerOAuthTokenAsync(serverId, ciphertext, CancellationToken.None);
            _oauthToken = updated;
            return true;
        }
        catch (McpOAuthInvalidGrantException ex)
        {
            // Final fable gate Finding 1 (composition defect T1<->T5): MarkNeedsReauthAsync flips
            // Status, but McpServerGrain.EnableAsync decides Published-vs-NeedsReauth on re-enable
            // from `hasUsableOAuthToken`, which it computes as "the OAuth ciphertext row is
            // non-null" (repository.GetMcpServerOAuthTokenCiphertextAsync(...) is not null) — NOT
            // from Status. If the dead ciphertext were left in place here, a later disable->enable
            // would see it, conclude the grant is still usable, and re-Published a server whose
            // refresh token the AS just revoked (invalid_grant) — a lying catalog and a broken
            // first session. Clear the ciphertext BEFORE (order doesn't matter for correctness,
            // but doing it first means a crash between the two calls still leaves the safer of the
            // two states: no ciphertext, Status not yet flipped, rather than the reverse) flipping
            // Status, and null the in-memory cache too so THIS activation immediately fail-closes
            // via the authRequiredButMissing guard instead of repeatedly dialing a dead token until
            // eviction.
            //
            // Tradeoff (deliberate): clearing the ciphertext means a subsequent Reconnect can no
            // longer REUSE the stored DCR client (McpOAuthConnectActionBuilder.BuildAsync reads it
            // from this same ciphertext) — it re-runs DCR fresh, which is correct/transparent for a
            // DCR-capable AS (the Miro target) and for a manual-cred AS the /reconnect body already
            // accepts clientId/clientSecret. A refresh-dead grant is dead; enable/catalog must not
            // treat it as usable.
            var deadServerId = new McpServerId(this.GetPrimaryKeyString());
            await repository.ClearMcpServerOAuthTokenAsync(deadServerId, CancellationToken.None);
            _oauthToken = null;
            logger.LogWarning("HttpMcpProxyGrain: oauth refresh invalid_grant serverId={ServerId} reason={Reason}", this.GetPrimaryKeyString(), ex.Message);
            await grainFactory.GetGrain<IMcpServerGrain>(this.GetPrimaryKeyString()).MarkNeedsReauthAsync();
            return false;
        }
        catch (McpOAuthTransientTokenException ex)
        {
            // Transient (network/5xx/malformed) — Status stays UNCHANGED. A one-hour AS outage
            // must not brick every server into re-consent (spec §"Failure classification").
            logger.LogWarning("HttpMcpProxyGrain: oauth refresh transient failure serverId={ServerId} reason={Reason}", this.GetPrimaryKeyString(), ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HttpMcpProxyGrain: oauth refresh unexpected failure serverId={ServerId}", this.GetPrimaryKeyString());
            return false;
        }
        // Fable review (Task 5 hardening) Finding 2: no `finally` here — clearing `_refreshTask` is
        // owned by RefreshOAuthTokenSingleFlightAsync's `ContinueWith`, scheduled AFTER this task is
        // assigned to the field. A blanket finally-clear inside THIS method would run before that
        // assignment if this method ever completed synchronously, wedging refresh permanently for
        // the rest of the activation's lifetime. See RefreshOAuthTokenSingleFlightAsync's doc
        // comment for the full mechanics.
    }

    /// <summary>
    /// Fable gate FIX 2 (T4 unhappy-path hardening, [BLOCKER]): the two distinct reasons
    /// <see cref="PushResponseWithGrainOwnedCapAsync"/> can fail to deliver a response.
    /// <see cref="CapExceeded"/> means the session really is closing (this grain's own
    /// grain-owned cap check tripped BEFORE ever attempting delivery — see M3); the caller must
    /// un-wedge its own per-consumer bookkeeping so a later frame starts fresh.
    /// <see cref="Undeliverable"/> means SessionRoutingTable.PushHttpCloudResponseAsync itself
    /// returned false for ANY other reason (a NATS reconnect window, a response over NATS
    /// max_payload, a dead local stream not yet re-established) — routine and NOT evidence the
    /// session is closed; the caller must keep draining, not wedge the pipeline over one failed push.
    /// </summary>
    private enum PushOutcome { Delivered, CapExceeded, Undeliverable }

    /// <summary>
    /// Finding 16, M3: the AUTHORITATIVE response-leg session-hard-limit (250 MB) check, owned by
    /// THIS grain — reliable regardless of which silo this activation lives on (unlike a
    /// SessionRoutingTable-side-only tracker lookup, see this class's doc comment). On a
    /// violation, tells the routing table to close the session + notify the consumer directly
    /// (mirrors SessionRoutingTable's own violation-handling shape) rather than attempting the
    /// push at all. On success, delegates to SessionRoutingTable.PushHttpCloudResponseAsync for
    /// delivery + its own best-effort byte-accounting parity with stdio_node sessions.
    ///
    /// Fable gate FIX 2: returns a <see cref="PushOutcome"/> rather than a bare bool so the caller
    /// (RunConsumerWorkerAsync) can tell a grain-owned cap violation (session closing — un-wedge
    /// and stop) apart from a plain undeliverable push (transient — keep draining). Previously
    /// both collapsed into a single `false`, and the caller treated ANY `false` as "session was
    /// torn down" — which is only true for the former.
    /// </summary>
    private async Task<PushOutcome> PushResponseWithGrainOwnedCapAsync(SessionId consumerSessionId, ConsumerUpstream consumer, byte[] responseBytes)
    {
        var newTotal = consumer.BytesPushed + responseBytes.LongLength;
        if (newTotal > PayloadLimitPolicy.DefaultSessionHardLimitBytes)
        {
            logger.LogWarning(
                "HttpMcpProxyGrain: grain-owned session_hard_limit exceeded serverId={ServerId} session={SessionId} totalBytes={TotalBytes}",
                this.GetPrimaryKeyString(), consumerSessionId.Value, newTotal);
            await routingTable.CloseForResponsePayloadLimitAsync(consumerSessionId, consumer.ConnectionId, CancellationToken.None);
            return PushOutcome.CapExceeded;
        }
        consumer.BytesPushed = newTotal;

        var delivered = await routingTable.PushHttpCloudResponseAsync(
            consumerSessionId, consumer.ConnectionId, responseBytes, CancellationToken.None);
        if (!delivered)
        {
            // Fable gate FIX 2: routine and transient (NATS reconnect window / oversized-for-NATS
            // response / cross-silo placement) — NOT evidence the session was torn down. Logged,
            // not escalated; the caller must keep draining rather than wedge the pipeline.
            logger.LogWarning("HttpMcpProxyGrain: response undeliverable (transient) serverId={ServerId} session={SessionId}",
                this.GetPrimaryKeyString(), consumerSessionId.Value);
            return PushOutcome.Undeliverable;
        }
        return PushOutcome.Delivered;
    }

    public Task CloseConsumerSessionAsync(SessionId consumerSessionId)
    {
        // Finding 16, M7: non-blocking — signal the worker to retire and return immediately. Do NOT
        // await the worker (it may be mid-5-min upstream call; this grain is non-reentrant, so
        // awaiting would stall DispatchFrameAsync for every OTHER consumer of this same server).
        if (_consumers.TryRemove(consumerSessionId.Value, out var consumer))
            ShutdownConsumer(consumer);
        return Task.CompletedTask;
    }

    public Task EvictAsync()
    {
        // Finding 16, M7: signal every worker to retire (non-blocking); each finishes its in-flight
        // frame and disposes its own client. DeactivateOnIdle then reloads config on next activation.
        foreach (var kvp in _consumers)
            ShutdownConsumer(kvp.Value);
        _consumers.Clear();
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finding 16, M7 (this review pass) — NON-BLOCKING close. Completes the consumer's inbox so
    /// RunConsumerWorkerAsync drains its in-flight frame and then exits; the worker disposes its own
    /// HttpMcpClient in its finally (see RunConsumerWorkerAsync). This deliberately does NOT await
    /// the worker task: the worker can be mid-upstream-call for up to UpstreamCallTimeout (5 min),
    /// and this grain is NOT [Reentrant] — awaiting here would hold the activation and stall
    /// DispatchFrameAsync for EVERY OTHER consumer of this same server (one grain serves all
    /// consumers of one McpServerId, and bridge-disconnect close is the single most common close
    /// path — Finding 16, M1) for the whole call. Signalling completion and returning keeps close
    /// O(1) and lets each worker retire itself. (The earlier M6 draft awaited the worker here to
    /// order the dispose after the last use; the worker-owned finally achieves the same ordering
    /// without the grain-turn stall.)
    /// </summary>
    private static void ShutdownConsumer(ConsumerUpstream consumer)
        => consumer.Inbox.Writer.TryComplete();

    /// <summary>
    /// One consumer session's upstream MCP session state (Crux Finding 14). Finding 16, M6: FIFO
    /// via a Channel + exactly one worker task (replaces the SemaphoreSlim gate). Finding 16, M3:
    /// BytesPushed is the grain-owned, placement-independent cumulative response-byte counter.
    /// </summary>
    private sealed class ConsumerUpstream(HttpMcpClient client, ConnectionId connectionId)
    {
        public HttpMcpClient Client { get; } = client;
        public ConnectionId ConnectionId { get; } = connectionId;
        public bool Initialized { get; set; }
        public bool PastInitializedBarrier { get; set; }
        public long BytesPushed { get; set; }
        public Channel<byte[]> Inbox { get; } = Channel.CreateUnbounded<byte[]>();
        /// <summary>Finding 16, M7: the fire-and-forget sink for StartConsumer's detached
        /// Task.Run (assigning it cleanly discards the task without a CS4014 warning). No longer
        /// awaited on close — the worker retires itself via its own finally, so ShutdownConsumer
        /// stays non-blocking. Retained as a diagnostic handle.</summary>
        public Task WorkerTask { get; set; } = Task.CompletedTask;
    }
}
