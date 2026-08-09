using System.CommandLine;
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
/// <c>korat up</c> — foreground debug mode.
///
/// Serves ALL locally-registered MCP servers (reads from config.json).
/// Reuses <see cref="NodeServiceHost"/> for publish/route/reconcile so the foreground
/// mode and the background daemon share identical logic.
///
/// Optional <c>--serve &lt;name&gt;</c> limits bridging to a single server (back-compat).
/// </summary>
public static class UpCommand
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);

    public static Command Create()
    {
        var command = new Command("up", "Start the local publisher runtime (foreground debug mode)");
        var nameOption = new Option<string?>("--name", "Publisher runtime display name (defaults to machine name)");
        var serveOption = new Option<string?>("--serve",
            "Limit bridging to a single registered server by name (optional; default is all).");
        command.AddOption(nameOption);
        command.AddOption(serveOption);
        command.SetHandler(RunAsync, nameOption, serveOption);
        return command;
    }

    private static async Task RunAsync(string? displayName, string? serveName)
    {
        // SP4: load CliCredentials (Bearer) — used for gRPC authentication.
        var credStore = new CredentialStore();
        var cliCreds = await credStore.LoadAsync();

        var store = new LocalIdentityStore();
        var identity = store.LoadOrCreate();
        if (string.IsNullOrWhiteSpace(identity.CloudUrl))
        {
            Console.Error.WriteLine("Cloud URL is missing. Run `korat login` first.");
            Environment.ExitCode = 1;
            return;
        }

        // Determine the server list to bridge (resolved once; survives reconnects).
        List<LocalMcpServer> servers;
        if (!string.IsNullOrWhiteSpace(serveName))
        {
            var match = identity.McpServers.FirstOrDefault(s =>
                string.Equals(s.DisplayName, serveName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.Error.WriteLine(
                    $"MCP server '{serveName}' is not registered on this machine. " +
                    "Run `korat mcp add <name> --command \"...\"` first.");
                Environment.ExitCode = 1;
                return;
            }
            servers = [match];
        }
        else
        {
            servers = identity.McpServers;
        }

        var nodeName = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        // SIGTERM surfaces here. Guard against the race where `using var cts` has already
        // disposed the source on a normal Ctrl+C exit before ProcessExit fires (else
        // Cancel() throws ObjectDisposedException → the process aborts with a stack trace).
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
        };

        // ── Outer reconnect loop (Bug 2) ─────────────────────────────────────
        // On stream loss (cloud restart) reconnect rather than exiting — mirrors
        // the daemon's reconnect loop. Exit ONLY on real Ctrl+C / SIGTERM.
        var backoff = new ReconnectBackoff();

        while (!cts.IsCancellationRequested)
        {
            // Пропуск перечитывается на каждой попытке, а не берётся снимком со старта.
            // Провайдер выдаёт его на часы, а этот цикл живёт сутками: со снимком первое
            // же переподключение после истечения ушло бы к шлюзу с мёртвым пропуском.
            // Чтение заодно обновляет истёкший (см. CredentialStore.LoadAsync).
            cliCreds = await credStore.LoadAsync(cts.Token) ?? cliCreds;

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
                    $"Could not reach Korat cloud at {identity.CloudUrl}: {ex.Message} — retrying in {delay.TotalSeconds:F0}s");
                try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }

            Korat.Cli.Util.UpgradeNotice.MaybeWarn(connection.GatewayHello.CurrentCliVersion);
            // fix/default-space-placeholder: persist the server-authoritative SpaceId on first
            // successful connect so the client no longer stores the "default" placeholder.
            store.PersistResolvedSpaceId(identity, connection.GatewayHello.ResolvedSpaceId);
            Console.WriteLine($"Publisher runtime {identity.NodeId} ({nodeName}) online -> {identity.CloudUrl}");
            Console.WriteLine($"Serving {servers.Count} server(s). Press Ctrl+C to stop.");
            Console.WriteLine("Note: agents/inference points added after startup require a restart of 'korat up' (the daemon 'korat service' picks them up live).");

            bool lostConnection = false;
            try
            {
                await using (connection)
                {
                    var emptyMap = new Dictionary<string, McpServerSpec>();
                    await using var bridge = new SessionBridge(connection, emptyMap);
                    var host = new NodeServiceHost(connection, bridge, identity.NodeId);

                    List<GatewayToNodeMessage>? buffered = null;
                    try
                    {
                        buffered = new List<GatewayToNodeMessage>(
                            await host.PublishAllAsync(servers, connection.IncomingMessages, cts.Token));
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to publish MCP servers: {ex.Message}");
                        // C1: treat publish failure as connection-loss for this iteration —
                        // do NOT return (which exits the method). The serve block below is
                        // guarded by `if (!lostConnection)` so we fall through to the
                        // bottom-of-loop backoff and reconnect.
                        lostConnection = true;
                    }

                    if (!lostConnection)
                    {
                        // H2: OnConnected only after publish succeeds — the connection is
                        // genuinely serving at this point. Moving it here prevents a
                        // connect-then-publish-fail hot-loop from wrongly resetting backoff.
                        backoff.OnConnected();

                        // C2: per-iteration linked CTS — cancels frame/heartbeat tasks on
                        // stream loss without touching the outer cts (so the loop reconnects).
                        using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                        var frameTask = Task.Run(() =>
                            FrameDispatchLoopAsync(connection, bridge, identity, buffered!, iterCts.Token));
                        var heartbeatTask = Task.Run(() =>
                            HeartbeatLoopAsync(connection, iterCts.Token));

                        try
                        {
                            await Task.WhenAny(heartbeatTask, frameTask);
                        }
                        finally
                        {
                            // C2: cancel the loser task without cancelling the outer cts.
                            iterCts.Cancel();
                            try { await frameTask; } catch { /* swallow on shutdown */ }
                            try { await heartbeatTask; } catch { /* swallow on shutdown */ }
                        }

                        if (cts.IsCancellationRequested)
                            break;

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
                Console.Error.WriteLine($"Connection lost: {ex.Message}");
                lostConnection = true;
            }

            if (!lostConnection || cts.IsCancellationRequested)
                break;

            var retryDelay = backoff.OnDisconnect();
            Console.Error.WriteLine(
                $"Disconnected from cloud — reconnecting in {retryDelay.TotalSeconds:F0}s");
            try { await Task.Delay(retryDelay, cts.Token); } catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("Publisher runtime stopped.");
    }

    private static async Task HeartbeatLoopAsync(NodeGatewayConnection connection, CancellationToken ct)
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
                Console.Error.WriteLine("Lost connection to cloud gateway.");
                return; // triggers reconnect via Task.WhenAny → iterCts.Cancel()
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Lost connection to cloud gateway: {ex.Status.Detail}");
                return;
            }
        }
    }

    private static async Task FrameDispatchLoopAsync(
        NodeGatewayConnection connection,
        SessionBridge bridge,
        LocalIdentity identity,
        IReadOnlyList<GatewayToNodeMessage> bufferedMessages,
        CancellationToken ct)
    {
        try
        {
            foreach (var msg in bufferedMessages)
                await DispatchMessageAsync(bridge, identity, msg, ct);

            await foreach (var msg in connection.IncomingMessages.ReadAllAsync(ct))
                await DispatchMessageAsync(bridge, identity, msg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Frame dispatch failed errorType={ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task DispatchMessageAsync(
        SessionBridge bridge,
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
            // PublishMcpServerAck may arrive after initial publish; ignore here.
            case GatewayToNodeMessage.PayloadOneofCase.PublishMcpServerAck:
                break;
        }
    }
}
