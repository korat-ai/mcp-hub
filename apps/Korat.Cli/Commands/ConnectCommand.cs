using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Korat.Cli.Auth;
using Korat.Cli.Gateway;
using Korat.Cli.Mcp.Aggregation;
using Korat.Cli.Util;
using Korat.Relay.V1;
using Korat.Mcp;

namespace Korat.Cli.Commands;

public static class ConnectCommand
{
    internal static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FrameResponseTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);
    /// <summary>
    /// 007 SC-004 watchdog: the bridge's stdin pump can be parked inside a
    /// native pipe read that does not observe the cancellation token reliably
    /// on macOS/Linux. After Ctrl+C → <c>cts.Cancel()</c> we grace this long
    /// before hard-exiting so SC-004 (Ctrl+C exit &lt; 1 s) holds regardless.
    /// Frames in flight at the moment of Ctrl+C are intentionally discarded.
    /// </summary>
    internal static readonly TimeSpan SigintExitWatchdog = TimeSpan.FromMilliseconds(500);

    // Small flag so we can distinguish Ctrl+C from deadline timeout inside catch blocks.
    private enum CancelReason { None, UserAbort, Timeout }

    /// <summary>
    /// A3 (node-visibility-doctor): threads the best-known agent name plus a "log only
    /// in bridge mode" gate through ConnectAsync/RunConnectFlowAsync/RunBridgeLoopAsync/
    /// RunSpaceAggregatorAsync/HeartbeatLoopAsync so every fatal exit and the eventual
    /// clean shutdown on the <c>--bridge</c> path leave a trace in
    /// <c>~/.korat/logs/connect-&lt;agent&gt;.log</c> — independent of whatever the MCP
    /// client's own log captured (or didn't, e.g. the 2026-06-09 bridge crash that left no
    /// korat-side trace at all). <see cref="AgentName"/> starts as the raw <c>--agent</c>
    /// option (may be null pre-resolution) and is updated to the resolved agent's Name once
    /// <see cref="ResolveOrCreateAgent"/> runs, so early exits (e.g. "not authenticated",
    /// before any identity is loaded) still log under the best name available at the time.
    /// <see cref="Log"/> is a no-op when <see cref="Bridge"/> is false, so this never touches
    /// disk for the default (request-access) or --send/--wait-response test-mode paths —
    /// their behavior is unchanged.
    /// </summary>
    /// <summary>
    /// Internal (not private) so <see cref="LogTerminalExitOnce"/>'s guard is directly unit
    /// testable — see <c>Korat.Cli.Tests</c> (InternalsVisibleTo).
    /// </summary>
    internal sealed class BridgeLogContext
    {
        public bool Bridge { get; }
        public string? AgentName { get; set; }

        // Final-review LOW fix: the SIGINT handler in ConnectAsync logs its own terminal
        // "exit code=130 reason=user-abort (SIGINT)" line, but the normal shutdown path
        // (RunBridgeLoopAsync / RunSpaceAggregatorAsync, once their pumps observe the same
        // cancellation and unwind) ALSO logs its own final "exit code=... reason=..." line —
        // racing to append a second, misleading line (e.g. "exit code=0 reason=clean-shutdown")
        // right after the accurate SIGINT one. Guarded with Interlocked so only the FIRST
        // terminal-exit log call for this process wins; every later one is a silent no-op.
        private int _terminalExitLogged;

        public BridgeLogContext(bool bridge, string? agentName)
        {
            Bridge = bridge;
            AgentName = agentName;
        }

        public void Log(string message)
        {
            if (!Bridge) return;
            BridgeExitLog.Append(AgentName ?? "unnamed", message);
        }

        /// <summary>
        /// Atomically claims the "terminal exit line" slot for this process. Returns
        /// <see langword="true"/> exactly once (the first caller); every subsequent call
        /// returns <see langword="false"/>. Exposed separately from
        /// <see cref="LogTerminalExitOnce"/> so the guard itself can be unit tested without
        /// touching disk (<see cref="Log"/> always writes to <c>~/.korat/logs</c>).
        /// </summary>
        internal bool TryClaimTerminalExit() => Interlocked.CompareExchange(ref _terminalExitLogged, 1, 0) == 0;

        /// <summary>
        /// Same as <see cref="Log"/>, but only the first call across the process's lifetime
        /// actually writes — use this (instead of <see cref="Log"/>) for every "final exit
        /// code=... reason=..." line so a race between the SIGINT handler and the normal
        /// shutdown path never produces two terminal lines for one exit.
        /// </summary>
        public void LogTerminalExitOnce(string message)
        {
            if (TryClaimTerminalExit())
                Log(message);
        }
    }

    public static Command Create()
    {
        // #97 vocabulary: "connect" is how an MCP CLIENT (agent client) consumes a
        // published MCP server. The --agent flag below names the CLIENT IDENTITY this
        // connection registers as — it is NOT an inference point (see `korat agent`).
        var command = new Command(
            "connect",
            "Connect an MCP client to a published MCP server (or the whole Space).\n\n" +
            "Modes:\n" +
            "  (1) default  — request access and exit. No session, no MCP traffic.\n" +
            "  (2) --bridge — a long-lived stdio TRANSPORT, not a tool-caller. It forwards " +
            "newline-delimited JSON-RPC between stdin/stdout and the remote MCP server; by " +
            "itself it does not call any tool. Point a real MCP client (Claude Desktop, an " +
            "editor, or your own agent's MCP client) at it as the server `command` — that " +
            "client then speaks MCP to it natively. This is the production path for " +
            "repeated tool calls.\n" +
            "  (3) --send   — a one-shot call. It opens the session AND runs the MCP " +
            "`initialize` handshake for you, then sends the single JSON-RPC request you " +
            "pass (e.g. tools/call, tools/list) and prints the response. You do not send a " +
            "manual initialize frame. Use --wait-response to print the reply (implied by " +
            "--send).\n\n" +
            "For an AI agent: use --bridge wired into your MCP client for native, repeated " +
            "tool calls; use --send for a quick one-shot call or smoke test from the shell.\n\n" +
            "Examples:\n" +
            "  # One-shot tool call — session + initialize handled automatically:\n" +
            "  korat connect \"iPhone (iPhone)\" --agent my-agent --send " +
            "'{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"<tool>\",\"arguments\":{}}}' " +
            "--wait-response\n\n" +
            "  # One-shot — list available tools:\n" +
            "  korat connect \"iPhone (iPhone)\" --agent my-agent --send " +
            "'{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}' --wait-response\n\n" +
            "  # Bridge into an MCP client — set this as the client's `command`; the client " +
            "(not this shell) then speaks MCP over stdio and calls tools natively:\n" +
            "  korat connect \"iPhone (iPhone)\" --agent my-agent --bridge\n\n" +
            "  # Bridge the whole Space (every granted server, one MCP endpoint):\n" +
            "  korat connect --space --agent my-agent --bridge\n\n" +
            "--bridge and --send/--wait-response are mutually exclusive.");
        var serverArg = new Argument<string?>("server-name", () => null, "Target MCP server display name (omit with --space)");
        var sendOption = new Option<string?>("--send",
            "One-shot call: pass a single JSON-RPC request as a JSON string (e.g. tools/call, " +
            "tools/list) and --send opens the session, runs the MCP initialize handshake for " +
            "you, then delivers your request and exits — no separate initialize call needed. " +
            "Implies --wait-response. For repeated/native tool calls use --bridge instead. " +
            "MUTUALLY EXCLUSIVE with --bridge.");
        var waitResponseOption = new Option<bool>("--wait-response",
            "Wait for one inbound frame (the JSON-RPC response to --send), print its bytes " +
            "as UTF-8, then exit. Used together with --send. MUTUALLY EXCLUSIVE with --bridge.");
        // 007-cli-bridge-mode: long-lived stdio bridge for external MCP clients
        // (Claude Desktop, editor plug-ins). Mutually exclusive with --send /
        // --wait-response — see FR-001.
        var bridgeOption = new Option<bool>("--bridge",
            "Long-lived stdio TRANSPORT, not a tool-caller: forwards newline-delimited " +
            "JSON-RPC between stdin and the remote MCP server, writing responses to stdout. " +
            "Requires a real MCP client driving it over stdin — set this as the `command` in " +
            "a Claude Desktop / editor / your own agent's MCP-client config so that client " +
            "talks MCP to a server that lives on another machine through the Korat relay. " +
            "Piping text into --bridge yourself does not call a tool; use --send for that. " +
            "MUTUALLY EXCLUSIVE with --send/--wait-response.");
        // #102: allow callers to bypass name-based lookup when a display name is
        // ambiguous (multiple servers share the same name). Supplying --server-id
        // skips ResolveServerWithPublisherAsync entirely and routes directly by ID.
        var serverIdOption = new Option<string?>("--server-id",
            "Connect directly by server ID, bypassing display-name lookup. " +
            "Use this when two servers share the same display name: run " +
            "`korat mcp list --ids` (or `--json`) to find the ID, then pass it here. " +
            "When supplied, the server-name argument is ignored.");
        // 017: named agent identity (the CLIENT identity, #97). Each name (e.g. "cursor",
        // "claude") gets its own NodeId so agent-client and publisher streams are distinct
        // in the cloud routing table. Auto-created on first use and persisted in config.json.
        // When omitted the stable "default" consumer identity is reused. Callers that run more
        // than one MCP client on the same machine should name each one explicitly.
        // NOTE (#97): this is the agent-CLIENT identity for consuming servers, NOT an
        // inference point — register inference points with `korat agent add`.
        var agentOption = new Option<string?>("--agent",
            description: "Stable MCP consumer identity (auto-created on first use). " +
                         "This names the MCP client, NOT an inference point (see `korat agent`). " +
                         "Use different names for different MCP clients on the same machine " +
                         "so each gets its own permissions. Omit to reuse `default`.");
        // Back-compat hidden alias. When supplied, it overrides the agent's persisted
        // ConsumerId. Prefer --agent for all new usage.
        var agentClientIdOption = new Option<string?>("--agent-client-id",
            "Override the agent-client identity (back-compat; prefer --agent).")
        {
            IsHidden = true
        };
        // PR-5 (agent-id-identity): internal option — the korat space-bridge MCP config
        // (BuildKoratMcpConfigJson) passes this alongside --agent agent-{name}-{id8} for a
        // hosted-agent turn only. It is (a) echoed on NodeHello.agent_id so the cloud can
        // stamp Agent.ConsumerAgentClientId at TOFU bind, and (b) recorded on the resolved
        // local AgentIdentity (compat shim — see ResolveOrCreateAgent) so a future name
        // reuse is detectable. Not meant for interactive/manual use.
        var agentIdOption = new Option<string?>("--agent-id",
            "Internal: the hosted agent's stable cloud AgentId (set by the korat space-bridge " +
            "MCP config only).")
        {
            IsHidden = true
        };
        // 028 Space aggregation: connect to the whole Space instead of one server.
        var spaceOption = new Option<bool>("--space",
            "Connect to the whole Space: aggregate every MCP server you are granted " +
            "into one MCP endpoint, auto-discovering new servers and surfacing " +
            "ungranted ones as request-access tools. Use with --bridge and omit the " +
            "server-name argument.");
        // 031: E2E encryption policy. prefer=offer, fallback to plaintext with warning.
        // require=offer, fail-closed if the handshake is not completed. off=no offer.
        var e2eOption = new Option<E2ePolicy>("--e2e",
            () => E2ePolicy.Prefer,
            "E2E encryption policy: prefer (default, fallback to plaintext with warning), " +
            "require (fail-closed: close session if E2E cannot be established), off (no offer).");
        // #104: keep the default E2E output calm + plain; gate protocol detail behind --verbose.
        var verboseOption = new Option<bool>(
            new[] { "--verbose", "-v" },
            "Show protocol-level detail (E2E handshake diagnostics) on stderr.");
        command.AddArgument(serverArg);
        command.AddOption(sendOption);
        command.AddOption(waitResponseOption);
        command.AddOption(bridgeOption);
        command.AddOption(serverIdOption);
        command.AddOption(spaceOption);
        command.AddOption(agentOption);
        command.AddOption(agentClientIdOption);
        command.AddOption(agentIdOption);
        command.AddOption(e2eOption);
        command.AddOption(verboseOption);
        command.SetHandler(
            (System.CommandLine.Invocation.InvocationContext ctx) =>
            {
                var serverName = ctx.ParseResult.GetValueForArgument(serverArg);
                var send = ctx.ParseResult.GetValueForOption(sendOption);
                var waitResponse = ctx.ParseResult.GetValueForOption(waitResponseOption);
                var bridge = ctx.ParseResult.GetValueForOption(bridgeOption);
                var serverId = ctx.ParseResult.GetValueForOption(serverIdOption);
                var space = ctx.ParseResult.GetValueForOption(spaceOption);
                var agentName = ctx.ParseResult.GetValueForOption(agentOption);
                var agentClientIdOverride = ctx.ParseResult.GetValueForOption(agentClientIdOption);
                var hostedAgentId = ctx.ParseResult.GetValueForOption(agentIdOption);
                var e2ePolicy = ctx.ParseResult.GetValueForOption(e2eOption);
                // #104: set the E2E console verbosity once — the CLI runs a single command per process.
                E2eConsole.Verbose = ctx.ParseResult.GetValueForOption(verboseOption);
                return ConnectAsync(serverName, send, waitResponse, bridge, serverId, space, agentName, agentClientIdOverride, e2ePolicy, hostedAgentId);
            });
        return command;
    }

    /// <summary>
    /// Р24: does this invocation ask `korat connect` to act as an MCP CONSUMER?
    ///
    /// <para>Extracted as its own predicate so the decision is testable. The publisher side of
    /// this command stays fully supported — the gate must not catch it, and "the gate is narrow
    /// enough" is exactly the property a test can hold onto while the flag set grows.</para>
    /// </summary>
    internal static bool IsConsumerMode(bool bridge, bool space, string? send, bool waitResponse) =>
        bridge || space || send is not null || waitResponse;

    /// <summary>031: E2E encryption policy for --e2e option.</summary>
    internal enum E2ePolicy { Prefer, Require, Off }

    /// <summary>
    /// Deferred-fix (latency): consume the cloud's advisory <c>SessionOpened.peer_supports_e2e</c>.
    /// Returns true ONLY when the cloud EXPLICITLY said the publisher cannot do E2E
    /// (field present AND false). <paramref name="peerSupportsE2e"/> is null when the field
    /// was absent (old cloud) — in that case we keep the normal offer/handshake path, because
    /// proto3 absence must not be confused with an explicit "unsupported" advisory.
    /// SECURITY: skipping on explicit-false is not a new downgrade vector — a malicious cloud
    /// could already force plaintext under prefer by forging E2eNotSupported or black-holing
    /// the offer; --e2e=require still fails closed (see RunBridgeLoopAsync).
    /// </summary>
    internal static bool ShouldSkipE2eOffer(bool? peerSupportsE2e) => peerSupportsE2e == false;

    /// <summary>
    /// Extracts the advisory flag with presence: null when the cloud did not stamp the field
    /// (old cloud), otherwise the explicit value.
    /// </summary>
    internal static bool? GetPeerSupportsE2eAdvisory(SessionOpened opened) =>
        opened.HasPeerSupportsE2E ? opened.PeerSupportsE2E : null;

    internal static TimeSpan GetApprovalTimeout()
    {
        var env = Environment.GetEnvironmentVariable("KORAT_CONNECT_APPROVAL_TIMEOUT_SECONDS");
        if (int.TryParse(env, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return DefaultApprovalTimeout;
    }

    /// <summary>Validates --space flag combinations. Returns an error message, or null if valid.</summary>
    internal static string? ValidateSpaceFlags(bool space, string? serverName, bool bridge, string? send, bool waitResponse)
    {
        if (!space)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return "A server-name argument is required (or use --space to connect to the whole Space).";
            return null;
        }
        if (!bridge)
            return "--space requires --bridge (the aggregator runs as a long-lived stdio MCP server).";
        if (!string.IsNullOrWhiteSpace(serverName))
            return "--space connects to the whole Space; do not pass a server-name argument.";
        if (send is not null || waitResponse)
            return "--space cannot be combined with --send or --wait-response.";
        return null;
    }

    /// <summary>
    /// 017/028: Finds the named agent in <paramref name="identity"/>.Agents, or creates and
    /// persists a new one with its own NodeId and ConsumerId. When
    /// <paramref name="agentName"/> is null or whitespace the stable name <c>default</c> is
    /// used. The consumer's NodeId is
    /// distinct from the publisher's <c>identity.NodeId</c> so the cloud routing table
    /// keeps their streams separate (fixes the loopback bug).
    /// </summary>
    /// <param name="agentId">
    /// PR-5 (agent-id-identity, additive): the hosted agent's stable cloud AgentId, when
    /// known (threaded from the hidden <c>--agent-id</c> option the korat space-bridge MCP
    /// config passes alongside <c>--agent agent-{name}-{id8}</c>). Null for plain
    /// (non-hosted) <c>korat connect --agent &lt;name&gt;</c> usage and for a mixed-version
    /// rollout window. Drives the legacy-name compat shim below and is recorded on the
    /// resolved/created identity so a future reuse of the name is detectable.
    /// </param>
    internal static AgentIdentity ResolveOrCreateAgent(
        LocalIdentity identity,
        string? agentName,
        LocalIdentityStore store,
        string? agentId = null)
    {
        // A generated name on every invocation creates a new ConsumerId, which fragments
        // permissions and leaves synthetic consumer rows behind. Reuse one predictable identity
        // unless the caller deliberately names separate MCP clients.
        agentName = string.IsNullOrWhiteSpace(agentName) ? "default" : agentName;

        if (!string.IsNullOrWhiteSpace(agentName))
        {
            var existing = identity.Agents.Find(a =>
                string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // fable #188 (PR #188, LOW-2): the recorded AgentId lets us detect a
                // same-name id8-slot COLLISION — a deleted agent left this
                // `agent-{name}-{id8}` identity behind with AgentId=A recorded, and a
                // recreated same-name agent's Guid("N") AgentId happens to collide on
                // the first 8 hex chars (B[..8]==A[..8]), so it resolves to the SAME
                // exact-match slot. Returning `existing` here would hand the caller the
                // DEAD agent's ConsumerId — and therefore its still-Active grants.
                // Detect the mismatch and mint a FRESH identity instead (new NodeId +
                // ConsumerId, recording the new AgentId), replacing the stale slot.
                if (agentId is not null && existing.AgentId is not null &&
                    !string.Equals(existing.AgentId, agentId, StringComparison.Ordinal))
                {
                    identity.Agents.Remove(existing);
                    var replacement = new AgentIdentity
                    {
                        Name = existing.Name,
                        NodeId = Korat.Domain.NodeId.New().Value,
                        AgentClientId = Korat.Domain.ConsumerId.New().Value,
                        AgentId = agentId,
                    };
                    identity.Agents.Add(replacement);
                    store.Save(identity);
                    Console.Error.WriteLine(
                        $"korat: local identity slot '{existing.Name}' was recorded for a " +
                        "different agent (id8 collision after delete+recreate) — minted a " +
                        "fresh identity instead of inheriting its permissions.");
                    return replacement;
                }

                // PR-5: backfill AgentId onto an identity that predates this field (created
                // by a pre-PR-5 CLI, or resolved once already before the bridge started
                // passing --agent-id). Purely additive bookkeeping — it never overwrites an
                // already-recorded value, so it cannot itself cause a name-reuse identity mix
                // up. The mismatch case above (both non-null and different) is handled first.
                if (agentId is not null && existing.AgentId is null)
                {
                    existing.AgentId = agentId;
                    store.Save(identity);
                }
                return existing;
            }

            // PR-5 compat shim. The hosted-agent bridge identity was
            // re-keyed from `agent-{name}` to `agent-{name}-{id8}` so a delete→recreate under
            // the SAME name mints a NEW ConsumerId (delete→recreate safety) instead of
            // reusing the dead agent's. Naively applying that re-key would make EVERY
            // pre-existing hosted agent detach its Active grants on its very first post-PR
            // turn, forcing a re-approval. Instead: when no identity exists under the new
            // `agent-{name}-{id8}` name but one DOES exist under the legacy `agent-{name}`
            // name, migrate it IN PLACE — same NodeId + ConsumerId, only Name (and now
            // AgentId) change — so the cloud-side grant (bound to ConsumerId) stays bound.
            // One-way, idempotent (the exact-match branch above short-circuits every later
            // call for the same agent), case-insensitive (mirrors the lookup above).
            // Detecting the legacy shape is exact, not a guess: id8 is always the first 8
            // chars of a Guid("N") AgentId (32 lowercase hex chars — never a dash in that
            // range), and we already know the CURRENT turn's real agentId, so
            // TryStripLegacyBridgeSuffix strips precisely that known suffix.
            //
            // KNOWN RESIDUAL (documented in the plan and here): a PRE-PR agent that is
            // deleted AND has its name reused before its FIRST post-PR turn will, on that
            // turn, migrate the stale legacy identity in place, and the recreated agent
            // inherits the dead one's grants. This is a one-time rollout window that
            // self-closes once every agent has run >=1 post-PR turn (which stamps AgentId
            // on the legacy identity, closing this branch for that name going forward).
            // Accepted per the user's full-re-key + preserve-grants intent — the
            // provably-safe alternative (mint fresh whenever unverifiable) would force a
            // one-time re-approval on EVERY pre-PR agent, defeating the shim's purpose.
            if (agentId is { Length: > 0 } && TryStripLegacyBridgeSuffix(agentName!, agentId, out var legacyName))
            {
                var legacy = identity.Agents.Find(a =>
                    string.Equals(a.Name, legacyName, StringComparison.OrdinalIgnoreCase));
                if (legacy is not null)
                {
                    legacy.Name = agentName!;
                    legacy.AgentId = agentId;
                    store.Save(identity);
                    Console.Error.WriteLine(
                        $"korat: migrated legacy agent identity '{legacyName}' -> '{agentName}' " +
                        "(agent-id-identity re-key; existing permissions preserved).");
                    return legacy;
                }
            }
        }

        var newAgent = new AgentIdentity
        {
            Name = agentName!,
            NodeId = Korat.Domain.NodeId.New().Value,
            AgentClientId = Korat.Domain.ConsumerId.New().Value,
            AgentId = agentId,
        };
        identity.Agents.Add(newAgent);
        store.Save(identity);
        return newAgent;
    }

    /// <summary>
    /// PR-5 compat-shim helper: <paramref name="agentName"/> is expected in the new
    /// <c>agent-{name}-{id8}</c> bridge-identity shape, where id8 is the first 8 chars of
    /// <paramref name="agentId"/> — a <c>Guid("N")</c> value (32 lowercase hex chars, so it
    /// never contains a '-' within its first 8 chars). Because the CURRENT turn's real
    /// agentId is already known, this strips that EXACT suffix rather than guessing at a
    /// generic "ends with 8 hex chars" shape (which would be ambiguous for a name that
    /// itself happens to end in 8 hex chars). Returns the legacy <c>agent-{name}</c> form
    /// via <paramref name="legacyName"/> when <paramref name="agentName"/> ends with
    /// "-{id8}" (case-insensitive — mirrors the macOS-default-case-insensitive-FS note
    /// elsewhere); otherwise false (nothing to strip).
    /// </summary>
    internal static bool TryStripLegacyBridgeSuffix(string agentName, string agentId, out string legacyName)
    {
        var id8 = agentId.Length >= 8 ? agentId[..8] : agentId;
        var suffix = "-" + id8;
        if (id8.Length > 0 &&
            agentName.Length > suffix.Length &&
            agentName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            legacyName = agentName[..^suffix.Length];
            return true;
        }
        legacyName = string.Empty;
        return false;
    }

    private static async Task ConnectAsync(string? serverName, string? send, bool waitResponse, bool bridge, string? serverIdOverride, bool space, string? agentName, string? agentClientIdOverride, E2ePolicy e2ePolicy = E2ePolicy.Prefer, string? hostedAgentId = null)
    {
        // ── Р24: the consumer side of `korat connect` is disabled ────────────────────────────
        //
        // Every mode below turns this process into an MCP CONSUMER: --bridge is a long-lived
        // stdio transport for an MCP client, --send/--wait-response is a one-shot tool call, and
        // --space is the local aggregator. All three authenticate with ~/.korat/credentials — one
        // token per machine — so the cloud saw a single consumer identity no matter which agent
        // was really calling. Grants issued "to an agent" were grants to the machine, and any
        // process on it got not someone else's access but its own, legitimately.
        //
        // Р25 removed the matching entrance on the server (/mcp/{space} is OAuth-only now).
        // Leaving this side working would not preserve anything — it would only fail later and
        // less clearly. Consumers connect straight to the cloud over HTTP/SSE with their own
        // OAuth client, which is what gives each agent an identity of its own.
        //
        // The code is NOT deleted (Р24 is explicit about that): the publisher side of this file
        // shares the connect/heartbeat/E2E machinery, and a future per-agent local transport would
        // start from it rather than from scratch.
        if (IsConsumerMode(bridge, space, send, waitResponse))
        {
            Console.Error.WriteLine(
                "korat connect can no longer act as an MCP consumer (--bridge, --space, --send, --wait-response).");
            Console.Error.WriteLine(
                "Point your MCP client at the Space endpoint directly and let it authenticate with OAuth:");
            Console.Error.WriteLine("  https://<your-korat-host>/mcp/<space>");
            Console.Error.WriteLine(
                "Each client then has its own identity, its own permissions, and its own revocation.");
            Environment.ExitCode = 1;
            return;
        }

        // A3: created before anything else so even the earliest fatal exits (before any
        // identity is loaded) leave a trace when --bridge was requested.
        var bridgeLog = new BridgeLogContext(bridge, agentName);

        // SP4: require CliCredentials (Bearer) — the legacy owner-token path is retired
        // from the CLI. Run `korat login` to obtain credentials before connecting.
        var credStore = new CredentialStore();
        var cliCreds = await credStore.LoadAsync();

        if (cliCreds is null)
        {
            Console.Error.WriteLine("Not authenticated. Run `korat login` first.");
            bridgeLog.LogTerminalExitOnce("exit code=1 reason=not-authenticated");
            Environment.ExitCode = 1;
            return;
        }

        var store = new LocalIdentityStore();
        var identity = store.LoadOrCreate();

        var testMode = send is not null;
        if (testMode) waitResponse = true; // --send implies --wait-response.

        // T002 (007 FR-001): all three pairwise mutex cases between --bridge and
        // the test-mode flags. Reject early before we touch the network.
        if (bridge && (send is not null || waitResponse))
        {
            Console.Error.WriteLine(
                "--bridge cannot be combined with --send or --wait-response.\n" +
                "  --bridge : long-lived stdio proxy (Claude Desktop / MCP clients)\n" +
                "  --send   : one-shot test frame (mutually exclusive with --bridge)");
            bridgeLog.LogTerminalExitOnce("exit code=1 reason=bridge-send-mutex");
            Environment.ExitCode = 1;
            return;
        }

        // #102: --server-id and --space are mutually exclusive (space mode aggregates all
        // servers and has no single target).
        if (!string.IsNullOrWhiteSpace(serverIdOverride) && space)
        {
            Console.Error.WriteLine("--server-id cannot be combined with --space.");
            bridgeLog.LogTerminalExitOnce("exit code=1 reason=server-id-space-mutex");
            Environment.ExitCode = 1;
            return;
        }

        // 028: --space requires --bridge, no server-name, and no test-mode flags.
        // #102: when --server-id is supplied, the server-name argument is optional
        // (the ID is the target), so satisfy the name requirement with a placeholder.
        var serverNameForValidation = !string.IsNullOrWhiteSpace(serverIdOverride)
            ? (string.IsNullOrWhiteSpace(serverName) ? serverIdOverride : serverName)
            : serverName;
        var spaceError = ValidateSpaceFlags(space, serverNameForValidation, bridge, send, waitResponse);
        if (spaceError is not null)
        {
            Console.Error.WriteLine(spaceError);
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason=invalid-flags: {spaceError}");
            Environment.ExitCode = 1;
            return;
        }

        var cancelReason = CancelReason.None;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancelReason = CancelReason.UserAbort;
            // A3: log BEFORE the watchdog's Environment.Exit(130) below, since that call can
            // terminate the process before the graceful shutdown path (which also logs) gets
            // a chance to run.
            bridgeLog.LogTerminalExitOnce("exit code=130 reason=user-abort (SIGINT)");
            cts.Cancel();
            // 007 SC-004 watchdog (see SigintExitWatchdog XmlDoc above for why).
            _ = Task.Run(async () =>
            {
                await Task.Delay(SigintExitWatchdog).ConfigureAwait(false);
                Environment.Exit(130);
            });
        };

        // Deadline governs the connect/approval phase only. Test mode keeps the
        // short frame-response window. Bridge mode shares the approval deadline
        // (the operator still needs to grant access before the bridge starts
        // pumping) and then clears the deadline once SessionOpened arrives —
        // see RunBridgeLoopAsync (FR-006).
        var deadline = testMode ? FrameResponseTimeout : GetApprovalTimeout();
        cts.CancelAfter(deadline);
        // Bug fix (LOW): gate the timeout continuation so it does not fire after bridge mode
        // has disarmed the approval-phase timer via CancelAfter(Infinite). Without this gate
        // the Task.Delay would still run to completion ~5 minutes later (before the token is
        // ever cancelled in a long-lived bridge session) and flip cancelReason to Timeout,
        // causing a misleading "Timed out waiting for owner approval" message on exit.
        _ = Task.Delay(deadline, cts.Token)
            .ContinueWith(_ =>
            {
                // Only record Timeout when we are not in bridge mode. In bridge mode the
                // approval timer is disarmed by RunBridgeLoopAsync via CancelAfter(Infinite)
                // before this continuation fires, so the delay can still complete (the token
                // is never cancelled during a healthy bridge session). Skipping here prevents
                // a spurious Timeout reason from overwriting UserAbort or None.
                if (!bridge && cancelReason == CancelReason.None)
                    cancelReason = CancelReason.Timeout;
            }, TaskContinuationOptions.OnlyOnRanToCompletion);

        try
        {
            await RunConnectFlowAsync(serverName, identity, store, cliCreds, send, waitResponse, bridge, serverIdOverride, space, agentName, agentClientIdOverride, cts, bridgeLog, e2ePolicy, hostedAgentId);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            string reason;
            if (cancelReason == CancelReason.UserAbort)
            {
                Console.Error.WriteLine("Connect cancelled by user.");
                reason = "user-abort";
            }
            else if (testMode)
            {
                Console.Error.WriteLine($"Timed out after {deadline.TotalSeconds:F0}s waiting for response frame.");
                reason = "timeout";
            }
            else
            {
                Console.Error.WriteLine("Timed out waiting for owner approval (5 minutes). Pending request remains in Space.");
                reason = "approval-timeout";
            }
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason={reason}");
            Environment.ExitCode = 1;
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine($"Could not reach Korat cloud at {identity.CloudUrl}: {ex.Status.Detail}");
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason=rpc-error: {ex.Status.Detail}");
            Environment.ExitCode = 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not reach Korat cloud at {identity.CloudUrl}: {ex.Message}");
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason=http-error: {ex.Message}");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            // Final-review MEDIUM fix: spec requires "any unhandled exception logs its
            // reason before exit" — OCE/RpcException/HttpRequestException above cover the
            // expected failure modes, but anything else previously escaped ConnectAsync with
            // no trace in ~/.korat/logs/connect-<agent>.log, leaving whoever is debugging a
            // dead bridge with nothing but Program.cs's Sentry capture (if network to Sentry
            // was even up) to go on. bridgeLog.Log(...) is a no-op when !Bridge, so non-bridge
            // behavior is byte-identical: `throw;` preserves the original stack trace and lets
            // Program.cs's top-level catch (Sentry capture) and the runtime's normal unhandled-
            // exception exit proceed exactly as before this catch existed.
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason=unhandled-exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static async Task RunConnectFlowAsync(
        string? serverName,
        LocalIdentity identity,
        LocalIdentityStore store,
        CliCredentials cliCreds,
        string? sendMessage,
        bool waitResponse,
        bool bridge,
        string? serverIdOverride,
        bool space,
        string? agentName,
        string? agentClientIdOverride,
        CancellationTokenSource cts,
        BridgeLogContext bridgeLog,
        E2ePolicy e2ePolicy = E2ePolicy.Prefer,
        string? hostedAgentId = null)
    {
        var ct = cts.Token;
        // Bridge mode reserves stdout for raw JSON-RPC; route human-facing
        // diagnostic lines to stderr (FR-005). All other modes use stdout.
        var info = bridge ? Console.Error : Console.Out;

        // 028: --space runs the client-side aggregator instead of a single-server
        // session. Branch before ResolveServerIdAsync — space mode has no server-name.
        if (space)
        {
            await RunSpaceAggregatorAsync(identity, store, cliCreds, agentName, agentClientIdOverride, cts, bridgeLog, e2ePolicy, hostedAgentId);
            return;
        }

        // #102: --server-id bypasses name-based resolution entirely, so callers can
        // disambiguate when multiple servers share the same display name. The publisher
        // node id is unknown in this path (we skipped the catalog lookup); the E2E
        // handshake tolerates a null publisher node id (prefer falls back to plaintext).
        string serverId;
        string? publisherNodeId;
        if (!string.IsNullOrWhiteSpace(serverIdOverride))
        {
            serverId = serverIdOverride!;
            publisherNodeId = null;
        }
        else
        {
            // ResolveServerWithPublisherAsync prints a specific error message and sets ExitCode on any
            // failure (auth error, transport error, not-found). Returning null here always
            // means "already reported" — we just exit.
            var serverResolution = await ResolveServerWithPublisherAsync(identity, cliCreds, serverName!, ct);
            if (serverResolution is null)
                return;
            (serverId, publisherNodeId) = serverResolution.Value;
        }

        // 017: resolve (or auto-create) the named agent identity.
        // The agent's NodeId is DISTINCT from the publisher's identity.NodeId so the
        // cloud routing table keeps agent and publisher streams under separate entries,
        // preventing the loopback bug (agent NodeId == publisher NodeId → frame echo).
        // PR-5: hostedAgentId (from the hidden --agent-id option, set only by a hosted-agent
        // bridge spawn) both drives the legacy-name compat shim and gets recorded on the
        // resolved identity — see ResolveOrCreateAgent.
        var agent = ResolveOrCreateAgent(identity, agentName, store, hostedAgentId);
        // A3: from here on, logs use the RESOLVED agent name (may differ from the raw
        // --agent option when it was auto-generated).
        bridgeLog.AgentName = agent.Name;

        // agentClientIdOverride is the back-compat --agent-client-id alias; when absent
        // use the agent identity's persisted ConsumerId.
        var agentClientId = agentClientIdOverride ?? agent.AgentClientId;
        var testMode = sendMessage is not null;
        // A3: "target" for the bridge start/exit log — prefer the display name the user
        // typed, fall back to the resolved server id (e.g. when --server-id was used).
        var bridgeTarget = string.IsNullOrWhiteSpace(serverName) ? serverId : serverName;

        // 006-cli-stdio-bridge: use NodeGatewayConnection so we can both send Frames
        // (in test/bridge mode) and consume incoming Frames (response from publisher).
        // 017: connect under the agent's NodeId with node_kind="agent" so the cloud
        // registers a separate stream entry distinct from the publisher stream.
        // 020-A: use the agent's friendly name (e.g. "cursor", "default") as the node
        // DisplayName so the Nodes view shows a meaningful label rather than the machine name.
        // PR-5: echo the resolved identity's AgentId (if any) on NodeHello.agent_id so the
        // cloud can stamp Agent.ConsumerAgentClientId at TOFU bind.
        await using var connection = await NodeGatewayConnection.ConnectAsync(
            identity, agent.Name, ct, cliCreds,
            nodeIdOverride: agent.NodeId, nodeKind: "agent", agentIdHint: agent.AgentId);
        Korat.Cli.Util.UpgradeNotice.MaybeWarn(connection.GatewayHello.CurrentCliVersion);
        // fix/default-space-placeholder: persist the server-authoritative SpaceId on first
        // successful connect so the client no longer stores the "default" placeholder.
        store.PersistResolvedSpaceId(identity, connection.GatewayHello.ResolvedSpaceId);

        var requestId = Guid.NewGuid().ToString("N");
        await connection.SendRequestSessionAsync(requestId, agentClientId, serverId, ct);

        // First non-HeartbeatAck message after RequestSession is one of:
        // SessionOpened / AccessPending / AccessDenied.
        var response = await ReadNextAsync(connection, ct);

        switch (response.PayloadCase)
        {
            case GatewayToNodeMessage.PayloadOneofCase.SessionOpened:
            {
                var sessionId = response.SessionOpened.SessionId;
                info.WriteLine($"Access granted. Session {sessionId} ready.");
                if (testMode)
                    await RunOneShotExchangeAsync(connection, sessionId, sendMessage!, waitResponse, ct);
                else if (bridge)
                    await RunBridgeLoopAsync(connection, sessionId, agentClientId, publisherNodeId, cts,
                        identity.CloudUrl, bridgeTarget!, bridgeLog, e2ePolicy,
                        GetPeerSupportsE2eAdvisory(response.SessionOpened));
                return;
            }

            case GatewayToNodeMessage.PayloadOneofCase.AccessDenied:
                Console.Error.WriteLine($"Access denied: {response.AccessDenied.Reason}");
                bridgeLog.LogTerminalExitOnce($"exit code=1 reason=access-denied: {response.AccessDenied.Reason}");
                Environment.ExitCode = 1;
                return;

            case GatewayToNodeMessage.PayloadOneofCase.AccessPending:
            {
                var accessRequestId = response.AccessPending.AccessRequestId;
                var opened = await WaitForApprovalAsync(connection, identity, cliCreds, serverId, accessRequestId, agentClientId, requestId, bridge, ct);
                if (opened is null) return;
                if (testMode)
                    await RunOneShotExchangeAsync(connection, opened.SessionId, sendMessage!, waitResponse, ct);
                else if (bridge)
                    await RunBridgeLoopAsync(connection, opened.SessionId, agentClientId, publisherNodeId, cts,
                        identity.CloudUrl, bridgeTarget!, bridgeLog, e2ePolicy,
                        GetPeerSupportsE2eAdvisory(opened));
                return;
            }

            default:
                Console.Error.WriteLine($"Unexpected gateway response: {response.PayloadCase}");
                bridgeLog.LogTerminalExitOnce($"exit code=1 reason=unexpected-response: {response.PayloadCase}");
                Environment.ExitCode = 1;
                return;
        }
    }

    // 031: E2E handshake timeout — generous enough to survive a slow publisher startup.
    internal static readonly TimeSpan E2eHandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 007-cli-bridge-mode (T004): long-lived stdio bridge for external MCP
    /// clients. Forward newline-delimited JSON between the local process's
    /// stdin/stdout and the relay session. Used by Claude Desktop and other
    /// MCP clients that spawn the bridge as a "local" MCP server subprocess.
    ///
    /// 031: attempts an E2E handshake (prefer mode) after SessionOpened.
    /// If established, frames are transparently encrypted/decrypted.
    /// On downgrade (E2eNotSupported or timeout) falls back to plaintext with a
    /// loud stderr warning. Anti-downgrade: the warning is always emitted; there
    /// is no silent downgrade path.
    ///
    /// Accepts the full CancellationTokenSource (not just the token) so we can
    /// disarm the approval-phase timer once SessionOpened lands — the bridge
    /// has no natural deadline; it lives until stdin EOF, session-close from
    /// the gateway, fatal RPC error, or Ctrl+C (FR-002, FR-006).
    /// </summary>
    private static async Task RunBridgeLoopAsync(
        NodeGatewayConnection connection,
        string sessionId,
        string agentClientId,
        string? publisherNodeId,
        CancellationTokenSource cts,
        string cloudUrl,
        string target,
        BridgeLogContext bridgeLog,
        E2ePolicy e2ePolicy = E2ePolicy.Prefer,
        bool? peerSupportsE2e = null)
    {
        // FR-006: drop the approval-phase timer; the bridge is open-ended.
        cts.CancelAfter(Timeout.InfiniteTimeSpan);
        var ct = cts.Token;

        // 031: attempt E2E handshake according to --e2e policy.
        // Messages that arrive during the handshake window but are NOT E2E exchange messages
        // (e.g. HeartbeatAcks demultiplexed to the ack channel, unrelated frames) are queued
        // in unconsumedMessages and replayed into the incoming channel before the pumps start.
        E2eAgentSession? e2eSession = null;
        var unconsumed = new Queue<GatewayToNodeMessage>();

        if (e2ePolicy == E2ePolicy.Off)
        {
            // No offer: run plain bridge.
        }
        else if (ShouldSkipE2eOffer(peerSupportsE2e))
        {
            // Deferred-fix (latency): the cloud EXPLICITLY advised the publisher cannot do E2E
            // (SessionOpened.peer_supports_e2e present AND false). Don't send an offer that
            // can only end in E2eNotSupported or a ~10s timeout.
            //   --e2e=require → fail closed immediately (same outcome as a failed handshake).
            //   --e2e=prefer  → plaintext immediately, with the same loud downgrade warning.
            // Old clouds never reach this branch (field absent → peerSupportsE2e == null).
            if (e2ePolicy == E2ePolicy.Require)
            {
                E2eConsole.RequiredButUnavailable(
                    sessionId, "cloud advises the publisher does not support E2E encryption");
                cts.Cancel();
                return;
            }
            E2eConsole.FellBackToPlaintext(
                sessionId, "cloud advises the publisher does not support E2E encryption");
        }
        else
        {
            // Prefer or Require: always send E2eKeyOffer.
            try
            {
                var (result, session) = await E2eAgentSession.EstablishAsync(
                    sessionId,
                    agentClientId,
                    publisherNodeId ?? string.Empty,
                    connection,
                    unconsumed,
                    E2eHandshakeTimeout,
                    ct);

                switch (result)
                {
                    case E2eHandshakeResult.Established:
                        e2eSession = session;
                        E2eConsole.Encrypted(sessionId);
                        break;

                    case E2eHandshakeResult.FellBackToPlaintext:
                        // Warning already printed inside EstablishAsync.
                        if (e2ePolicy == E2ePolicy.Require)
                        {
                            E2eConsole.RequiredButUnavailable(sessionId, "E2E handshake did not complete");
                            cts.Cancel();
                            return;
                        }
                        // Prefer: continue plaintext — warning already printed inside EstablishAsync.
                        break;

                    default:
                        // Failed (crypto error / confirm-tag mismatch): positive evidence of tampering.
                        // Abort even under --e2e=prefer — a broken MAC is not a benign absence.
                        E2eConsole.HandshakeFailedClosing(
                            sessionId, "broken confirm tag / E2E handshake crypto failure");
                        cts.Cancel();
                        return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                e2eSession?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                E2eConsole.Detail($"handshake exception: {ex.Message}");
                if (e2ePolicy == E2ePolicy.Require)
                {
                    E2eConsole.RequiredButUnavailable(sessionId, "handshake exception");
                    cts.Cancel();
                    return;
                }
                E2eConsole.FellBackToPlaintext(sessionId, "handshake error");
            }
        }

        // Unconsumed messages queued during the E2E handshake window are replayed by
        // passing the queue directly to PumpFramesToStdoutAsync, which drains it first
        // before reading from IncomingMessages. No additional buffering needed.

        // A3: right before entering the stdio loop — the last point before the pumps
        // take over stdin/stdout for the life of the session.
        bridgeLog.Log($"started pid={Environment.ProcessId} cloud={cloudUrl} target={target}");

        // Linked CTS lets us cancel the pumps independently from the outer
        // Ctrl+C token, so first-pump-to-finish can tear down its sibling
        // cleanly without disturbing the surrounding cancellation semantics.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stdinTask = PumpStdinToFramesAsync(connection, sessionId, e2eSession, pumpCts.Token);
        var stdoutTask = PumpFramesToStdoutAsync(connection, sessionId, e2eSession, unconsumed, pumpCts.Token);
        // Bug 1: keep the agent node Online for the life of the bridge by sending
        // heartbeats on the same connection (mirrors the publisher HeartbeatLoopAsync).
        var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(connection, bridgeLog, pumpCts.Token));

        var first = await Task.WhenAny(stdinTask, stdoutTask, heartbeatTask);
        pumpCts.Cancel();
        // Drain both pumps before deciding the outcome. The "loser" task
        // (still running when first won) cancels via OCE — expected. The
        // "winner" may have faulted with a real exception (e.g. the stdout
        // pump throwing GatewayDisconnectedException when the gateway emits
        // CloseSession for this session, or when the stream itself dies),
        // and that exception is what Task.WhenAll re-throws. The catch
        // filter on first.IsFaulted lets that exception escape the drain
        // so the `if (first.IsFaulted)` block below can format a clean
        // stderr line. Without this filter the non-OCE exception would
        // bypass the OperationCanceledException catch entirely, propagate
        // up to ConnectAsync's outer try, miss its specific catch list
        // (RpcException, HttpRequestException, OCE), and surface as an
        // unhandled exception with a stack trace — violating the spec's
        // "stderr quiet by default" edge case (S1 from reviewer pass #1).
        try { await Task.WhenAll(stdinTask, stdoutTask, heartbeatTask); }
        catch (OperationCanceledException) { /* expected on the loser */ }
        catch (Exception) when (first.IsFaulted) { /* observe via if-block below */ }

        e2eSession?.Dispose();

        // Surface the first-task fault if any (otherwise clean stdin-EOF exit).
        if (first.IsFaulted && first.Exception is not null)
        {
            var reason = first.Exception.InnerException?.Message ?? first.Exception.Message;
            Console.Error.WriteLine($"Bridge terminated: {reason}");
            Environment.ExitCode = 1;
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason={reason}");
        }
        else
        {
            // A3: covers stdin-EOF and Ctrl+C-cancelled pumps. When ExitCode is non-zero
            // here it was set by a non-faulted path (e.g. HeartbeatLoopAsync losing the
            // gateway connection) that already logged its own specific reason above.
            // Final-review LOW fix: LogTerminalExitOnce guards this against racing the
            // SIGINT handler's own "exit code=130 reason=user-abort (SIGINT)" line — without
            // the guard, a Ctrl+C that unwinds the pumps fast enough could append a second,
            // misleading "exit code=0 reason=clean-shutdown" line right after it.
            bridgeLog.LogTerminalExitOnce($"exit code={Environment.ExitCode} reason=" +
                (Environment.ExitCode == 0 ? "clean-shutdown" : "see-prior-log-line"));
        }
    }

    /// <summary>
    /// 028 T11: --space aggregator. Discovers every granted MCP server, opens a
    /// backend session per server, and runs a single long-lived stdio MCP endpoint
    /// (<see cref="AggregatorMcpServer"/>) that namespaces every backend's tools and
    /// surfaces ungranted servers as request-access tools. A <see cref="SpaceWatcher"/>
    /// polls for Space changes and reconciles sessions/catalog live, emitting
    /// notifications/tools/list_changed when the tool surface changes.
    ///
    /// Mirrors <see cref="RunBridgeLoopAsync"/>: open-ended (disarms the approval-phase
    /// deadline), stdout reserved for MCP JSON-RPC, all diagnostics to stderr, and the
    /// same first-task-wins teardown with OCE drain + fault surfacing.
    /// </summary>
    private static async Task RunSpaceAggregatorAsync(
        LocalIdentity identity, LocalIdentityStore store, CliCredentials cliCreds,
        string? agentName, string? agentClientIdOverride, CancellationTokenSource cts,
        BridgeLogContext bridgeLog,
        E2ePolicy e2ePolicy = E2ePolicy.Prefer,
        string? hostedAgentId = null)
    {
        // Aggregator is open-ended: disarm the approval-phase deadline (mirrors RunBridgeLoopAsync).
        cts.CancelAfter(Timeout.InfiniteTimeSpan);
        var ct = cts.Token;
        var info = Console.Error; // stdout reserved for MCP JSON-RPC

        // PR-5: this is the path a hosted-agent bridge spawn actually takes
        // (`korat connect --space --bridge --agent agent-{name}-{id8} --agent-id {full-id}`).
        // hostedAgentId drives the legacy-name compat shim and is recorded on the identity.
        var agent = ResolveOrCreateAgent(identity, agentName, store, hostedAgentId);
        // A3: from here on, logs use the RESOLVED agent name.
        bridgeLog.AgentName = agent.Name;
        var agentClientId = agentClientIdOverride ?? agent.AgentClientId;

        // PR-5: echo the resolved identity's AgentId (if any) on NodeHello.agent_id.
        await using var connection = await NodeGatewayConnection.ConnectAsync(
            identity, agent.Name, ct, cliCreds, nodeIdOverride: agent.NodeId, nodeKind: "agent",
            agentIdHint: agent.AgentId);
        Korat.Cli.Util.UpgradeNotice.MaybeWarn(connection.GatewayHello.CurrentCliVersion);
        // fix/default-space-placeholder: persist the server-authoritative SpaceId on first
        // successful connect so the client no longer stores the "default" placeholder.
        store.PersistResolvedSpaceId(identity, connection.GatewayHello.ResolvedSpaceId);
        // Своё хранилище, а не общее: оно без состояния и читает с диска, так что дешевле
        // создать здесь, чем тащить через шесть кадров стека.
        using var http = CreateBearerHttpClient(cliCreds, store: new CredentialStore());

        await using var sessions = new BackendSessionManager(connection, agentClientId, e2ePolicy: e2ePolicy);
        var catalog = new AggregateCatalog();
        catalog.SetUpgradeAvailable(
            Korat.Cli.Util.SemVer.IsNewer(connection.GatewayHello.CurrentCliVersion, Korat.Cli.Util.CliVersion.Bare()),
            connection.GatewayHello.CurrentCliVersion,
            Korat.Cli.Util.CliVersion.Bare());

        info.WriteLine($"Connecting to Space as agent '{agent.Name}'…");
        var snapshot = await SpaceDiscovery.DiscoverAsync(http, agentClientId, ct);

        // Open a session per granted server; offline publishers are skipped and excluded from
        // the watcher baseline so SpaceWatcher treats them as not-yet-open and retries them.
        // slugsByServerId is filled in by OpenGrantedServersAsync (id -> the disambiguated slug
        // it actually opened under) and handed to SpaceWatcher below so incremental opens on
        // later ticks disambiguate against the SAME slugs, not a fresh empty set.
        var slugsByServerId = new Dictionary<string, string>();
        var openedGranted = await OpenGrantedServersAsync(sessions, catalog, snapshot.Granted, info, ct, slugsByServerId);
        catalog.SetUngranted(snapshot.Ungranted);
        info.WriteLine($"Ready. {openedGranted.Count}/{snapshot.Granted.Count} granted servers connected, {snapshot.Ungranted.Count} discoverable. Listening on stdio.");

        // stdout writer (reserved for MCP) + stdin reader (wake the blocking read on Ctrl+C).
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
        var stdinStream = Console.OpenStandardInput();
        await using var cancelReg = ct.Register(() => { try { stdinStream.Dispose(); } catch { } });
        using var stdin = new StreamReader(stdinStream, new UTF8Encoding(false), false, 64 * 1024);

        var server = new AggregatorMcpServer(catalog, sessions, stdout, AggregatorVersion());
        var baseline = new SpaceSnapshot(openedGranted, snapshot.Ungranted);
        var watcher = new SpaceWatcher(
            discover: c => SpaceDiscovery.DiscoverAsync(http, agentClientId, c),
            sessions: sessions, catalog: catalog,
            onChanged: c => server.EmitToolsListChangedAsync(c),
            baseline: baseline,
            baselineSlugsByServerId: slugsByServerId);

        // A3: right before entering the stdio loop.
        bridgeLog.Log($"started pid={Environment.ProcessId} cloud={identity.CloudUrl} target=space");

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = server.RunAsync(stdin, pumpCts.Token);
        var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(connection, bridgeLog, pumpCts.Token));
        var watcherTask = Task.Run(() => watcher.RunAsync(pumpCts.Token));

        var first = await Task.WhenAny(serverTask, heartbeatTask, watcherTask);
        pumpCts.Cancel();
        try { await Task.WhenAll(serverTask, heartbeatTask, watcherTask); }
        catch (OperationCanceledException) { /* expected on losers */ }
        catch (Exception) when (first.IsFaulted) { /* surfaced below */ }

        if (first.IsFaulted && first.Exception is not null)
        {
            var reason = first.Exception.InnerException?.Message ?? first.Exception.Message;
            info.WriteLine($"Aggregator terminated: {reason}");
            Environment.ExitCode = 1;
            bridgeLog.LogTerminalExitOnce($"exit code=1 reason={reason}");
        }
        else
        {
            // A3: clean exit on stdin EOF (serverTask returns normally), Ctrl+C-cancelled
            // pumps, or a non-faulted HeartbeatLoopAsync loss (which already logged its
            // own specific reason above).
            // Final-review LOW fix: LogTerminalExitOnce guards against the same SIGINT-vs-
            // shutdown-path race as RunBridgeLoopAsync above (--space also runs under
            // --bridge, so the same CancelKeyPress handler applies).
            bridgeLog.LogTerminalExitOnce($"exit code={Environment.ExitCode} reason=" +
                (Environment.ExitCode == 0 ? "clean-shutdown" : "see-prior-log-line"));
        }
    }

    /// <summary>
    /// Opens a session for each granted server and registers its tools in the catalog.
    /// Returns the servers that opened SUCCESSFULLY — offline/failed ones are skipped and
    /// logged; they are deliberately EXCLUDED from the returned list so the SpaceWatcher
    /// baseline does not record them as open and retries them on the next tick.
    /// </summary>
    /// <param name="slugsByServerId">
    /// Optional out-collector: on return, contains serverId -> the slug each SUCCESSFULLY
    /// opened server was actually registered under (via <see cref="ToolNamespacer.UniqueSlug"/>).
    /// Pass the SAME instance to <see cref="SpaceWatcher"/>'s baseline so later incremental
    /// opens disambiguate against these exact slugs instead of an empty set — otherwise a
    /// server granted after connect-time whose display name collapses to the same slug as one
    /// opened here could collide and mis-route tools/call. Defaults to a scratch dictionary
    /// when the caller doesn't need the mapping (e.g. existing tests).
    /// </param>
    internal static async Task<List<ServerDescriptor>> OpenGrantedServersAsync(
        BackendSessionManager sessions,
        AggregateCatalog catalog,
        IReadOnlyList<ServerDescriptor> granted,
        TextWriter info,
        CancellationToken ct,
        Dictionary<string, string>? slugsByServerId = null)
    {
        slugsByServerId ??= new Dictionary<string, string>();
        // Deterministic order = iteration order of `granted`. Slug is computed exactly once per
        // server and reused for both OpenAsync (session routing key) and SetGranted (catalog
        // key) — see BackendSession.Slug, the single source of truth both end up keyed on.
        var taken = new HashSet<string>(slugsByServerId.Values);
        var opened = new List<ServerDescriptor>();
        foreach (var s in granted)
        {
            try
            {
                var slug = ToolNamespacer.UniqueSlug(s.DisplayName, s.Id, taken);
                var tools = await sessions.OpenAsync(s, slug, ct);
                catalog.SetGranted(s.Id, slug, s.DisplayName, tools);
                slugsByServerId[s.Id] = slug;
                opened.Add(s);
                info.WriteLine($"  + {s.DisplayName}: {tools.Count} tool(s)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { info.WriteLine($"  ! {s.DisplayName}: unavailable ({ex.Message}) — will retry"); }
        }
        return opened;
    }

    private static string AggregatorVersion()
    {
        // Reuse the CLI's informational version if available; fall back to "0".
        return CliVersion.Informational();
    }

    /// <summary>
    /// Bug 1: sends periodic heartbeats on the agent connection so the cloud keeps the
    /// agent node Online for the life of the bridge. Mirrors UpCommand.HeartbeatLoopAsync.
    /// </summary>
    private static async Task HeartbeatLoopAsync(NodeGatewayConnection connection, BridgeLogContext bridgeLog, CancellationToken ct)
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
                Console.Error.WriteLine("Lost connection to cloud gateway — exiting.");
                bridgeLog.LogTerminalExitOnce("exit code=1 reason=gateway-disconnected");
                Environment.ExitCode = 1;
                return;
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Lost connection to cloud gateway: {ex.Status.Detail}");
                bridgeLog.LogTerminalExitOnce($"exit code=1 reason=rpc-error: {ex.Status.Detail}");
                Environment.ExitCode = 1;
                return;
            }
        }
    }

    /// <summary>
    /// 007-cli-bridge-mode (T005): reads newline-delimited JSON from stdin and
    /// writes each line (with the trailing newline preserved) as a single
    /// relay frame in the client→server direction (FR-003). Returns on EOF;
    /// the caller then cancels the sibling stdout pump and exits cleanly.
    ///
    /// 031: when <paramref name="e2eSession"/> is non-null, the payload is E2E-encrypted
    /// before sending. The cleartext metadata header is stamped alongside the ciphertext.
    ///
    /// **Cancellation correctness note (SC-004 fix from independent review)**:
    /// `StreamReader.ReadLineAsync(CancellationToken)` over `Console.OpenStandardInput()`
    /// is blocked inside a native pipe read that does **not** observe the token
    /// reliably, especially under producers that hold the pipe open with no
    /// data (`tail -f`, Claude Desktop between messages, etc.). Without
    /// intervention, Ctrl+C → `cts.Cancel()` would not return from the
    /// in-flight `ReadLineAsync` until the next byte arrived — gating SC-004
    /// (Ctrl+C exit &lt; 1 s) to "whenever stdin moves next", which can be
    /// indefinite. We register a token callback that disposes the underlying
    /// stdin stream when `ct` fires; the in-flight read then surfaces as
    /// `ObjectDisposedException` / `IOException`, which we treat as a clean
    /// cancel (NOT a fault — we silently return so the bridge exits 0 for
    /// stdin-EOF semantics).
    /// </summary>
    private static async Task PumpStdinToFramesAsync(
        NodeGatewayConnection connection,
        string sessionId,
        E2eAgentSession? e2eSession,
        CancellationToken ct)
    {
        var stdin = Console.OpenStandardInput();
        // Wake the blocking native read when ct cancels.
        await using var cancelReg = ct.Register(() =>
        {
            try { stdin.Dispose(); }
            catch { /* ignore — best-effort wake */ }
        });
        // 64 KiB balances throughput against per-frame overhead for the typical
        // few-KB MCP request. Larger payloads land across multiple ReadAsync
        // calls and we still emit them as soon as we see the trailing newline.
        using var reader = new StreamReader(
            stdin,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024);

        ulong seq = 1;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0) continue; // skip blank keep-alive lines
                var plaintext = Encoding.UTF8.GetBytes(line + "\n");
                if (e2eSession?.Cipher is { } cipher)
                {
                    // 031: stamp metadata header, then seal the payload.
                    // Use the out-seqUsed overload so the wire SequenceNumber matches
                    // the cipher's internal counter (starting at 0), not the independent
                    // seq variable (which starts at 1 and would cause nonce mismatch).
                    var meta = Korat.Protocol.FrameMetadataFactory.FromPlaintext(
                        plaintext, "client_to_server", (ulong)plaintext.Length);
                    var metaBytes = meta.ToByteArray();
                    var wirePayload = cipher.Seal(
                        plaintext,
                        Korat.Protocol.E2eSessionCipher.DirClientToServer,
                        metaBytes,
                        out var seqUsed);
                    await connection.SendE2eFrameAsync(
                        sessionId: sessionId,
                        wirePayload: wirePayload,
                        sequenceNumber: seqUsed,
                        direction: "client_to_server",
                        meta: meta,
                        cancellationToken: ct);
                }
                else
                {
                    await connection.SendFrameAsync(
                        sessionId: sessionId,
                        ciphertext: plaintext,
                        sequenceNumber: seq++,
                        direction: "client_to_server",
                        cancellationToken: ct);
                }
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { /* cancel-wake */ }
        catch (IOException) when (ct.IsCancellationRequested) { /* cancel-wake */ }
        // EOF on stdin — caller (e.g. Claude Desktop) has shut us down.
    }

    /// <summary>
    /// 007-cli-bridge-mode (T006): reads gateway messages, filters for frames
    /// in this session, and writes their raw bytes to stdout, flushing after
    /// every write (FR-004). The publisher side already preserves the MCP
    /// server's exact stdout bytes — Claude Desktop sees a byte-perfect echo.
    ///
    /// 031: when <paramref name="e2eSession"/> is non-null, incoming E2E frames
    /// (enc==1) are decrypted before writing to stdout. AEAD failure kills the
    /// bridge — never let bad ciphertext through.
    ///
    /// <paramref name="unconsumed"/> contains messages that arrived during the
    /// E2E handshake window and must be drained before reading from the channel.
    ///
    /// FlushAsync is not optional: Claude Desktop blocks on `readline()` and
    /// will not see a buffered response until the newline reaches its file
    /// descriptor — the flush makes each response immediately observable.
    /// </summary>
    private static async Task PumpFramesToStdoutAsync(
        NodeGatewayConnection connection,
        string sessionId,
        E2eAgentSession? e2eSession,
        Queue<GatewayToNodeMessage> unconsumed,
        CancellationToken ct)
    {
        await using var stdout = Console.OpenStandardOutput();

        // Replay any messages queued during the E2E handshake window first.
        while (unconsumed.TryDequeue(out var queued))
        {
            await ProcessInboundMessageAsync(queued, sessionId, e2eSession, stdout, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            var msg = await ReadNextAsync(connection, ct);
            await ProcessInboundMessageAsync(msg, sessionId, e2eSession, stdout, ct);
        }
    }

    /// <summary>
    /// 031: processes one inbound gateway message for the bridge's stdout pump.
    /// Handles Frame (decrypt if E2E), CloseSession, and ignores everything else.
    ///
    /// ANTI-DOWNGRADE (MAJOR-1): if a cipher is installed for this session and an inbound
    /// frame is NOT enc==1, the frame is treated as a downgrade/injection attack —
    /// we throw <see cref="DowngradeAttackException"/> to terminate the session immediately.
    /// A compromised cloud cannot inject plaintext into an established E2E session.
    /// </summary>
    private static async Task ProcessInboundMessageAsync(
        GatewayToNodeMessage msg,
        string sessionId,
        E2eAgentSession? e2eSession,
        Stream stdout,
        CancellationToken ct)
    {
        switch (msg.PayloadCase)
        {
            case GatewayToNodeMessage.PayloadOneofCase.Frame:
                if (msg.Frame.SessionId != sessionId) return;
                byte[] outBytes;
                if (e2eSession?.Cipher is { } cipher)
                {
                    // E2E session is established. ONLY accept enc==1 frames.
                    if (msg.Frame.Enc != 1)
                    {
                        // ANTI-DOWNGRADE: a frame without enc==1 arrived after an E2E session
                        // was established. This is either a compromised cloud injecting plaintext
                        // or a severe protocol violation. DROP and close the session immediately.
                        E2eConsole.DowngradeAttackDetected(sessionId, msg.Frame.Enc);
                        throw new DowngradeAttackException(
                            $"Downgrade/injection attack detected on session {sessionId}: " +
                            $"enc={msg.Frame.Enc} frame received after E2E was established.");
                    }
                    // 031: decrypt; throw on AEAD failure (kills bridge — correct behavior).
                    var metaBytes = msg.Frame.Meta?.ToByteArray() ?? Array.Empty<byte>();
                    outBytes = cipher.Open(
                        msg.Frame.Ciphertext.Span,
                        Korat.Protocol.E2eSessionCipher.DirServerToClient,
                        msg.Frame.SequenceNumber,
                        metaBytes);
                }
                else
                {
                    // No E2E session: plaintext path.
                    // Note: enc!=0 with no cipher means we received ciphertext we cannot
                    // decrypt — this is a protocol error; close the session.
                    if (msg.Frame.Enc != 0)
                    {
                        E2eConsole.EncCipherMismatch(sessionId, msg.Frame.Enc, hasCipher: false);
                        throw new DowngradeAttackException(
                            $"Protocol error on session {sessionId}: enc={msg.Frame.Enc} without cipher.");
                    }
                    outBytes = msg.Frame.Ciphertext.ToByteArray();
                }
                await stdout.WriteAsync(outBytes, ct);
                await stdout.FlushAsync(ct);
                break;

            case GatewayToNodeMessage.PayloadOneofCase.CloseSession:
                // Publisher revoked / shut down the session (006 G3 path,
                // bubbled up by the gateway as a CloseSession envelope).
                // The bridge must NOT silently wait for the next Frame —
                // surface the close as a task fault so RunBridgeLoopAsync
                // exits with a clear stderr line. Filter by sessionId so
                // unrelated sessions on the same stream don't kill us.
                if (msg.CloseSession.SessionId != sessionId) return;
                throw new GatewayDisconnectedException(
                    $"Session {sessionId} closed by gateway" +
                    (string.IsNullOrEmpty(msg.CloseSession.Reason)
                        ? "."
                        : $": {msg.CloseSession.Reason}"));

            default:
                // HeartbeatAck and anything else not addressed to a bridge —
                // ignore and continue draining.
                break;
        }
    }

    /// <summary>
    /// Test mode: send one UTF-8 frame, optionally wait for one inbound frame, then return.
    /// Used by automated E2E tests and quick manual smoke checks.
    /// </summary>
    private static async Task RunOneShotExchangeAsync(
        NodeGatewayConnection connection,
        string sessionId,
        string sendMessage,
        bool waitResponse,
        CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(sendMessage + "\n");
        await connection.SendFrameAsync(
            sessionId: sessionId,
            ciphertext: payload,
            sequenceNumber: 1,
            direction: "client_to_server",
            cancellationToken: ct);

        if (!waitResponse) return;

        while (!ct.IsCancellationRequested)
        {
            var msg = await ReadNextAsync(connection, ct);
            if (msg.PayloadCase != GatewayToNodeMessage.PayloadOneofCase.Frame) continue;
            if (msg.Frame.SessionId != sessionId) continue;

            var bytes = msg.Frame.Ciphertext.ToByteArray();
            // Strip a single trailing newline for readable demo output.
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\n', '\r');
            Console.WriteLine(text);
            return;
        }
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<GatewayToNodeMessage> ReadNextAsync(
        NodeGatewayConnection connection,
        CancellationToken ct)
    {
        if (!await connection.IncomingMessages.WaitToReadAsync(ct))
            throw new GatewayDisconnectedException("Cloud closed stream before next message.");
        connection.IncomingMessages.TryRead(out var msg);
        return msg!;
    }

    // ResolveServerIdAsync outcome: either an id string (found), null (not-found),
    // or it sets ExitCode + writes to stderr and returns null (error/auth paths).
    // handlerOverride is used by tests to inject a stub HttpMessageHandler.
    internal static async Task<string?> ResolveServerIdAsync(LocalIdentity identity, CliCredentials cliCreds, string displayName, CancellationToken ct, HttpMessageHandler? handlerOverride = null)
    {
        var result = await ResolveServerWithPublisherAsync(identity, cliCreds, displayName, ct, handlerOverride);
        return result?.ServerId;
    }

    /// <summary>
    /// 031: Resolves the MCP server and returns both the server id and the publisher node id
    /// (the latter used for the E2E handshake transcript). Returns null on any error (already reported).
    /// </summary>
    internal static async Task<(string ServerId, string? PublisherNodeId)?> ResolveServerWithPublisherAsync(
        LocalIdentity identity,
        CliCredentials cliCreds,
        string displayName,
        CancellationToken ct,
        HttpMessageHandler? handlerOverride = null)
    {
        using var http = CreateBearerHttpClient(cliCreds, handlerOverride);
        using var response = await http.GetAsync("api/space", ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            if (code is 401 or 403)
            {
                Console.Error.WriteLine("Not authenticated — run `korat login` to refresh your credentials.");
                Environment.ExitCode = 1;
            }
            else if (code >= 500)
            {
                Console.Error.WriteLine($"Could not reach Korat cloud at {cliCreds.CloudUrl}: server returned {code}.");
                Environment.ExitCode = 1;
            }
            else
            {
                Console.Error.WriteLine($"Unexpected response from Korat cloud: HTTP {code}.");
                Environment.ExitCode = 1;
            }
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), default, ct);
        if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
            return null;

        var matches = new List<(string Id, string? PublisherNodeId)>();
        foreach (var server in servers.EnumerateArray())
        {
            if (!server.TryGetProperty("displayName", out var nameEl))
                continue;

            var name = nameEl.GetString();
            if (!string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = ReadId(server.GetProperty("id"));
            if (id is null)
                continue;

            string? publisherNodeId = null;
            if (server.TryGetProperty("nodeId", out var nodeIdEl) ||
                server.TryGetProperty("publisherNodeId", out nodeIdEl))
                publisherNodeId = ReadId(nodeIdEl);

            matches.Add((id, publisherNodeId));
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"MCP server '{displayName}' was not found in your Space.");
            Environment.ExitCode = 1;
            return null;
        }

        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"Ambiguous server name '{displayName}': {matches.Count} servers share that name.");
            Console.Error.WriteLine("Disambiguate using --server-id (copy an ID from the list below):");
            foreach (var (id, pub) in matches)
                Console.Error.WriteLine($"  korat connect --server-id {id}  # publisher-runtime={pub ?? "(unknown)"}");
            Console.Error.WriteLine("Alternatively, rename one of the servers so names are unique.");
            Environment.ExitCode = 1;
            return null;
        }

        return (matches[0].Id, matches[0].PublisherNodeId);
    }

    private static async Task<SessionOpened?> WaitForApprovalAsync(
        NodeGatewayConnection connection,
        LocalIdentity identity,
        CliCredentials cliCreds,
        string serverId,
        string accessRequestId,
        string agentClientId,
        string requestId,
        bool bridge,
        CancellationToken ct)
    {
        var approveUrl = BrowserLauncher.BuildApproveUrl(cliCreds.CloudUrl, accessRequestId);
        // Bridge mode reserves stdout for raw JSON-RPC; route status lines to
        // stderr (FR-005). All other modes use stdout as before.
        var info = bridge ? Console.Error : Console.Out;
        info.WriteLine("Access pending owner approval.");
        info.WriteLine($"Approve URL: {approveUrl}");
        BrowserLauncher.TryOpen(approveUrl);

        using var http = CreateBearerHttpClient(cliCreds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);

            using var detailResponse = await http.GetAsync($"api/access-requests/{accessRequestId}", ct);
            if (!detailResponse.IsSuccessStatusCode) continue;

            using var detailDoc = await JsonDocument.ParseAsync(
                await detailResponse.Content.ReadAsStreamAsync(ct), default, ct);
            var status = detailDoc.RootElement.GetProperty("status").GetString();

            switch (status)
            {
                case "Approved":
                    info.WriteLine("Access approved.");
                    // Re-issue RequestSession on the existing stream so the gateway responds
                    // with SessionOpened now that an active grant exists.
                    await connection.SendRequestSessionAsync(requestId, agentClientId, serverId, ct);
                    var msg = await ReadNextAsync(connection, ct);
                    if (msg.PayloadCase != GatewayToNodeMessage.PayloadOneofCase.SessionOpened)
                    {
                        Console.Error.WriteLine($"Unexpected response after approval: {msg.PayloadCase}");
                        Environment.ExitCode = 1;
                        return null;
                    }
                    // Deferred-fix: return the whole SessionOpened so the caller can read the
                    // advisory peer_supports_e2e flag (with presence) for the bridge preflight.
                    return msg.SessionOpened;

                case "Denied":
                    Console.Error.WriteLine("Access denied by owner.");
                    Environment.ExitCode = 1;
                    return null;

                case "Expired":
                    Console.Error.WriteLine("Access request expired before owner decision.");
                    Environment.ExitCode = 1;
                    return null;

                case "Canceled":
                    Console.Error.WriteLine("Access request was canceled.");
                    Environment.ExitCode = 1;
                    return null;
            }
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// HttpClient configured with <c>Authorization: Bearer</c> from <see cref="CliCredentials"/>
    /// so requests to <c>/api/space</c> and <c>/api/access-requests/*</c> succeed.
    /// Tests can supply a <paramref name="handlerOverride"/> to intercept HTTP calls.
    /// </summary>
    /// <param name="store">
    /// Когда задан, пропуск читается заново на каждый запрос, а не берётся снимком. Нужно
    /// долгоживущим клиентам — мосту прежде всего: токен провайдера живёт часами, а мост
    /// сутками, и со снимком он через час молча переставал бы обновлять права.
    /// </param>
    internal static HttpClient CreateBearerHttpClient(
        CliCredentials creds,
        HttpMessageHandler? handlerOverride = null,
        CredentialStore? store = null)
    {
        var handler = handlerOverride ?? new HttpClientHandler();
        var outer = store is null ? handler : new FreshBearerHandler(store, creds, handler);

        var http = new HttpClient(outer, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(creds.CloudUrl.TrimEnd('/') + "/"),
        };

        // Заголовок по умолчанию нужен и при обновляющем обработчике: он ставит свой на
        // каждый запрос, но короткоживущие вызовы без store опираются именно на этот.
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        return http;
    }

    internal static string? ReadId(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("value", out var value) => value.GetString(),
            _ => null
        };
    }
}

/// <summary>
/// 031-relay-confidentiality (MAJOR-1): thrown when an established E2E session receives
/// a non-E2E frame — indicates a downgrade attack or injection by a compromised cloud.
/// The bridge terminates the session immediately on this exception.
/// </summary>
internal sealed class DowngradeAttackException : Exception
{
    public DowngradeAttackException(string message) : base(message) { }
}
