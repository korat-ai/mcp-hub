using System.CommandLine;
using System.Text.Json;
using Grpc.Core;
using Korat.Cli.Auth;
using Korat.Cli.Config;
using Korat.Cli.Gateway;
using Korat.Cli.Mcp;
using Korat.Cli.Service;
using Korat.Cli.Util;
using Korat.Relay.V1;

namespace Korat.Cli.Commands;

/// <summary>
/// <c>korat service</c> — manages the always-on node service daemon.
///
/// Subcommands:
///   <c>run</c>       — daemon entrypoint; invoked by the OS unit (launchd/systemd).
///   <c>install</c>   — write + load an OS service unit that runs <c>korat service run</c>
///                       at login and restarts on crash.
///   <c>uninstall</c> — stop, unload, and delete the OS unit.
///   <c>status</c>    — report whether the unit is installed and running.
/// </summary>
public static class ServiceCommand
{
    public static Command Create()
    {
        var service = new Command("service", "Manage the Korat publisher runtime daemon");
        service.AddCommand(CreateRunCommand());
        service.AddCommand(CreateInstallCommand());
        service.AddCommand(CreateUninstallCommand());
        service.AddCommand(CreateReinstallCommand());
        service.AddCommand(CreateStatusCommand());
        return service;
    }

    private static Command CreateRunCommand()
    {
        var run = new Command("run", "Start the publisher runtime daemon (invoked by the OS unit)");
        run.SetHandler(RunDaemonAsync);
        return run;
    }

    private static Command CreateInstallCommand()
    {
        var install = new Command("install",
            "Install the Korat publisher runtime so it starts automatically at login");
        install.SetHandler(InstallAsync);
        return install;
    }

    private static Command CreateUninstallCommand()
    {
        var uninstall = new Command("uninstall",
            "Stop and remove the Korat publisher runtime OS unit");
        uninstall.SetHandler(UninstallAsync);
        return uninstall;
    }

    private static Command CreateReinstallCommand()
    {
        var reinstall = new Command("reinstall",
            "Uninstall (if present) then install the Korat publisher runtime OS unit");
        reinstall.SetHandler(ReinstallAsync);
        return reinstall;
    }

