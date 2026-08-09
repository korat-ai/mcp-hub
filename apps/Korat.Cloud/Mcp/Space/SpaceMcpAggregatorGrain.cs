using System.Text.Json.Nodes;
using Korat.Cloud.Gateways;
using Korat.Cloud.Gateways.Admission;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;
using Orleans.Concurrency;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Space;

/// <summary>
/// Space-MCP (increment 1, Task 4): the per-session aggregator grain — one activation per
/// Streamable-HTTP MCP session, keyed by the server-generated <c>Mcp-Session-Id</c>
/// (<see cref="ISpaceMcpAggregatorGrain"/>'s <c>IGrainWithStringKey</c>).
///
/// <c>[Reentrant]</c>: a slow backend `tools/call` (Task 6) must not block this SAME session's
/// other concurrent calls — e.g. the HTTP responder's GET-SSE loop (Task 8) polling
/// <see cref="NextListChangedAsync"/>, or <see cref="OnDeliveryAsync"/> demuxing another
/// backend's response — from making progress on this grain's single-threaded turn.
///
/// <see cref="InitializeAsync"/>: registers the in-process delivery leg FIRST (so the
/// routing-table slot is live before any backend can respond), then discovers the Space's
/// Published MCP servers, opens a backend relay session for each GRANTED one via
/// <c>ISessionAdmission.AdmitAsync</c> with <c>ConsumerBindPolicy.ServerMinted</c> — Task 5:
/// concurrently, bounded by <see cref="MaxConcurrentBackendOpens"/> and independently timed out
/// per backend via <see cref="PerBackendTimeout"/>, so one slow/hung backend never stalls the
/// others (S9) — and registers a synthetic "request-access" stub tool for each UNGRANTED one
/// (<see cref="AggregateCatalog.SetUngranted"/>).
///
/// <see cref="DispatchAsync"/> handles <c>initialize</c> (cached result), notifications/responses
/// (→ <c>null</c>, 202), <c>tools/list</c> (→ the catalog, which already includes both granted
/// tools and Task 5's request-access stubs), and (Task 6) <c>tools/call</c> routing: a real tool
/// routes to its backend and the response is reframed under the external client's own id
/// (<see cref="HandleToolRouteAsync"/>); a <c>request-access__&lt;slug&gt;</c> call creates an
/// access request (<see cref="HandleRequestAccessRouteAsync"/>, N1: catches the
/// already-granted race). Any other method is <c>-32601</c>.
///
/// <see cref="OnDeliveryAsync"/> (B1 plan-review correction, wired from Task 3's
/// <see cref="CallbackServerStreamWriter"/>): demuxes a delivered frame to the owning
/// <see cref="SpaceBackendSession"/> by relay <c>SessionId</c>; a close event faults that backend
/// and removes the live session. Its last known catalog survives transient availability loss so
/// the next tool call can reopen it; authorization-invalidating closes still remove it. ALL
/// grain-state mutation happens here, on the scheduler.
/// </summary>
[Reentrant]
public sealed class SpaceMcpAggregatorGrain(
    ISessionAdmission admission,
    SessionRoutingTable routingTable,
    IClusterClient clusterClient,
    IMetadataRepository repository,
    IGrainFactory grainFactory,
    SessionTerminator terminator,
    ILogger<SpaceMcpAggregatorGrain> logger)
    : Grain, ISpaceMcpAggregatorGrain
{
    /// <summary>MCP protocol versions this aggregator can echo back on <c>initialize</c> (N4) —
    /// mirrors the Global Constraint's accepted set exactly (Task 7 rejects anything else with
    /// a per-request <c>400</c>, independent of this echo choice).</summary>
    private static readonly HashSet<string> SupportedProtocolVersions =
        new(StringComparer.Ordinal) { "2025-06-18", "2025-03-26" };

    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>S9 (Task 5): bounds how many backend opens run concurrently during
    /// <see cref="InitializeAsync"/>'s fan-out — a Space with many granted servers must not
    /// open unboundedly many relay sessions (and downstream subprocess/gRPC connections) all at
    /// once.</summary>
    internal const int MaxConcurrentBackendOpens = 8;

    /// <summary>S9 (Task 5): per-backend budget for the WHOLE open (admission + handshake +
    /// tools/list) — not just <see cref="SpaceBackendSession"/>'s own internal
    /// <c>SendRequestAsync</c> wait — so a backend that hangs during <c>ISessionAdmission.AdmitAsync</c>
    /// itself (which has no timeout of its own) is bounded too, not only a hung MCP reply.
    /// Forty seconds leaves room for the server-minted mobile wake window (30s) plus the MCP
    /// handshake. Individual handshake requests remain bounded by
    /// <see cref="SpaceBackendSession.HandshakeTimeout"/>.
    /// Mutable (not const) so integration tests can shrink it for a hung-backend scenario without
    /// burning 40 real seconds per run — mirrors <c>SessionAdmissionCharacterizationTests</c>'
    /// own <c>wakeWaitSeconds</c> override precedent; a bare static field is the pragmatic
    /// equivalent here since an Orleans-activated grain has no per-call constructor-injection
    /// seam. Safe as shared mutable state because this test assembly runs sequentially (no
    /// assembly-level parallelization) — tests must restore the default in a finally block.</summary>
    internal static TimeSpan PerBackendTimeout = TimeSpan.FromSeconds(40);

    /// <summary>Task 8: how long <see cref="NextListChangedAsync"/>'s slow path waits for a bump
    /// before returning the cursor unchanged (a keep-alive "nothing changed yet" heartbeat) — MUST
    /// stay well under Orleans' default 30s grain-call response timeout (plan-review correction
    /// N2), since this wait runs INSIDE a single <c>NextListChangedAsync</c> grain call the
    /// dispatcher's GET-SSE loop is awaiting; a wait too close to (or past) 30s risks the runtime
    /// timing out the call itself instead of returning cleanly. 15s gives a wide margin. Mutable
    /// (not const) so integration tests can shrink it — same test-shrink precedent as
    /// <see cref="PerBackendTimeout"/> and <see cref="ReconcileInterval"/>; restore the default in
    /// a <c>finally</c> block.</summary>
    internal static TimeSpan ListChangedHeartbeat = TimeSpan.FromSeconds(15);

    /// <summary>Task 8: how often the backstop reconcile timer (<see cref="_reconcileTimer"/>)
    /// re-runs <see cref="SpaceServerDiscovery.DiscoverAsync"/> to pick up a newly-approved grant
    /// (approval sends no frame of its own — this timer is the ONLY path that observes it; a
    /// revoke reaches <see cref="OnDeliveryAsync"/> synchronously between ticks and needs no timer;
    /// transient close is handled lazily by the next tool call). Mutable (not const) so integration tests can shrink it — same test-shrink precedent
    /// as <see cref="PerBackendTimeout"/>; restore the default in a <c>finally</c> block.</summary>
    internal static TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private readonly AggregateCatalog _catalog = new();
    private readonly Dictionary<string, SpaceBackendSession> _backendsBySessionId = new();
    private readonly Dictionary<string, SpaceBackendSession> _backendsBySlug = new();
    // This grain is [Reentrant]: concurrent calls for one offline tool must share one admission /
    // APNs wake / handshake operation instead of opening duplicate relay sessions and pushes.
    private readonly Dictionary<string, Task> _backendOpenTasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _takenSlugs = new();
    // A cached catalog keeps its namespace while the publisher is temporarily offline. Without
    // this reservation, two same-name servers reconnecting in the opposite order can silently
    // swap namespaced tool names even though no tools/list change was announced.
    private readonly Dictionary<string, string> _reservedSlugsByServerId = new(StringComparer.Ordinal);

    private bool _initialized;
    private ConnectionId _syntheticConn;
    private SpaceMcpBinding? _binding;
    private string? _cachedInitializeResultJson;

    /// <summary>F3 (adversarial review): the memoized in-flight/completed <see cref="InitializeAsync"/>
    /// task, set SYNCHRONOUSLY at method entry (before any await) — closes a reentrancy hole the
    /// old <c>_initialized</c>-only guard had: that flag was set only AFTER the
    /// <c>RegisterAgentStreamAsync</c> await returned, so two concurrent <see cref="InitializeAsync"/>
    /// calls could both observe <c>_initialized==false</c> and interleave at that await, each
    /// registering the delivery leg (silently overwriting the routing-table slot and leaking the
    /// FIRST call's NATS subscription) and each opening every granted backend from scratch. Reset
    /// to <c>null</c> on failure (see <see cref="AwaitWithResetOnFailureAsync"/>) so a later retry
    /// can re-init rather than wedging every future call on this activation behind a permanently
    /// faulted task.</summary>
    private Task<string>? _initializeTask;

    /// <summary>MUST-FIX 1 (adversarial review, second pass, BLOCKER): flips true at the very
    /// start of <see cref="TerminateAsync"/> and <see cref="OnDeactivateAsync"/> — BEFORE either
    /// does anything else, including any await. Closes the race where <see cref="TerminateAsync"/>
    /// snapshots <see cref="_backendsBySessionId"/> while a granted backend is still inside
    /// <c>admission.AdmitAsync</c> (node-wake can take seconds): that backend is absent from the
    /// snapshot, so <see cref="TerminateAsync"/>'s teardown loop never touches its relay session,
    /// yet the backend can still complete admission/handshake and get indexed/cataloged AFTER
    /// teardown already ran — leaking its publisher-side relay session forever. <see cref="OpenBackendAsync"/>
    /// re-checks this flag at two points (see its own comments) and terminates rather than
    /// indexing/cataloging once it is true.</summary>
    private bool _tornDown;

    // Task 6: kept alongside _binding (which only stores the string projection for
    // GetBindingAsync) so DispatchAsync's "tools/call request-access__<slug>" branch can call
    // ISpaceGrain.CreateAccessRequestAsync with the real typed identity/Space this session was
    // initialized with, without re-deriving or re-parsing them from _binding's strings.
    private ConsumerId _consumerIdentity;
    private SpaceId _spaceId;

    // Task 8: kept alongside _consumerIdentity/_spaceId for the same reason — ReconcileAsync's
    // periodic re-discovery needs a full SpaceMcpSessionContext to call OpenBackendBoundedAsync
    // exactly the way InitializeCoreAsync's own fan-out does, and ctx.Owner is not otherwise
    // retained anywhere on this grain.
    private UserId _owner;

    /// <summary>Task 8 (GET-SSE <c>list_changed</c> watch): monotonically increasing —
    /// incremented once per <see cref="BumpListChanged"/> call. <see cref="NextListChangedAsync"/>'s
    /// fast path compares a caller's <c>knownCursor</c> against this value; the dispatcher's
    /// GET-SSE loop only emits a <c>notifications/tools/list_changed</c> event when it observes a
    /// STRICTLY GREATER value than the one it already knows about.</summary>
    private long _listChangedCursor;
    // Last catalog state represented by the cursor/initial tools list. A lazy reconnect can
    // refresh schemas while opening a cached backend; compare against this snapshot so a real
    // change emits list_changed exactly once, while an identical reconnect stays silent.
    private string _announcedToolsListJson = "";

    /// <summary>Task 8: the "next bump" signal <see cref="NextListChangedAsync"/>'s slow path
    /// awaits (bounded by <see cref="ListChangedHeartbeat"/>). <see cref="BumpListChanged"/> swaps
    /// this to a FRESH instance before completing the old one — see that method's own doc comment
    /// for why this order never loses a waiter. <c>RunContinuationsAsynchronously</c> so
    /// completing it never runs a waiter's continuation inline on this (the bumping call's) turn.</summary>
    private TaskCompletionSource _cursorBump = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Task 8 backstop timer: re-runs <see cref="SpaceServerDiscovery.DiscoverAsync"/> on
    /// <see cref="ReconcileInterval"/> and reconciles this session's open backends/catalog against
    /// it — the ONLY path that picks up a newly-APPROVED grant (approval sends no frame of its own;
    /// the sync <see cref="OnDeliveryAsync"/> close-path handles revocation immediately
    /// between ticks). Registered at the end of <see cref="InitializeCoreAsync"/> (after the first
    /// fan-out already opened everything granted at that point); disposed in
    /// <see cref="TerminateAsync"/>/<see cref="OnDeactivateAsync"/> so no timer survives this
    /// activation's own teardown.</summary>
    private IGrainTimer? _reconcileTimer;

    /// <summary>Task 8: true while a <see cref="ReconcileAsync"/> tick is already in flight —
    /// checked (and set) SYNCHRONOUSLY at the very start of <see cref="ReconcileTimerCallbackAsync"/>,
    /// before any await, so a second timer tick firing while a slow reconcile (many concurrent
    /// admits) is still running on this <c>[Reentrant]</c> grain simply no-ops and retries next
    /// tick, rather than running two overlapping reconciles that could race each other's
    /// open/close decisions against the same backend.</summary>
    private bool _reconciling;

    /// <summary>Task 8: the ungranted server ids <see cref="AggregateCatalog.SetUngranted"/> was
    /// last called with (by <see cref="InitializeCoreAsync"/> or a prior <see cref="ReconcileAsync"/>
    /// tick) — kept so a tick can tell whether the ungranted SET actually changed (mirrors the CLI
    /// <c>SpaceWatcher.ComputeDiff</c>'s own <c>UngrantedAdded</c>/<c>UngrantedRemoved</c> check)
    /// instead of unconditionally rebuilding the catalog and bumping <see cref="_listChangedCursor"/>
    /// on every tick even when nothing changed.</summary>
    private HashSet<string> _ungrantedServerIds = new();

    public Task<string> InitializeAsync(SpaceMcpSessionContext ctx, string clientInitializeJson)
    {
        // F3 (adversarial review, second pass): memoize the in-flight/completed task itself,
        // set SYNCHRONOUSLY here before any await — see _initializeTask's own doc comment for
        // the reentrancy hole this closes. A concurrent second call returns the SAME Task the
        // first call is already running (or has already completed), never re-entering the body.
        if (_initializeTask is { } inFlight)
            return inFlight;

        var task = InitializeCoreAsync(ctx, clientInitializeJson);
        _initializeTask = task;
        return AwaitWithResetOnFailureAsync(task);
    }

    private async Task<string> AwaitWithResetOnFailureAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            // Reset so a later retry can re-init instead of wedging every future call on this
            // activation behind a permanently faulted memoized task.
            _initializeTask = null;
            throw;
        }
    }

    private async Task<string> InitializeCoreAsync(SpaceMcpSessionContext ctx, string clientInitializeJson)
    {
        // SF-4: idempotence. The interface doc says "called at most once per grain activation",
        // but nothing enforced that — a second initialize (e.g. a client retry after a slow
        // first response it gave up waiting on) would re-run RegisterAgentStreamAsync (silently
        // overwriting the routing-table slot and leaking the FIRST call's NATS subscription —
        // SubscribeConnectionAsync has no matching unsubscribe on overwrite) and re-open every
        // granted backend from scratch (duplicate relay sessions/subprocesses for the same
        // logical MCP session). Short-circuit on the cached result instead. F3: in practice this
        // is now unreachable via the outer _initializeTask memoization (a genuine second call
        // after completion returns the same completed Task above) — kept as a defensive fallback.
        if (_initialized)
            return _cachedInitializeResultJson ?? BuildInitializeResultJson(DefaultProtocolVersion);

        _binding = new SpaceMcpBinding(ctx.ConsumerIdentity.Value, ctx.SpaceId.Value);
        _consumerIdentity = ctx.ConsumerIdentity;
        _spaceId = ctx.SpaceId;
        _owner = ctx.Owner;
        _syntheticConn = SpaceMcpConsumerIdentity.SyntheticConnectionId(this.GetPrimaryKeyString());

        // FIX B (holistic review, should-fix): registry BEFORE delivery leg — deliberate order.
        // RegisterAsync is a pure HashSet.Add with no dependency on the delivery leg being live;
        // nothing races an unregistered routing-table slot from here because backends aren't
        // opened until after _initialized flips below. So if RegisterAsync throws, the delivery
        // leg was NEVER registered — the client's retry (AwaitWithResetOnFailureAsync resets
        // _initializeTask) re-runs RegisterAgentStreamAsync for the FIRST time on this activation,
        // cleanly, with no leak. The old (delivery-leg-first) order leaked on every such retry: a
        // registry failure AFTER the leg was already registered meant the retry re-ran
        // RegisterAgentStreamAsync for the SAME _syntheticConn, silently overwriting the
        // routing-table slot without unsubscribing the prior NATS subscription
        // (SubscribeConnectionAsync has no unsubscribe-on-overwrite — see its own comment). A
        // transient registry entry pointing at a not-yet-initialized aggregator is harmless
        // either way: TerminateAsync no-ops on _initialized == false, and the registry's own
        // TerminateAllAsync finally-remove sweeps phantom ids.
        //
        // Task 7 (inc-2a, SF-6/SF-1): index this session under its consumer identity so a
        // consent revoke can find and terminate it — BEFORE flipping _initialized (fail-CLOSED,
        // plan-review correction SF-1). Registering here means a registration failure aborts init
        // (this await throws, AwaitWithResetOnFailureAsync resets _initializeTask, _initialized is
        // never set) — the client's retry re-enters InitializeCoreAsync from scratch and gets a
        // clean second attempt, exactly like any other failure during initialize (e.g. a backend
        // that never opens).
        await GrainFactory.GetGrain<ISpaceMcpConsumerSessionsGrain>(ctx.ConsumerIdentity.Value)
            .RegisterAsync(this.GetPrimaryKeyString());

        // Register the delivery leg BEFORE opening any backend — a backend that replies
        // immediately after admission must never race an unregistered routing-table slot. Ordered
        // AFTER the registry registration above (FIX B) — see that comment for why.
        await routingTable.RegisterAgentStreamAsync(
            _syntheticConn,
            new CallbackServerStreamWriter(grainFactory, this.GetPrimaryKeyString(), logger),
            CancellationToken.None);

        _initialized = true;

        var servers = await SpaceServerDiscovery.DiscoverAsync(
            clusterClient, repository, ctx.SpaceId, ctx.ConsumerIdentity, CancellationToken.None);

        // Task 5 (S9): bounded-concurrency fan-out — MaxConcurrentBackendOpens caps how many
        // backend opens run at once; each is independently bounded by PerBackendTimeout so one
        // slow/hung backend can never stall (or indefinitely delay) InitializeAsync over the
        // others'. Per-session catalog (SF-9) — this._catalog is never shared across activations.
        using var semaphore = new SemaphoreSlim(MaxConcurrentBackendOpens);
        var grantedServers = servers.Where(s => s.Granted).ToList();
        var openTasks = grantedServers.Select(server => OpenBackendBoundedAsync(server, ctx, semaphore)).ToList();
        await Task.WhenAll(openTasks);

        var ungrantedServers = servers
            .Where(s => !s.Granted)
            .Select(s => new ServerDescriptor(s.Id.Value, s.DisplayName))
            .ToList();
        _catalog.SetUngranted(ungrantedServers);
        // Task 8: seed the diff baseline so the FIRST reconcile tick only reports a change if the
        // ungranted set actually moved since this initial snapshot, not unconditionally.
        _ungrantedServerIds = ungrantedServers.Select(s => s.Id).ToHashSet();
        _announcedToolsListJson = _catalog.ToolsListJson();

        var protocolVersion = ExtractEchoProtocolVersion(clientInitializeJson);
        _cachedInitializeResultJson = BuildInitializeResultJson(protocolVersion);

        // Task 8: register the backstop reconcile timer AFTER the first fan-out above has already
        // opened everything granted at initialize time — the timer only ever needs to react to
        // CHANGES from this snapshot onward, never to the initial state itself. Deliberately the
        // LAST thing this method does, so a failure anywhere above (caught by
        // AwaitWithResetOnFailureAsync, which resets _initializeTask for a retry) never leaves a
        // timer registered against a half-initialized activation.
        _reconcileTimer = this.RegisterGrainTimer(
            ReconcileTimerCallbackAsync, ReconcileInterval, ReconcileInterval);

        return _cachedInitializeResultJson;
    }

    private async Task OpenBackendBoundedAsync(BackendServer server, SpaceMcpSessionContext ctx, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(PerBackendTimeout);
            await EnsureBackendOpenAsync(server, ctx, cts.Token);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Opens at most one relay session per server. The activation is reentrant, so multiple tool
    /// calls can arrive while admission is awaiting a mobile wake; every caller joins the same
    /// task and observes the resulting live backend.
    /// </summary>
    private async Task<SpaceBackendSession?> EnsureBackendOpenAsync(
        BackendServer server,
        SpaceMcpSessionContext ctx,
        CancellationToken ct)
    {
        var live = FindLiveBackend(server.Id.Value);
        if (live is not null)
            return live;

        // Release any dead entry's slug/session before a new open computes its namespace.
        var dead = _backendsBySlug.Values
            .FirstOrDefault(b => b.ServerId == server.Id.Value && !b.IsAlive);
        if (dead is not null)
            EvictDeadBackendLocal(dead);

        if (!_backendOpenTasks.TryGetValue(server.Id.Value, out var openTask))
        {
            openTask = OpenBackendAsync(server, ctx, ct);
            _backendOpenTasks[server.Id.Value] = openTask;
        }

        try
        {
            await openTask;
        }
        finally
        {
            if (_backendOpenTasks.TryGetValue(server.Id.Value, out var current)
                && ReferenceEquals(current, openTask))
            {
                _backendOpenTasks.Remove(server.Id.Value);
            }
        }

        return FindLiveBackend(server.Id.Value);
    }

    private SpaceBackendSession? FindLiveBackend(string serverId) =>
        _backendsBySlug.Values.FirstOrDefault(b => b.ServerId == serverId && b.IsAlive);

    private string ReserveSlug(BackendServer server)
    {
        if (_reservedSlugsByServerId.TryGetValue(server.Id.Value, out var existing))
            return existing;

        var slug = ToolNamespacer.UniqueSlug(server.DisplayName, server.Id.Value, _takenSlugs);
        _reservedSlugsByServerId[server.Id.Value] = slug;
        return slug;
    }

    private void ReleaseReservedSlug(string serverId)
    {
        if (_reservedSlugsByServerId.Remove(serverId, out var slug))
            _takenSlugs.Remove(slug);
    }

    private void ReleaseReservedSlugIfUncataloged(string serverId)
    {
        if (!_catalog.HasGrantedServer(serverId))
            ReleaseReservedSlug(serverId);
    }

    private async Task OpenBackendAsync(BackendServer server, SpaceMcpSessionContext ctx, CancellationToken ct)
    {
        var principal = new ConsumerPrincipal(
            ctx.ConsumerIdentity,
            ctx.SpaceId,
            _syntheticConn,
            SessionAdmission.AggregatorSentinelNodeId,
            null,
            ConsumerBindPolicy.ServerMinted,
            "Connected MCP client");

        AdmissionResult result;
        try
        {
            result = await admission.AdmitAsync(server.Id, principal, ct);
        }
        catch (Exception ex)
        {
            // Catches OperationCanceledException (PerBackendTimeout elapsed) too — a hung/slow
            // admission must never propagate out of Task.WhenAll and fail every OTHER backend's
            // open along with it. This backend's tools are simply absent from the catalog.
            logger.LogWarning(ex, "Space-MCP: admission failed/timed-out for backend serverId={ServerId}", server.Id.Value);
            return;
        }

        if (result is not AdmissionResult.Opened opened)
        {
            // Should not normally happen — discovery only forwarded servers this identity has
            // an active grant for (the same GetActiveGrantAsync query AdmitAsync itself re-runs).
            // A denial here means the grant was revoked in the window between discovery and
            // admission — leave the server absent from the catalog rather than failing the
            // whole InitializeAsync call over one backend.
            logger.LogInformation(
                "Space-MCP: backend not opened serverId={ServerId} result={ResultType}",
                server.Id.Value, result.GetType().Name);
            return;
        }

        // MUST-FIX F1 checkpoint (a) (adversarial review, second pass, BLOCKER): _tornDown may
        // have flipped true WHILE we were inside admission.AdmitAsync above (node-wake can take
        // seconds) — TerminateAsync's own _backendsBySessionId snapshot cannot have included this
        // backend (it isn't in ANY grain dictionary yet), so its teardown loop never touched this
        // just-opened relay session. Terminate it here, BEFORE ever indexing/handshaking/cataloging
        // it — otherwise it would be indexed into an already-torn-down activation's dictionaries
        // (nothing wipes them again — TerminateAsync already ran once) and its relay session leaks
        // the publisher-side MCP subprocess + an Active session row forever.
        if (_tornDown)
        {
            try
            {
                await terminator.TerminateSessionAsync(
                    new SessionId(opened.SessionId.Value), SessionCloseReason.ServerUnavailable, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Space-MCP: failed to terminate late-opening backend relay session after teardown serverId={ServerId}",
                    server.Id.Value);
            }
            return;
        }

        var slug = ReserveSlug(server);
        var backend = new SpaceBackendSession(
            routingTable, SessionAdmission.AggregatorSentinelNodeId, server.Id.Value, slug, opened.SessionId.Value);

        // Index by relay SessionId BEFORE the handshake — OnDeliveryAsync demuxes the backend's
        // OWN "initialize"/"tools/list" replies by looking this table up by SessionId. Indexing
        // only AFTER InitializeAsync/ListToolsAsync return would be a chicken-and-egg deadlock:
        // the reply that InitializeAsync is awaiting can never be demuxed to a backend session
        // that isn't in the table yet, so every handshake would hang until PerBackendTimeout.
        _backendsBySessionId[opened.SessionId.Value] = backend;

        try
        {
            await backend.InitializeAsync(ct);
            var tools = await backend.ListToolsAsync(ct);
            // MUST-FIX F1 checkpoint (b) + N-b (adversarial review): re-check BOTH _tornDown and
            // IsAlive AFTER the handshake awaits return.
            //   - _tornDown: a concurrent TerminateAsync ran to completion WHILE we awaited the
            //     handshake above. The handshake may have succeeded perfectly cleanly (IsAlive
            //     still true) — that tells us nothing about whether the AGGREGATOR itself is
            //     still alive. Never catalog a backend for an activation that has already torn
            //     down.
            //   - !IsAlive: a CloseSession delivered mid-handshake (OnDeliveryAsync, reentrant on
            //     this same activation) already faulted this backend via OnClosed. Cataloging it
            //     as granted here would advertise tools for a backend that is already known-dead.
            // Either way the outcome is identical: drop it locally and terminate the relay session.
            // F1 part 2: terminate UNCONDITIONALLY on this path now (not only when _tornDown) —
            // TerminateSessionAsync is idempotent (a no-op for an already-closed/unknown session),
            // so this is harmless in the ordinary publisher-initiated-close case the IsAlive branch
            // was originally written for (that branch assumed "closed ⇒ the other side already
            // tearing down itself", which is false in the teardown-race case), and it closes the
            // gap where the relay session is still genuinely open but this activation is already
            // gone.
            if (_tornDown || !backend.IsAlive)
            {
                if (!backend.IsAlive)
                    logger.LogInformation(
                        "Space-MCP: backend closed during its own handshake, not cataloging serverId={ServerId}",
                        server.Id.Value);
                _backendsBySessionId.Remove(opened.SessionId.Value);
                ReleaseReservedSlugIfUncataloged(server.Id.Value);
                try
                {
                    await terminator.TerminateSessionAsync(
                        new SessionId(opened.SessionId.Value), SessionCloseReason.ServerUnavailable, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Space-MCP: failed to terminate backend relay session after handshake serverId={ServerId}",
                        server.Id.Value);
                }
                return;
            }
            _backendsBySlug[slug] = backend;
            _catalog.SetGranted(server.Id.Value, slug, server.DisplayName, tools);
        }
        catch (Exception ex)
        {
            // A hung backend (e.g. never answers tools/list) throws OperationCanceledException
            // once PerBackendTimeout elapses — caught here too, same reasoning as the admission
            // catch above: this backend's tools are simply absent; every OTHER concurrent open
            // must complete normally.
            logger.LogWarning(ex, "Space-MCP: backend open failed/timed-out serverId={ServerId}", server.Id.Value);
            _backendsBySessionId.Remove(opened.SessionId.Value);
            ReleaseReservedSlugIfUncataloged(server.Id.Value);
            backend.OnClosed("handshake failed or timed out");

            // MUST-FIX 1 (S4 BLOCKER rework): an admitted-but-hung backend must have its
            // PUBLISHER-side relay session torn down too, not merely marked locally dead —
            // otherwise it leaks a live publisher-side MCP subprocess forever (same leak
            // TerminateAsync/OnDeactivateAsync close below, just reached via the
            // handshake-timeout path instead). Deliberately CancellationToken.None, NOT `ct`:
            // this catch is most commonly entered BECAUSE `ct` (the per-backend timeout token)
            // just fired, so passing it through would make TerminateSessionAsync's own
            // repository read throw immediately and skip the very teardown it exists to
            // perform. Best-effort/wrapped — one backend's termination failure must never fail
            // the whole concurrent InitializeAsync fan-out (Task.WhenAll).
            try
            {
                // Korat.Domain.SessionCloseReason has no "Failed" member — ServerUnavailable is
                // the closest fit (mirrors HandleToolRouteAsync's "-32000, Server unavailable."
                // for the same "backend didn't respond in time" family of failure).
                await terminator.TerminateSessionAsync(
                    new SessionId(opened.SessionId.Value), SessionCloseReason.ServerUnavailable, CancellationToken.None);
            }
            catch (Exception termEx)
            {
                logger.LogWarning(termEx,
                    "Space-MCP: failed to terminate hung/handshake-failed backend relay session serverId={ServerId}",
                    server.Id.Value);
            }
        }
    }

    /// <summary>Task 8: the <see cref="_reconcileTimer"/> callback. Guards re-entrancy
    /// SYNCHRONOUSLY (before any await) against a slow reconcile still running when the next tick
    /// fires — see <see cref="_reconciling"/>'s own doc comment — and against a tick firing on an
    /// already-torn-down activation (the timer is disposed in <see cref="TerminateAsync"/>/
    /// <see cref="OnDeactivateAsync"/>, but a tick already queued before that Dispose() call could
    /// still be sitting in the scheduler). Never lets an exception from <see cref="ReconcileAsync"/>
    /// escape — a bad tick must not crash the timer or this activation; it simply retries next
    /// tick.</summary>
    private async Task ReconcileTimerCallbackAsync(CancellationToken ct)
    {
        if (_tornDown || _reconciling)
            return;

        _reconciling = true;
        try
        {
            await ReconcileAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Space-MCP: reconcile tick failed sessionId={SessionId}", this.GetPrimaryKeyString());
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>Task 8 backstop: re-runs <see cref="SpaceServerDiscovery.DiscoverAsync"/> and
    /// diffs it against this session's CURRENT open backends / ungranted-stub set — server-side
    /// port of the CLI's own <c>SpaceWatcher.ReconcileAsync</c> polling shape, adapted to this
    /// grain's per-slug/per-session dictionaries instead of a <c>SpaceSnapshot</c> value type.
    ///
    /// This is the ONLY path that reacts to a newly-APPROVED grant — approval sends no frame of
    /// its own, unlike revoke, which reaches <see cref="OnDeliveryAsync"/> synchronously
    /// between ticks (SF-6) and need no timer. Symmetrically, this also backstops the REMOVE side
    /// (a server un-published or a grant that went inactive by some path other than the
    /// synchronous revoke-close, e.g. direct DB state drift) — same "granted but no open backend"
    /// / "backend open but no longer granted" diff either way.
    ///
    /// Reentrancy: <see cref="ReconcileTimerCallbackAsync"/> already guarantees only one tick runs
    /// at a time and that none runs post-teardown. Within a tick, every dictionary is snapshotted
    /// (<c>.ToList()</c>/<c>.ToHashSet()</c>) before any await, and <see cref="_tornDown"/> is
    /// re-checked after the one await that can race a concurrent <see cref="TerminateAsync"/>
    /// (discovery itself) — opens beyond that point are safe by construction because
    /// <see cref="OpenBackendAsync"/>'s own checkpoints (a)/(b) already handle a
    /// <see cref="_tornDown"/> flip landing mid-open.</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (_tornDown)
            return;

        IReadOnlyList<BackendServer> servers;
        try
        {
            servers = await SpaceServerDiscovery.DiscoverAsync(clusterClient, repository, _spaceId, _consumerIdentity, ct);
        }
        catch (Exception ex)
        {
            // Transient discovery failure (DB hiccup, grain call failure) — never let a bad tick
            // crash the timer; the next tick retries from scratch (mirrors SpaceWatcher.RunAsync's
            // own "a transient discovery/reconcile failure must not kill the watcher").
            logger.LogWarning(ex, "Space-MCP: reconcile discovery failed sessionId={SessionId}", this.GetPrimaryKeyString());
            return;
        }

        // Re-check AFTER the only await above that could race a concurrent TerminateAsync/
        // OnDeactivateAsync — this activation may have torn down entirely while discovery was
        // in flight. Nothing below is safe to run against a torn-down activation's dictionaries.
        if (_tornDown)
            return;

        var changed = false;

        // MUST-FIX 3 part 2 (adversarial review, third pass): backstop dead-backend sweep. A
        // backend can mark itself dead OUTSIDE the synchronous OnDeliveryAsync close-path (this
        // session's own enc≠0 fail-closed guard, or an undeliverable SendLineAsync mid a
        // concurrent tools/call — MUST-FIX 3 part 1's own doc comment) — without this sweep such
        // a backend stays indexed in _backendsBySlug/_backendsBySessionId FOREVER: a permanent
        // zombie whose calls can never enter the lazy reopen path. Sweeping HERE makes the live
        // backend snapshot accurate. Its cached tool definitions deliberately remain: temporary
        // availability loss is not a tools/list change, and the next call reopens on demand.
        var deadBackends = _backendsBySlug.Values.Where(b => !b.IsAlive).ToList();
        foreach (var dead in deadBackends)
            EvictDeadBackendLocal(dead);

        var grantedServers = servers.Where(s => s.Granted).ToList();
        var grantedServerIds = grantedServers.Select(s => s.Id.Value).ToHashSet();

        // Snapshot BEFORE any await below — this grain is [Reentrant]; OnDeliveryAsync (a
        // concurrent sync revoke-close) or another interleaved call could mutate
        // _backendsBySlug while this tick is suspended at an await otherwise.
        var openServerIds = _backendsBySlug.Values.Select(b => b.ServerId).ToHashSet();

        // (a) Newly granted servers that have never produced a catalog — open them via the SAME
        // bounded-concurrency path InitializeCoreAsync's own fan-out uses. A temporarily offline
        // server WITH a cached catalog is intentionally excluded: timer-driven reconnects would
        // burn silent-push budget even when nobody is trying to use the tool. Its first call owns
        // the wake/reopen instead.
        var toOpen = grantedServers
            .Where(s => !openServerIds.Contains(s.Id.Value) && !_catalog.HasGrantedServer(s.Id.Value))
            .ToList();
        if (toOpen.Count > 0)
        {
            var openCtx = new SpaceMcpSessionContext(_consumerIdentity, _spaceId, _owner);
            using var semaphore = new SemaphoreSlim(MaxConcurrentBackendOpens);
            var openTasks = toOpen.Select(server => OpenBackendBoundedAsync(server, openCtx, semaphore)).ToList();
            await Task.WhenAll(openTasks);

            // OpenBackendAsync only ever indexes/catalogs a backend on genuine success (its own
            // _tornDown/IsAlive checkpoints drop anything else) — a server actually present in
            // _backendsBySlug now that wasn't in the pre-open snapshot proves a real open landed,
            // exactly like SpaceWatcher's own "committed only actually-open granted servers"
            // comment. A failed/denied/timed-out open leaves that server absent — retried next
            // tick automatically, same retry-by-omission behavior as the CLI reference.
            if (toOpen.Any(s => _backendsBySlug.Values.Any(b => b.ServerId == s.Id.Value)))
                changed = true;
        }

        // (b) Open backends whose server is no longer granted (revoked/unpublished/deleted by a
        // path other than the synchronous OnDeliveryAsync close) — terminate + evict. A backend
        // already evicted by a racing OnDeliveryAsync close-path simply won't appear in this
        // snapshot's re-lookup below, so it's never double-counted or double-bumped.
        //
        // Bonus fix (adversarial review, third pass — discovered empirically while building
        // MUST-FIX 3's own test, not itself one of the review's findings): this loop used to
        // AWAIT terminator.TerminateSessionAsync inline, same as EvictDeadBackendLocal originally
        // did — and carries the EXACT SAME self-notify hazard EvictDeadBackendLocal's own doc
        // comment documents (every backend's "agent" side is bound to THIS activation, so
        // terminating one loops back through a fresh nested OnDeliveryAsync call on this same
        // activation). Left un-fixed, a reconcile tick landing on this branch (a server
        // unpublished/de-granted by a path OTHER than the synchronous revoke-close — e.g. direct
        // DB state drift, the scenario this backstop exists for) would stall THIS reconcile tick
        // for the same ~30s before the identity-recheck/eviction below could even run — during
        // which _reconciling stays true, blocking every other tick. Fired-and-forgotten below,
        // like EvictDeadBackendLocal, for the same reason.
        var toClose = _backendsBySlug.Values.Where(b => !grantedServerIds.Contains(b.ServerId)).ToList();
        foreach (var backend in toClose)
        {
            // Re-check identity (not just presence) before mutating: this grain is [Reentrant], so
            // a concurrent OnDeliveryAsync close-path for this SAME backend could have already
            // evicted it (and even let a brand-new backend claim the same slug, in principle)
            // between this snapshot and now. Only tear down OUR OWN snapshot's entry.
            if (_backendsBySlug.TryGetValue(backend.Slug, out var current) && ReferenceEquals(current, backend))
            {
                _backendsBySlug.Remove(backend.Slug);
                _backendsBySessionId.Remove(backend.SessionId);
                ReleaseReservedSlug(backend.ServerId);
                _catalog.RemoveGranted(backend.ServerId);
                backend.OnClosed("no longer granted (reconcile backstop)");
                changed = true;
            }

            _ = TerminateBackendBestEffortAsync(backend, SessionCloseReason.Revoked);
        }

        // A revoked/unpublished server may already be offline and therefore absent from
        // _backendsBySlug, so the live-backend close loop above cannot see it. Prune every stale
        // cached catalog against the authoritative discovery snapshot as a fail-closed backstop.
        foreach (var serverId in _reservedSlugsByServerId.Keys
                     .Where(id => !grantedServerIds.Contains(id))
                     .ToList())
        {
            ReleaseReservedSlug(serverId);
        }
        if (_catalog.RemoveGrantedExcept(grantedServerIds))
            changed = true;

        // (c) Ungranted-stub set changed — refresh the request-access catalog entries. Diffed
        // against _ungrantedServerIds (not unconditionally rebuilt) so a tick where nothing about
        // the ungranted set moved doesn't spuriously bump the cursor.
        var ungrantedServers = servers
            .Where(s => !s.Granted)
            .Select(s => new ServerDescriptor(s.Id.Value, s.DisplayName))
            .ToList();
        var newUngrantedServerIds = ungrantedServers.Select(s => s.Id).ToHashSet();
        if (!newUngrantedServerIds.SetEquals(_ungrantedServerIds))
        {
            _catalog.SetUngranted(ungrantedServers);
            _ungrantedServerIds = newUngrantedServerIds;
            changed = true;
        }

        // N11 (adversarial review, cosmetic): one more _tornDown check right before the final
        // mutation this method performs — a concurrent TerminateAsync/OnDeactivateAsync could
        // have completed during any of the awaits above (the dead-backend sweep's terminate
        // calls, the toOpen fan-out, the toClose terminate calls). Bumping the cursor of an
        // already-torn-down activation's catalog is harmless (nothing is listening any more) but
        // pointless — tidy, not load-bearing.
        if (_tornDown)
            return;

        if (changed)
            BumpListChanged();
    }

    public async Task<string?> DispatchAsync(string jsonRpc)
    {
        JsonRpcMessage msg;
        try
        {
            msg = JsonRpcMessage.Parse(jsonRpc);
        }
        catch
        {
            return JsonRpcMessage.Error(null, -32700, "Parse error.");
        }

        try
        {
            if (msg.Method == "initialize")
                // SF-5(a): the pre-fix code returned the bare cached result with no jsonrpc/id
                // envelope — not a valid JSON-RPC response on its own. Wrap it under THIS
                // request's id (InitializeAsync's OWN direct return stays a bare result — Task
                // 7's HTTP responder wraps that one; this is DispatchAsync's separate re-ask
                // path, e.g. a client that calls initialize twice on the same session).
                return _cachedInitializeResultJson is null
                    ? JsonRpcMessage.Error(msg.Id, -32002, "not initialized")
                    : JsonRpcMessage.Result(msg.Id, _cachedInitializeResultJson);

            if (msg.Id is null)
                // Notification (e.g. notifications/initialized) or a stray response — 202, no body.
                return null;

            // SF-5(b): a JSON-RPC *response* (id present, no method) must not fall through to
            // "Method not found" below — treat it like a notification (202, no body).
            if (msg.IsResponse)
                return null;

            return msg.Method switch
            {
                "tools/list" => JsonRpcMessage.Result(msg.Id, _catalog.ToolsListJson()),
                // Task 6: awaiting a slow backend here never blocks another concurrent call on this
                // SAME session (e.g. another tools/call, or OnDeliveryAsync demuxing a DIFFERENT
                // backend's frame) — the grain is [Reentrant].
                "tools/call" => await HandleToolCallAsync(msg),
                _ => JsonRpcMessage.Error(msg.Id, -32601, "Method not found."),
            };
        }
        catch (Exception ex)
        {
            // SF-3: defense-in-depth backstop. The known-risky JsonNode extractions below each
            // have their own guard (HandleToolCallAsync's tool-name extraction,
            // HandleToolRouteAsync's backend error-code extraction, JsonRpcMessage.Method
            // itself) — this catches anything unanticipated a malformed/malicious client- or
            // backend-controlled payload could still trigger, so a single bad request returns a
            // normal JSON-RPC error instead of faulting the whole grain call.
            logger.LogWarning(ex, "Space-MCP: DispatchAsync failed unexpectedly method={Method}", msg.Method);
            return JsonRpcMessage.Error(msg.Id, -32603, "internal error");
        }
    }

    private async Task<string> HandleToolCallAsync(JsonRpcMessage msg)
    {
        // SF-3: defensive extraction — msg.Params["name"] could be a non-string JSON value from
        // a malformed/malicious client payload (e.g. {"name":123}); GetValue<string>() throws
        // InvalidOperationException in that case. Guard with `as JsonValue` + TryGetValue rather
        // than trusting the shape.
        var name = msg.Params?["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var n)
            ? n
            : null;
        if (string.IsNullOrEmpty(name))
            return JsonRpcMessage.Error(msg.Id, -32602, "missing tool name");

        if (!_catalog.TryResolve(name, out var route) || route is null)
            return JsonRpcMessage.Error(msg.Id, -32601, $"unknown tool: {name}");

        return route.Kind == RouteKind.Tool
            ? await HandleToolRouteAsync(msg, route)
            : await HandleRequestAccessRouteAsync(msg, route);
    }

    /// <summary>Routes a tools/call to the backend owning <paramref name="route"/>'s server and
    /// reframes its raw JSON-RPC response under the EXTERNAL client's own <c>id</c> — the
    /// backend's id space (SpaceBackendSession's own monotonic counter) is private and never
    /// leaks to the client. Port of BackendSessionManager.CallAsync's shape
    /// (apps/Korat.Cli/Mcp/Aggregation/BackendSessionManager.cs:412) + AggregatorMcpServer's
    /// HandleRealToolAsync reframing (apps/Korat.Cli/Mcp/Aggregation/AggregatorMcpServer.cs:173).</summary>
    private async Task<string> HandleToolRouteAsync(JsonRpcMessage msg, ToolRoute route)
    {
        if (route.ServerId is null || route.OriginalName is null)
            return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");

        // The catalog survives temporary relay loss. If there is no live backend, this call
        // revalidates the route and enters shared admission, including APNs wake for mobile.
        var backend = FindLiveBackend(route.ServerId)
            ?? await OpenBackendForToolCallAsync(route.ServerId);
        if (backend is null)
            return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");

        // DeepClone: msg.Params["arguments"] already has a parent (msg's own JSON tree) —
        // System.Text.Json.Nodes throws "The node already has a parent" if a node already
        // attached elsewhere is assigned directly into a new JsonObject.
        var arguments = (msg.Params?["arguments"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        var @params = new JsonObject { ["name"] = route.OriginalName, ["arguments"] = arguments };

        JsonRpcMessage backendResp;
        try
        {
            backendResp = await backend.SendRequestAsync(
                "tools/call", @params, CancellationToken.None, SpaceBackendSession.ToolCallTimeout);
        }
        catch (BackendRequestNotDeliveredException ex)
        {
            // ForwardFrameAsync proved the frame never reached the publisher, so retrying after a
            // fresh admission cannot duplicate a side effect. Timeouts and close-after-delivery
            // failures are deliberately not retried: the backend may already have executed them.
            logger.LogInformation(ex,
                "Space-MCP: tools/call frame not delivered; reopening serverId={ServerId} tool={Tool}",
                route.ServerId, route.OriginalName);
            EvictDeadBackendLocal(backend);

            backend = await OpenBackendForToolCallAsync(route.ServerId);
            if (backend is null)
                return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");

            try
            {
                // The first JsonRpcMessage.Request attached @params to its own JSON tree. Clone
                // before constructing the retry envelope; JsonNode instances cannot have two
                // parents even after the first envelope has been serialized.
                var retryParams = (JsonObject)@params.DeepClone();
                backendResp = await backend.SendRequestAsync(
                    "tools/call", retryParams, CancellationToken.None, SpaceBackendSession.ToolCallTimeout);
            }
            catch (Exception retryEx)
            {
                logger.LogWarning(retryEx,
                    "Space-MCP: tools/call failed after reopen serverId={ServerId} tool={Tool}",
                    route.ServerId, route.OriginalName);
                return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Space-MCP: tools/call failed serverId={ServerId} tool={Tool}",
                route.ServerId, route.OriginalName);
            return JsonRpcMessage.Error(msg.Id, -32000, "Server unavailable.");
        }

        JsonObject backendJson;
        try
        {
            backendJson = JsonNode.Parse(backendResp.Raw())!.AsObject();
        }
        catch
        {
            return JsonRpcMessage.Error(msg.Id, -32603, "malformed backend response");
        }

        if (backendJson["error"] is JsonObject err)
        {
            // SF-3: defensive extraction — a backend's "error.code" is untrusted wire data
            // (could be a string/bool/missing from a buggy or malicious backend); GetValue<int>()
            // throws in that case. Guard with `as JsonValue` + TryGetValue defaulting to -32603.
            var code = err["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var c) ? c : -32603;
            var message = err["message"]?.GetValue<string>() ?? "backend error";
            return JsonRpcMessage.Error(msg.Id, code, message);
        }

        if (backendJson["result"] is JsonNode result)
            return JsonRpcMessage.Result(msg.Id, result.ToJsonString());

        return JsonRpcMessage.Error(msg.Id, -32603, "malformed backend response");
    }

    /// <summary>
    /// Revalidates a cached route against the current Published/grant snapshot and lazily opens
    /// its backend. Shared admission remains the final fail-closed authority and repeats grant,
    /// server-assertion, and node-status checks after any wake wait.
    /// </summary>
    private async Task<SpaceBackendSession?> OpenBackendForToolCallAsync(string serverId)
    {
        if (_tornDown)
            return null;

        var existing = FindLiveBackend(serverId);
        if (existing is not null)
            return existing;

        IReadOnlyList<BackendServer> servers;
        try
        {
            using var cts = new CancellationTokenSource(PerBackendTimeout);
            servers = await SpaceServerDiscovery.DiscoverAsync(
                clusterClient, repository, _spaceId, _consumerIdentity, cts.Token);

            if (_tornDown)
                return null;

            var server = servers.FirstOrDefault(s => s.Id.Value == serverId && s.Granted);
            if (server is null)
            {
                // The cached route is no longer authorized/published. Remove it immediately
                // rather than waiting for reconcile; no admission or backend call is attempted.
                ReleaseReservedSlug(serverId);
                if (_catalog.RemoveGranted(serverId))
                    BumpListChanged();
                return null;
            }

            var ctx = new SpaceMcpSessionContext(_consumerIdentity, _spaceId, _owner);
            var backend = await EnsureBackendOpenAsync(server, ctx, cts.Token);
            if (backend is not null
                && !string.Equals(_catalog.ToolsListJson(), _announcedToolsListJson, StringComparison.Ordinal))
            {
                BumpListChanged();
            }
            return backend;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Space-MCP: lazy backend discovery failed serverId={ServerId}", serverId);
            return null;
        }
    }

    /// <summary>N1 (plan-review correction): <c>ISpaceGrain.CreateAccessRequestAsync</c> throws
    /// <c>KoratDomainException(AccessDenied)</c> when an active grant already exists
    /// (SpaceGrain.cs:466-467) — a legitimate race between this session's own tools/list snapshot
    /// (taken at InitializeAsync/the periodic re-discovery, Task 8) and an owner approving the
    /// SAME server in between. Caught here and turned into a normal tool-result telling the
    /// caller access is already granted, never a raw 500-shaped JSON-RPC error.</summary>
    private async Task<string> HandleRequestAccessRouteAsync(JsonRpcMessage msg, ToolRoute route)
    {
        var serverId = new McpServerId(route.ServerId!);
        try
        {
            var accessRequest = await clusterClient.GetGrain<ISpaceGrain>(_spaceId.Value)
                .CreateAccessRequestAsync(_consumerIdentity, serverId, SessionAdmission.AggregatorSentinelNodeId);
            return ToolTextResult(msg.Id,
                $"Access request created ({accessRequest.Id.Value}); the Space owner must approve it.");
        }
        catch (KoratDomainException ex) when (ex.Code == KoratErrorCode.AccessDenied)
        {
            return ToolTextResult(msg.Id,
                $"Access to '{route.Slug}' is already granted; its tools should appear on the next tools/list.");
        }
    }

    private static string ToolTextResult(JsonNode? id, string text)
    {
        var content = new JsonArray();
        // Cast to JsonNode so the non-generic JsonArray.Add(JsonNode?) overload is bound rather
        // than the RequiresUnreferencedCode generic Add<T> (mirrors AggregateCatalog.Rebuild).
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        var payload = new JsonObject { ["content"] = content };
        return JsonRpcMessage.Result(id, payload.ToJsonString());
    }

    /// <summary>Task 8: long-poll primitive backing the dispatcher's GET-SSE loop. Fast path —
    /// <see cref="_listChangedCursor"/> already moved past <paramref name="knownCursor"/> (a bump
    /// landed since the caller's last look, e.g. while it was writing the previous SSE event or
    /// re-checking the binding) — returns immediately, no waiting. Slow path — captures the
    /// CURRENT <see cref="_cursorBump"/> and awaits it bounded by <see cref="ListChangedHeartbeat"/>
    /// (well under Orleans' 30s response timeout, N2): a bump completes it early; otherwise the
    /// bounded wait times out and this returns the cursor UNCHANGED, letting the dispatcher's loop
    /// keep the SSE connection alive without emitting a notification. NEVER throws — a heartbeat
    /// timeout is the expected, common case, not a failure.</summary>
    public async Task<long> NextListChangedAsync(long knownCursor)
    {
        if (_listChangedCursor > knownCursor)
            return _listChangedCursor;

        var bump = _cursorBump;
        try
        {
            await bump.Task.WaitAsync(ListChangedHeartbeat);
        }
        catch (TimeoutException)
        {
            // Heartbeat elapsed with no bump — fall through and return the cursor unchanged.
        }
        // Re-read _listChangedCursor fresh rather than assuming "bump completed => it moved
        // exactly once" — by the time this continuation actually runs (RunContinuationsAsynchronously
        // queues it rather than running it inline off BumpListChanged's own turn), any number of
        // further bumps may already have landed; the caller only cares that this is the LATEST
        // value, not how many bumps produced it.
        return _listChangedCursor;
    }

    /// <summary>Task 8: increments <see cref="_listChangedCursor"/> and wakes every caller
    /// currently blocked in <see cref="NextListChangedAsync"/>'s slow path. Swaps
    /// <see cref="_cursorBump"/> to a FRESH <c>TaskCompletionSource</c> BEFORE completing the old
    /// one: this grain's Orleans activation runs strictly one turn at a time (reentrancy lets a
    /// NEW call start while an earlier one is suspended at an await — it never runs this
    /// method's own synchronous body concurrently with itself), so by the time
    /// <c>old.TrySetResult()</c> below can possibly hand control to anything else, <see cref="_cursorBump"/>
    /// already points at the fresh instance. That ordering matters for one reason: a caller that
    /// enters <see cref="NextListChangedAsync"/> AFTER this swap must capture the FRESH tcs (and
    /// correctly wait for the NEXT bump), never the one that just fired — swapping first
    /// guarantees that. A waiter that captured the OLD tcs before this call runs is never lost
    /// either way: its continuation (queued, not inlined) always re-reads
    /// <see cref="_listChangedCursor"/> fresh rather than trusting a value closed over at capture
    /// time, so it observes this bump (and any further ones that land before it actually resumes).</summary>
    private void BumpListChanged()
    {
        _announcedToolsListJson = _catalog.ToolsListJson();
        _listChangedCursor++;
        var old = _cursorBump;
        _cursorBump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    public async Task TerminateAsync()
    {
        // MUST-FIX F1 (adversarial review, second pass, BLOCKER): flip this BEFORE anything else,
        // including any await — see _tornDown's own doc comment for the teardown-vs-fanout race
        // this closes (OpenBackendAsync's two checkpoints gate on it).
        _tornDown = true;

        // N7 (adversarial review, third pass): wake any GET-SSE stream already blocked in
        // NextListChangedAsync's long-poll RIGHT NOW, rather than making it wait out up to a full
        // ListChangedHeartbeat before its next binding re-check notices GetBindingAsync now
        // returns null (see GetBindingAsync's own _tornDown gate below). A DELETE should close an
        // open watch stream at once, not up to ~15s later.
        BumpListChanged();

        // Task 8: dispose the backstop reconcile timer synchronously, up front — Dispose() is
        // non-blocking (it just deregisters the timer with the runtime), so there is no reason to
        // delay it past any of the awaits below. Prevents a leaked timer from firing again after
        // this activation has already started tearing down (it would no-op on _tornDown anyway —
        // see ReconcileTimerCallbackAsync's own guard — but disposing it outright is cleaner than
        // relying on that guard alone).
        _reconcileTimer?.Dispose();
        _reconcileTimer = null;

        // MUST-FIX 1 (S4 BLOCKER rework): the pre-fix code called ONLY backend.OnClosed(...),
        // which is purely cosmetic — it flips this grain's own local _isAlive/faults its
        // in-flight TCS but never terminates the PUBLISHER-side relay session, leaking a live
        // publisher-side MCP subprocess on every DELETE. Terminate each opened backend's relay
        // session FIRST (each call is itself best-effort/never-throwing internally, but the loop
        // is defensively wrapped too so one backend's DB/routing failure never skips the rest),
        // THEN do the existing local-only teardown (OnClosed/clear + unregister the delivery leg).
        // Snapshot (.ToList) before awaiting inside the loop: this grain is [Reentrant], so the
        // TerminateSessionAsync await below yields the turn — a queued OnDeliveryAsync close-path
        // (which Removes from _backendsBySessionId) can run and mutate the dictionary mid-loop,
        // which would throw "Collection was modified" on the next MoveNext over a live .Values.
        //
        // S1 (whole-feature adversarial review): DELIBERATELY inline/sequential, unlike
        // EvictDeadBackendLocal/TerminateBackendBestEffortAsync's fire-and-forget treatment of the
        // SAME terminator.TerminateSessionAsync self-notify loop-back (see that method's own doc
        // comment for the ~30s-per-call stall it measured). The difference is WHERE this loop runs
        // from: EvictDeadBackendLocal is called from OnDeliveryAsync (and ReconcileAsync's own
        // eviction), i.e. from INSIDE an already-in-flight frame delivery — the stall it measured
        // is a genuine self-notify deadlock through that in-flight call's own state. TerminateAsync
        // is a fresh top-level grain call (dispatched by the HTTP responder's DELETE handler, not
        // from within a frame delivery), so the nested OnDeliveryAsync this loop triggers per
        // backend has no in-flight call of its own to contend with. Empirically verified in
        // SpaceMcpTeardownLatencyTests (5 granted backends): TerminateAsync completes in ~tens of
        // milliseconds, not ~30s×N — do NOT "fix" this into fire-and-forget without re-measuring;
        // it would trade a real F1 teardown guarantee (publisher relay session actually terminated
        // before TerminateAsync returns) for no measured benefit.
        foreach (var backend in _backendsBySessionId.Values.ToList())
        {
            try
            {
                await terminator.TerminateSessionAsync(
                    new SessionId(backend.SessionId), SessionCloseReason.Completed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Space-MCP: TerminateAsync failed to terminate backend relay session sessionId={SessionId}",
                    backend.SessionId);
            }
        }

        foreach (var backend in _backendsBySessionId.Values)
            backend.OnClosed("terminated");
        _backendsBySessionId.Clear();
        _backendsBySlug.Clear();
        _takenSlugs.Clear(); // N-d: this activation is fully torn down — no live backends remain.
        _reservedSlugsByServerId.Clear();

        if (_initialized)
        {
            await routingTable.UnregisterAgentStreamAsync(_syntheticConn);

            // Task 7 (inc-2a): drop this session from the consumer index (best-effort — teardown
            // must never be blocked or faulted by a registry hiccup; the registry itself is
            // volatile and self-heals on next silo restart even if this never runs).
            try
            {
                await GrainFactory.GetGrain<ISpaceMcpConsumerSessionsGrain>(_consumerIdentity.Value)
                    .UnregisterAsync(this.GetPrimaryKeyString());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Space-MCP: failed to unregister session from consumer index");
            }

            _initialized = false;
        }

        DeactivateOnIdle();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // MUST-FIX F1 (adversarial review, second pass, BLOCKER): flip this BEFORE anything else
        // — see _tornDown's own doc comment. OnDeactivateAsync is a perfectly legitimate path for
        // a backend fan-out to still be resuming through when the client abandons a session
        // (never sends DELETE) instead of calling TerminateAsync; OpenBackendAsync's checkpoints
        // (a)/(b) gate on this flag too, not only TerminateAsync's own.
        _tornDown = true;

        // Task 8: same reasoning as TerminateAsync — dispose the backstop reconcile timer up
        // front so an abandoned-session deactivation (no DELETE ever sent) cannot leave a timer
        // running against a deactivating/deactivated activation.
        _reconcileTimer?.Dispose();
        _reconcileTimer = null;

        // Plan-review correction S4 + MUST-FIX 1 rework: an abandoned session (client never
        // sends DELETE) must not leak backend relay sessions/routing-table entries past this
        // activation's own deactivation. Order deliberately mirrors NodeGatewayService's own
        // agent-teardown (:430-459): unregister the delivery leg FIRST, then terminate each
        // backend's relay session — the OPPOSITE order from TerminateAsync (which terminates
        // first). Deactivation is an involuntary/best-effort path that must never throw;
        // unregistering the leg up front means a slow/failing terminate call below can never
        // race a fresh OnDeliveryAsync re-entering this same (deactivating) activation.
        if (_initialized)
        {
            await routingTable.UnregisterAgentStreamAsync(_syntheticConn);

            // Task 7 (inc-2a): drop this session from the consumer index (best-effort — teardown
            // must never be blocked or faulted by a registry hiccup; the registry itself is
            // volatile and self-heals on next silo restart even if this never runs).
            try
            {
                await GrainFactory.GetGrain<ISpaceMcpConsumerSessionsGrain>(_consumerIdentity.Value)
                    .UnregisterAsync(this.GetPrimaryKeyString());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Space-MCP: failed to unregister session from consumer index");
            }

            _initialized = false;
        }

        // MUST-FIX F1 part 3 rework (adversarial review, second pass): terminate whatever remains
        // in _backendsBySessionId UNCONDITIONALLY — no longer gated on `if (_initialized)` above.
        // Only the delivery-leg unregister stays gated on _initialized (unregistering twice would
        // be wrong); termination itself must run even when _initialized was already false by the
        // time we got here (e.g. TerminateAsync already ran once on this activation, and a
        // late-resuming OpenBackendAsync re-added an entry afterward — checkpoints (a)/(b) are the
        // PRIMARY fix for that specific race, but this loop is the backstop for any entry that
        // reaches here regardless of how). SessionCloseReason.Abandoned (not Completed) — this is
        // the involuntary/no-DELETE path, source-agnostic ghost reconciliation, same semantics as
        // the session reaper's own Abandoned close.
        // Snapshot (.ToList) before awaiting inside the loop — same [Reentrant] hazard as
        // TerminateAsync: a queued OnDeliveryAsync close-path Removing from _backendsBySessionId
        // during the await below would otherwise throw "Collection was modified" mid-enumeration.
        // (Unregistering the leg above narrows but does not eliminate already-queued deliveries.)
        //
        // S1 (whole-feature adversarial review): same reasoning as TerminateAsync's own loop for
        // why this stays inline/sequential rather than fire-and-forget like
        // EvictDeadBackendLocal/TerminateBackendBestEffortAsync — see TerminateAsync's doc comment
        // above. OnDeactivateAsync, like TerminateAsync, is a top-level grain-lifecycle call, never
        // invoked from inside an in-flight frame delivery holding a connection lock, so the nested
        // OnDeliveryAsync each TerminateSessionAsync call triggers has nothing to contend with.
        // SpaceMcpTeardownLatencyTests measures TerminateAsync's identical code shape at ~tens of
        // milliseconds for 5 backends, not ~30s×N.
        foreach (var backend in _backendsBySessionId.Values.ToList())
        {
            try
            {
                await terminator.TerminateSessionAsync(
                    new SessionId(backend.SessionId), SessionCloseReason.Abandoned, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Space-MCP: OnDeactivateAsync failed to terminate backend relay session sessionId={SessionId}",
                    backend.SessionId);
            }
            backend.OnClosed("deactivated");
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    // Task 7 (SF-5/S4): a torn-down activation must present as "no such session" to the HTTP
    // responder's re-validation, not as a still-live binding — the responder's DELETE handler
    // and OnDeactivateAsync both flip _tornDown synchronously (see its own doc comment), but
    // DeactivateOnIdle() only SCHEDULES the actual Orleans deactivation; a request racing in
    // immediately after TerminateAsync returns could otherwise still hit this SAME activation
    // instance with _binding untouched and be wrongly treated as a live session. Gating on
    // _tornDown here makes "DELETE then POST -> 404" hold regardless of Orleans' deactivation
    // timing, without needing TerminateAsync/OnDeactivateAsync themselves to mutate _binding.
    public Task<SpaceMcpBinding?> GetBindingAsync() => Task.FromResult(_tornDown ? null : _binding);

    public async Task OnDeliveryAsync(string backendSessionId, byte[] payload, uint enc, string? closeReason)
    {
        if (!_backendsBySessionId.TryGetValue(backendSessionId, out var backend))
            // Unknown/already-evicted backend session — nothing to demux to. Not an error: a
            // close event can race this grain's own eviction of the same session.
            return;

        if (closeReason is not null)
        {
            // MUST-FIX 1: do NOT call terminator.TerminateSessionAsync here. This event means
            // the PUBLISHER end already sent CloseSession (or the routing table itself detected
            // the session is gone, e.g. PayloadLimitExceeded) — that side is already tearing
            // itself down. Re-terminating from here would be redundant (a second, unnecessary
            // repository read + grain CloseAsync call for an already-closing/closed session).
            // Only this grain's LOCAL bookkeeping is evicted.
            backend.OnClosed(closeReason);
            _backendsBySessionId.Remove(backendSessionId);
            _backendsBySlug.Remove(backend.Slug);
            // A relay session closing does not normally change the server's tool definitions.
            // Keep them cached so a later tools/call can wake/reopen the publisher. Revocation
            // and explicit disable are authorization/catalog changes, so those still evict and
            // synchronously wake list_changed listeners.
            if (ShouldInvalidateCatalog(closeReason))
            {
                ReleaseReservedSlug(backend.ServerId);
                if (_catalog.RemoveGranted(backend.ServerId))
                    BumpListChanged();
            }
            return;
        }

        await backend.OnInboundBytesAsync(payload, enc, CancellationToken.None);

        // MUST-FIX 3 part 1 (adversarial review, third pass): OnInboundBytesAsync's own enc≠0
        // fail-closed guard (SpaceBackendSession.cs, N3) — and any other future path that marks
        // the backend dead WITHOUT ever sending a CloseSession event — never reaches the
        // close-path eviction above. Left indexed, this backend would zombie forever and prevent
        // the lazy reopen path from running. Evict the dead live-session state immediately while
        // retaining its last known catalog.
        if (!backend.IsAlive)
            EvictDeadBackendLocal(backend);
    }

    private static bool ShouldInvalidateCatalog(string closeReason) =>
        Enum.TryParse<SessionCloseReason>(closeReason, ignoreCase: true, out var reason)
        && reason is SessionCloseReason.Revoked or SessionCloseReason.ServerDisabled;

    /// <summary>MUST-FIX 3 (adversarial review, third pass): shared LOCAL eviction for a backend
    /// THIS grain already knows is dead but that never reached <see cref="OnDeliveryAsync"/>'s
    /// synchronous CloseSession-eviction — either <see cref="SpaceBackendSession.OnInboundBytesAsync"/>'s
    /// own <c>enc≠0</c> fail-closed guard, or an undeliverable <c>SendLineAsync</c> mid
    /// <c>tools/call</c> (both flip <see cref="SpaceBackendSession.IsAlive"/> to <c>false</c>
    /// without ever routing an event back through this grain's own close-path).
    ///
    /// Deliberately SYNCHRONOUS (no <c>await</c> at all) — an empirical finding while writing this
    /// fix's own test (<c>SpaceMcpDeadBackendReconcileTests</c>): every backend session this grain
    /// opens has its "agent" side bound to THIS SAME activation's own synthetic
    /// <c>ConnectionId</c>, so <c>terminator.TerminateSessionAsync</c> for one of them always loops
    /// back through <c>SendToConnectionAsync</c> → <c>CallbackServerStreamWriter</c> → a FRESH
    /// nested <see cref="OnDeliveryAsync"/> call on THIS SAME activation. Awaiting that call INLINE
    /// from a method (<see cref="OnDeliveryAsync"/> itself, or a reconcile tick) that is ALREADY
    /// executing on this activation measured out at a consistent ~30s stall before proceeding —
    /// exactly Orleans' un-overridden default response timeout — rather than the near-instant
    /// local bookkeeping this was supposed to be. Splitting local eviction (this method, always
    /// safe and fast) from the relay-session termination (<see cref="TerminateBackendBestEffortAsync"/>,
    /// fired-and-forgotten below) means every live-session dictionary update happens immediately
    /// regardless of how long that round trip actually takes.
    ///
    /// Re-checks identity (not just presence) before mutating — same discipline as
    /// <see cref="ReconcileAsync"/>'s own "no longer granted" eviction loop. Provably redundant
    /// now that this method has no <c>await</c> of its own (Orleans cannot switch this activation's
    /// turn mid-method without one), but kept as defense-in-depth against a future edit
    /// reintroducing an await here without noticing the invariant this comment documents.</summary>
    private void EvictDeadBackendLocal(SpaceBackendSession backend)
    {
        if (_backendsBySlug.TryGetValue(backend.Slug, out var current) && ReferenceEquals(current, backend))
        {
            _backendsBySlug.Remove(backend.Slug);
            _backendsBySessionId.Remove(backend.SessionId);
        }

        // Best-effort terminate the relay session — fired-and-forgotten (NOT awaited): unlike the
        // OnDeliveryAsync close-path above (which deliberately skips terminating — the publisher
        // side already sent CloseSession itself, so it is already tearing down), whatever flipped
        // this backend's IsAlive here was NOT an inbound CloseSession, so the publisher/DB side may
        // still consider the session Active and this call is the only thing that will ever tell it
        // otherwise. See this method's own doc comment for why it must not be awaited inline.
        _ = TerminateBackendBestEffortAsync(backend, SessionCloseReason.ServerUnavailable);
    }

    /// <summary>The fire-and-forget half of <see cref="EvictDeadBackendLocal"/> (and, since the
    /// same self-notify hazard applies equally there, <see cref="ReconcileAsync"/>'s own "no
    /// longer granted" eviction loop) — see <see cref="EvictDeadBackendLocal"/>'s doc comment for
    /// why this must never be awaited from inside a call already executing on this activation.
    /// Never throws (catches and logs internally), so there is nothing for a caller to observe
    /// even if it wanted to.</summary>
    private async Task TerminateBackendBestEffortAsync(SpaceBackendSession backend, SessionCloseReason reason)
    {
        try
        {
            await terminator.TerminateSessionAsync(new SessionId(backend.SessionId), reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Space-MCP: failed to terminate backend relay session serverId={ServerId} reason={Reason}",
                backend.ServerId, reason);
        }
    }

    private static string ExtractEchoProtocolVersion(string clientInitializeJson)
    {
        try
        {
            var msg = JsonRpcMessage.Parse(clientInitializeJson);
            var requested = msg.Params?["protocolVersion"]?.GetValue<string>();
            // N4: echo the client's requested protocolVersion when it is one we support, rather
            // than hard-pinning our own preferred version regardless of what was asked.
            return requested is not null && SupportedProtocolVersions.Contains(requested)
                ? requested
                : DefaultProtocolVersion;
        }
        catch
        {
            return DefaultProtocolVersion;
        }
    }

    private static string BuildInitializeResultJson(string protocolVersion) => new JsonObject
    {
        ["protocolVersion"] = protocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
        ["serverInfo"] = new JsonObject { ["name"] = "korat-space", ["version"] = "1" },
    }.ToJsonString();
}