    private static Command CreateStatusCommand()
    {
        var status = new Command("status",
            "Show whether the Korat publisher runtime is installed and running");
        status.SetHandler(StatusAsync);
        return status;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // install / uninstall / status
    // ─────────────────────────────────────────────────────────────────────────

    // internal so `mcp list` can query the local daemon's running state for its 💻 status leg.
    internal static IServiceController? TryGetController()
    {
        if (OperatingSystem.IsMacOS()) return new LaunchdController();
        if (OperatingSystem.IsLinux()) return new SystemdController();
        if (OperatingSystem.IsWindows()) return new ScheduledTaskController();
        return null;
    }

    private static async Task InstallAsync()
    {
        var ctrl = TryGetController();
        if (ctrl is null)
        {
            Console.Error.WriteLine("OS service management is not supported on this platform.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            await ctrl.InstallAsync();
            Console.WriteLine();
            Console.WriteLine("Korat publisher runtime installed successfully.");
            Console.WriteLine("Run `korat service status` to verify it is running.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Install failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task UninstallAsync()
    {
        var ctrl = TryGetController();
        if (ctrl is null)
        {
            Console.Error.WriteLine("OS service management is not supported on this platform.");
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            await ctrl.UninstallAsync();
            Console.WriteLine("Korat publisher runtime uninstalled.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task ReinstallAsync()
    {
        var ctrl = TryGetController();
        if (ctrl is null)
        {
            Console.Error.WriteLine("OS service management is not supported on this platform.");
            Environment.ExitCode = 1;
            return;
        }

        await ReinstallWithControllerAsync(ctrl);
    }

    /// <summary>
    /// Core reinstall logic: uninstall (ignore absent), then install.
    /// Extracted as an internal method so tests can inject a stub controller.
    /// </summary>
    internal static async Task ReinstallWithControllerAsync(IServiceController ctrl)
    {
        // Uninstall (best-effort — ignore "not installed" / "not found" errors).
        try
        {
            await ctrl.UninstallAsync();
        }
        catch (Exception ex)
        {
            // Not installed is fine; surface anything genuinely unexpected.
            Console.WriteLine($"Uninstall step: {ex.Message} (continuing)");
        }

        try
        {
            await ctrl.InstallAsync();
            Console.WriteLine();
            Console.WriteLine("Korat publisher runtime reinstalled successfully.");
            Console.WriteLine("Run `korat service status` to verify it is running.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Install step failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task StatusAsync()
    {
        var ctrl = TryGetController();
        if (ctrl is null)
        {
            Console.WriteLine("OS service management is not supported on this platform.");
            return;
        }

        ServiceStatus status;
        try
        {
            status = await ctrl.GetStatusAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Status check failed: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Installed : {status.IsInstalled}");
        Console.WriteLine($"Running   : {status.IsRunning}");
        if (status.Detail is not null)
            Console.WriteLine($"Detail    : {status.Detail}");

        // Show locally-registered MCP server count.
        var identityStore = new LocalIdentityStore();
        var identity = identityStore.LoadOrCreate();
        Console.WriteLine($"Servers   : {identity.McpServers.Count} registered locally");
        foreach (var s in identity.McpServers)
            Console.WriteLine($"            - {s.DisplayName}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Daemon entry-point
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);

    private static async Task RunDaemonAsync()
    {
        // ── Credentials & identity ──────────────────────────────────────────
        var credStore = new CredentialStore();
        var cliCreds = await credStore.LoadAsync();
        if (cliCreds is null)
        {
            Console.Error.WriteLine("[service] Not authenticated. Run `korat login` first.");
            Environment.ExitCode = 1;
            return;
        }

        var identityStore = new LocalIdentityStore();
        var identity = identityStore.LoadOrCreate();
        if (string.IsNullOrWhiteSpace(identity.CloudUrl))
        {
            Console.Error.WriteLine("[service] Cloud URL is missing. Run `korat login` first.");
            Environment.ExitCode = 1;
            return;
        }

        // ── Cancellation (SIGINT/SIGTERM) ────────────────────────────────────
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        // On Unix, SIGTERM (e.g. launchd/systemd stopping the service) is raised as
        // AppDomain.ProcessExit. Guard against the disposal race: on a clean Ctrl+C exit
        // `using var cts` may already be disposed when ProcessExit fires, and Cancel() would
        // then throw ObjectDisposedException → the process aborts with a stack trace.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
        };

        var nodeName = Environment.MachineName;

        // Determine config.json path for the watcher once (path does not change across reconnects).
        var configPath = KoratConfigPaths.FindExistingConfigPath()
            ?? KoratConfigPaths.GetWritePath();

        // ── Outer reconnect loop (Bug 2) ─────────────────────────────────────
        // On stream loss (cloud restart, network blip) we back off and reconnect
        // rather than leaving the daemon alive but stuck Offline. Exit ONLY on
        // real cancellation (SIGINT/SIGTERM). The backoff resets after a connection
        // that was live for at least 60 s (normal cloud restart scenario).
        var backoff = new ReconnectBackoff();

        while (!cts.IsCancellationRequested)
        {
            // Пропуск перечитывается на каждой попытке, а не берётся снимком со старта.
            // Демон живёт неделями, а пропуск провайдера — часы: со снимком служба после
            // первого же истечения переподключалась бы с мёртвым пропуском и оставалась
            // офлайн до перезапуска руками. Чтение заодно обновляет истёкший
            // (см. CredentialStore.LoadAsync).
            cliCreds = await credStore.LoadAsync(cts.Token) ?? cliCreds;

            // ── gRPC connection ──────────────────────────────────────────────
            NodeGatewayConnection connection;
            try
            {
                connection = await NodeGatewayConnection.ConnectAsync(
                    identity, nodeName, cts.Token, cliCreds, nodeKind: "publisher");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = backoff.OnDisconnect();
                Console.Error.WriteLine(
                    $"[service] Could not reach Korat cloud at {identity.CloudUrl}: {ex.Message} — retrying in {delay.TotalSeconds:F0}s");
                try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }

            Korat.Cli.Util.UpgradeNotice.MaybeWarn(connection.GatewayHello.CurrentCliVersion);
            // fix/default-space-placeholder: persist the server-authoritative SpaceId on first
            // successful connect so the client no longer stores the "default" placeholder.
            identityStore.PersistResolvedSpaceId(identity, connection.GatewayHello.ResolvedSpaceId);
            Console.WriteLine(
                $"[service] Publisher runtime {identity.NodeId} online -> {identity.CloudUrl}");
            Console.WriteLine(
                $"[service] {identity.McpServers.Count} server(s) registered locally.");

            bool lostConnection = false;
            try
            {
                await using (connection)
                {
                    // ── Initial routing map (empty — filled after acks) ──────────────
                    var emptyMap = new Dictionary<string, McpServerSpec>();
                    await using var bridge = new SessionBridge(connection, emptyMap);
                    var host = new NodeServiceHost(connection, bridge, identity.NodeId);

                    // ── 021 (Layer 1): declarative sync on (re)connect (#13 ghost fix) ──────
                    // Replace the old PublishMcpServer-per-server bootstrap with ONE
                    // SyncMcpServers carrying the full set. The cloud upserts all declared
                    // servers and soft-retires any server it currently holds for this node
                    // that is absent from the set — so reconnects can never produce "ghost"
                    // servers left over from a prior connection whose _publishedByName was lost.
                    //
                    // Transient-empty guard: we ONLY send SyncMcpServers when we have an
                    // authoritative config read (parsed, well-formed JSON from a non-empty file).
                    // A failed read (IO error, JSON parse error, zero-length file) → skip sync
                    // entirely, leaving the prior cloud state intact. An authoritatively-empty
                    // set (config parsed fine, McpServers genuinely []) IS sent — it legitimately
                    // retires the node's servers. This guard prevents a transient read during an
                    // atomic-save rename from being misinterpreted as "owner removed all servers".
                    List<GatewayToNodeMessage>? buffered = null;
                    try
                    {
                        // Authoritative config read: try to read and parse config.json directly.
                        // We do NOT use LocalIdentityStore.LoadOrCreate() here because on parse
                        // error it mints a fresh identity (empty McpServers) that is
                        // indistinguishable from a genuine authoritative-empty config — which
                        // would silently retire all cloud servers. Instead we attempt the raw
                        // read and only proceed with sync when it fully succeeds.
                        IReadOnlyList<LocalMcpServer> serversForSync;
                        // 079 (BLOCKING fix): the inference sync is DESTRUCTIVE (hard-deletes
                        // orphans), so its declared set MUST come from the same authoritative raw
                        // parse as serversForSync — NOT the startup `identity` snapshot (loaded once
                        // outside this reconnect loop, hence stale after an in-session `agent add`).
                        // Tying it to `parsed` makes the syncAuthoritative guard genuinely cover it.
                        IReadOnlyList<InferencePointIdentity> pointsForSync;
                        bool syncAuthoritative;
                        try
                        {
                            // Require the file to exist and be non-empty before parsing.
                            // A zero-length file (e.g. mid-atomic-write) is treated as a failed read.
                            var fi = new FileInfo(configPath);
                            if (!fi.Exists || fi.Length == 0)
                                throw new InvalidOperationException("config file missing or empty");

                            var json = File.ReadAllText(configPath);
                            var parsed = JsonSerializer.Deserialize(json, KoratCliJsonContext.Default.LocalIdentity);
                            if (parsed is null)
                                throw new InvalidOperationException("config parsed as null");

                            serversForSync = parsed.McpServers;
                            pointsForSync = parsed.InferencePoints;
                            syncAuthoritative = true;
                        }
                        catch (Exception readEx)
                        {
                            // Failed read — skip sync entirely; leave prior cloud state intact.
                            Console.Error.WriteLine(
                                $"[service] sync skipped — config unreadable: {readEx.Message}");
                            serversForSync = Array.Empty<LocalMcpServer>();
                            pointsForSync = Array.Empty<InferencePointIdentity>();
                            syncAuthoritative = false;
                        }

                        if (syncAuthoritative)
                        {
                            buffered = new List<GatewayToNodeMessage>(
                                await host.SyncAllAsync(
                                    serversForSync,
                                    connection.IncomingMessages,
                                    cts.Token));
                        }
                        else
                        {
                            // Guard fired — no sync sent. Routing map stays empty for this
                            // connection; ReconcileAsync will populate it on the next
                            // ConfigWatcher fire once the file is readable again.
                            buffered = new List<GatewayToNodeMessage>();
                        }

                        // 029 + 079: Publish and sync inference points on (re)connect.
                        // Best-effort: a failure here does NOT treat the connection as lost —
                        // the MCP relay is already functional; inference simply won't be available
                        // until the next reconnect or a manual `korat agent add`.
                        //
                        // 079: SyncAllInferencePointsAsync fires FIRST (before PublishAllInferencePointsAsync).
                        // It sends SyncInferencePoints which HARD-DELETES any orphan cloud point whose
                        // agent_name is absent from the current config set — closing the offline-remove gap.
                        // It also seeds _publishedPoints from the declared set so ReconcileAsync's diff
                        // sees a clean baseline. Fire-and-forget (no ack), so it does not block the
                        // publish path below.

                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[service] Failed to sync MCP servers: {ex.Message}");
                        // C1: treat sync failure as connection-loss for this iteration —
                        // do NOT return (which would kill the daemon). The serve block below
                        // is guarded by `if (!lostConnection)` so we skip it and fall through
                        // to the bottom-of-loop backoff so the outer while reconnects.
                        lostConnection = true;
                    }

                    if (!lostConnection)
                    {
                        // H2: OnConnected only after publish succeeds — the connection is
                        // genuinely serving at this point. Calling it before publish would
                        // wrongly reset backoff on a connect-then-publish-fail hot-loop.
                        backoff.OnConnected();

                        // ── Config watcher (phase 2: live reconcile) ──────────────────────
                        // Reconcile requests are serialized via a channel so they run on the same
                        // task that owns the bridge, avoiding concurrent mutations.
                        var reconcileChannel = System.Threading.Channels.Channel.CreateUnbounded<LocalIdentity>(
                            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

                        using var configWatcher = new ConfigWatcher(configPath);
                        configWatcher.Changed += newIdentity =>
                        {
                            Console.WriteLine("[service] config.json changed — scheduling reconcile.");
                            reconcileChannel.Writer.TryWrite(newIdentity);
                        };

                        // C2: per-iteration linked CTS so frame/heartbeat tasks can be
                        // cancelled independently of the outer cts on stream loss.
                        // The shared cts must remain uncancelled so the outer loop reconnects;
                        // only iterCts is cancelled at the end of each iteration.
                        using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                        // ── Frame dispatch + reconcile loop ───────────────────────────────
                        var frameTask = Task.Run(() =>
                            FrameDispatchLoopAsync(connection, bridge, host, identity, buffered!, reconcileChannel.Reader, iterCts.Token));

                        var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(connection, iterCts.Token));

                        try
                        {
                            await Task.WhenAny(heartbeatTask, frameTask);
                        }
                        finally
                        {
                            // C2: cancel the loser task (stops it immediately rather than
                            // blocking up to 25 s) without cancelling the outer cts.
                            iterCts.Cancel();
                            reconcileChannel.Writer.TryComplete();
                            try { await frameTask; } catch { /* swallow on shutdown */ }
                            try { await heartbeatTask; } catch { /* swallow on shutdown */ }
                        }

                        // If cancelled cleanly, exit the outer loop.
                        if (cts.IsCancellationRequested)
                            break;

                        // Otherwise the stream died — mark for reconnect.
                        lostConnection = true;
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[service] Connection lost: {ex.Message}");
                lostConnection = true;
            }

            if (!lostConnection || cts.IsCancellationRequested)
                break;

            var retryDelay = backoff.OnDisconnect();
            Console.Error.WriteLine(
                $"[service] Disconnected from cloud — reconnecting in {retryDelay.TotalSeconds:F0}s");
            try { await Task.Delay(retryDelay, cts.Token); } catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("[service] Publisher runtime stopped.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Heartbeat loop (identical pattern to UpCommand)
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task HeartbeatLoopAsync(
        NodeGatewayConnection connection,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, ct);
                await connection.SendHeartbeatAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (GatewayDisconnectedException)
            {
                Console.Error.WriteLine("[service] Lost connection to cloud gateway — exiting.");
                Environment.ExitCode = 1;
                return;
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine(
                    $"[service] Lost connection to cloud gateway: {ex.Status.Detail}");
                Environment.ExitCode = 1;
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Frame dispatch + reconcile loop
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task FrameDispatchLoopAsync(
        NodeGatewayConnection connection,
        SessionBridge bridge,
        NodeServiceHost host,
        LocalIdentity identity,
        IReadOnlyList<GatewayToNodeMessage> bufferedMessages,
        System.Threading.Channels.ChannelReader<LocalIdentity> reconcileRequests,
        CancellationToken ct)
    {
        // Track current identity so E2E handshakes use the latest NodeId even after reconcile.
        var currentIdentity = identity;

        try
        {
            // Re-process any messages buffered while waiting for PublishMcpServerAcks.
            foreach (var msg in bufferedMessages)
                await DispatchMessageAsync(connection, bridge, host, currentIdentity, msg, ct);

            // Run both the incoming gRPC channel and the reconcile channel concurrently
            // but on a single logical loop to avoid concurrent routing-map mutations.
            var incomingTask = connection.IncomingMessages.WaitToReadAsync(ct).AsTask();
            var reconcileTask = reconcileRequests.WaitToReadAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(incomingTask, reconcileTask);

                if (completed == incomingTask)
                {
                    if (!await incomingTask) break; // channel completed
                    while (connection.IncomingMessages.TryRead(out var msg))
                        await DispatchMessageAsync(connection, bridge, host, currentIdentity, msg, ct);
                    incomingTask = connection.IncomingMessages.WaitToReadAsync(ct).AsTask();
                }
                else
                {
                    // Drain all pending reconcile requests (edge: multiple rapid changes).
                    if (!await reconcileTask) break;
                    LocalIdentity? latest = null;
                    while (reconcileRequests.TryRead(out var req))
                        latest = req;
                    if (latest is not null)
                    {
                        currentIdentity = latest;
                        try
                        {
                            // FIX-2: replay any Frame/CloseSession messages that were buffered
                            // while PublishAllAsync waited for acks during reconcile, mirroring
                            // the startup path where buffered messages are replayed before the loop.
                            // Also pass inference points so that `korat agent add` (which writes
                            // config.json) causes the running daemon to publish the new point.
                            var reconcileBuffered = await host.ReconcileAsync(
                                latest.McpServers,
                                connection.IncomingMessages,
                                ct,
                                latest.InferencePoints);
                            foreach (var msg in reconcileBuffered)
                                await DispatchMessageAsync(connection, bridge, host, currentIdentity, msg, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"[service] Reconcile failed: {ex.Message}");
                        }
                    }
                    reconcileTask = reconcileRequests.WaitToReadAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[service] Frame dispatch failed errorType={ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task DispatchMessageAsync(
        NodeGatewayConnection connection,
        SessionBridge bridge,
        NodeServiceHost host,
        LocalIdentity identity,
        GatewayToNodeMessage msg,
        CancellationToken ct)
    {
        switch (msg.PayloadCase)
        {
            case GatewayToNodeMessage.PayloadOneofCase.Frame:
                // 031: pass enc + meta + sequenceNumber so the bridge can decrypt E2E frames.
                await bridge.OnFrameReceivedAsync(
                    msg.Frame.SessionId,
                    msg.Frame.McpServerId,
                    msg.Frame.Ciphertext.Memory,
                    msg.Frame.Enc,
                    msg.Frame.Meta,
                    msg.Frame.SequenceNumber,
                    ct);
                break;

            // 031: E2E handshake offer forwarded from the cloud to the publisher.
            // BLOCKING-1 fix: HandleE2eKeyOfferAsync internally awaits E2eKeyConfirm
            // (up to 10 s) which arrives on this same IncomingMessages channel.
            // Awaiting it inline would park the dispatch loop and prevent delivery of
            // E2EKeyConfirm → deadlock. Task.Run keeps the loop free to consume and
            // deliver the confirm.
            case GatewayToNodeMessage.PayloadOneofCase.E2EKeyOffer:
            {
                var offerMsg = msg.E2EKeyOffer;
                var capturedIdentity = identity;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await bridge.HandleE2eKeyOfferAsync(
                            offerMsg,
                            agentClientId: offerMsg.AgentClientId,
                            publisherNodeId: capturedIdentity.NodeId,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[e2e] HandleE2eKeyOfferAsync faulted session={offerMsg.SessionId}: {ex.Message}");
                    }
                });
                break;
            }

            // 031: E2E confirm forwarded from the cloud to the publisher.
            case GatewayToNodeMessage.PayloadOneofCase.E2EKeyConfirm:
                bridge.HandleE2eKeyConfirm(msg.E2EKeyConfirm);
                break;

            case GatewayToNodeMessage.PayloadOneofCase.CloseSession:
                await bridge.CloseSessionAsync(msg.CloseSession.SessionId);
                break;

            // PublishMcpServerAck arriving here means a reconcile publish completed;
            // NodeServiceHost.PublishAllAsync drains these before returning but any
            // stale ack landing in the main loop is simply ignored.
            case GatewayToNodeMessage.PayloadOneofCase.PublishMcpServerAck:
                break;

            // 029: InferenceRequest — cloud dispatches a job to this node.
            // Fire-and-forget async task; host manages the job lifecycle.

            // 029: InferenceCancel — client disconnected or gateway timed out.

            // AccessPending/Denied/SessionOpened only flow to the requesting agent node.
        }
    }
}
