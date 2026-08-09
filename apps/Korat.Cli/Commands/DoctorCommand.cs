using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korat.Cli.Auth;
using Korat.Cli.Util;

namespace Korat.Cli.Commands;

/// <summary>
/// <c>korat doctor</c> — read-only diagnostic command. Runs local checks (credentials,
/// env-coherence, service, claude-on-path, claude-login, agents-dir, agents-dir-orphans)
/// plus cloud/network checks (cloud-auth, node-presence, agents-stale, grpc-gateway,
/// version) and reports each as ✅ ok / ⚠️ warn / ❌ fail, with an optional one-line fix hint.
///
/// Exit code: 0 when no check is "fail" (warn-only is non-fatal), 1 otherwise.
/// <c>--json</c> emits <c>{"ok":bool,"checks":[{"id","status","detail","fix"?}]}</c>.
///
/// Secrets hygiene: NEVER prints <see cref="CliCredentials.AccessToken"/>,
/// <see cref="CliCredentials.RefreshToken"/> or any other secret value — only scope/expiry/urls,
/// which are safe to display.
///
/// Network checks degrade gracefully when offline: each reports its own "fail"/"warn" with
/// an "unreachable"-flavoured detail, but never throws — local checks are still reported and
/// the overall report always completes.
///
/// Review 2026-07-04 ("doctor слеп к hosted-agents"): a node with zero registered hosted
/// agents (config.json <c>InferencePoints</c> empty) previously reported all-green even
/// when it could never actually serve one — <c>claude</c> missing from PATH, the
/// subscription logged out, or <c>~/.korat/agents</c> not writable were invisible. The
/// checks below only run when at least one hosted agent IS registered — nothing to
/// diagnose otherwise — mirroring the existing 0..N "agents-stale" pattern (no baseline
/// "ok" clutters a report that has nothing to say):
/// <list type="bullet">
/// <item><b>claude-on-path</b> (warn): only when a "claude"-kind agent is registered —
/// the <c>claude</c> binary must be resolvable via PATH for a hosted turn to run at all.</item>
/// <item><b>claude-login</b> (warn): reuses <see cref="ClaudeInferenceProvider.ProbeLoginAsync"/>
/// (the SAME probe <c>korat agent add</c> already uses) — skipped when claude-on-path itself
/// warned (a doomed probe would just re-report the same "binary missing" via a spawn
/// failure).</item>
/// <item><b>agents-dir</b> (fail): the per-agent config root (<c>~/.korat/agents</c>) must
/// be writable for ANY registered hosted agent, regardless of kind — a hosted turn cannot
/// even start its own per-agent config/workdir prep otherwise, so this is fatal rather than
/// warn-only.</item>
/// <item><b>agents-dir-orphans</b> (warn, 0..N, informational): a directory under
/// <c>~/.korat/agents</c> with no matching registered agent name — stranded by an agent
/// rename or delete (see <see cref="ClaudeInferenceProvider.PrepareHostedAgentConfig"/>'s
/// XML doc for the age-sweep this complements).</item>
/// </list>
/// </summary>
public static class DoctorCommand
{
    // A2: agent-kind nodes idle longer than this are flagged (warn-only) as possibly having
    // a disabled bridge in the MCP client config — see the "agents-stale" check.
    private static readonly TimeSpan StaleAgentThreshold = TimeSpan.FromDays(7);

    // A2: fallback presence-staleness window used only if the cloud omits
    // presenceStaleSeconds (legacy/partial deployment) — mirrors the server default
    // (see Korat.Domain.NodePresenceRules.StaleThreshold).
    private const int DefaultPresenceStaleSeconds = 90;

    private static readonly TimeSpan GrpcProbeTimeout = TimeSpan.FromSeconds(2);

    // Final-review fix: the claude-login probe spawns `claude -p ping`, which can hang forever
    // on a stuck/wedged claude binary — with no deadline that would hang `korat doctor` itself.
    // Bound it so a stuck probe degrades to a "timed out" warning (never fatal) in ~30s while
    // still giving a healthy claude ample time to answer a trivial ping.
    private static readonly TimeSpan LoginProbeTimeout = TimeSpan.FromSeconds(30);

    // Final-review LOW fix: the cloud-facing HttpClients below previously had no explicit
    // Timeout, so a blackholed network (no RST, no response) left `korat doctor` hanging for
    // the BCL default of 100s per check instead of degrading gracefully like every other
    // network check here. 10s is generous for a healthy cloud round-trip while still keeping
    // the whole report snappy when the network is actually down.
    private static readonly TimeSpan CloudCheckTimeout = TimeSpan.FromSeconds(10);

    public static Command Create()
    {
        var command = new Command("doctor", "Diagnose common Korat setup problems");
        var jsonOption = new Option<bool>("--json",
            "Output machine-readable JSON instead of the human report");
        command.AddOption(jsonOption);
        command.SetHandler(async (bool json) =>
        {
            Environment.ExitCode = await RunAsync(json);
        }, jsonOption);
        return command;
    }

    /// <summary>
    /// Testable core — mirrors <see cref="LoginCommand.ExecuteAsync"/>'s override pattern:
    /// parameters left <see langword="null"/> use real production objects, tests pass stubs.
    /// Returns the process exit code (0 = no failing check, 1 = at least one failing check).
    /// </summary>
    internal static async Task<int> RunAsync(
        bool json,
        CredentialStore? credentialStore = null,
        LocalIdentityStore? identityStore = null,
        HttpMessageHandler? handlerOverride = null,
        Func<string, int, TimeSpan, Task<bool>>? tcpProbe = null,
        TextWriter? outputWriter = null,
        TimeSpan? loginProbeTimeout = null,
        CancellationToken ct = default)
    {
        var output = outputWriter ?? Console.Out;
        var credStore = credentialStore ?? new CredentialStore();
        var idStore = identityStore ?? new LocalIdentityStore();

        var checks = new List<DoctorCheck>();

        // ── credentials ──────────────────────────────────────────────────────
        var creds = await RunCredentialsCheckAsync(credStore, checks, ct);

        // ── local identity (config.json) — loaded once, shared by env-coherence,
        // node-presence and grpc-gateway below. A load failure is reported as the
        // "env-coherence" check (that's the check whose whole job is comparing this
        // file against credentials) and every check that needs it is skipped.
        var identity = LoadIdentityForChecks(idStore, checks);

        // ── env-coherence ────────────────────────────────────────────────────
        // Only meaningful once we know both sides. If credentials failed to load,
        // the "credentials" check above already explains the root cause — skip
        // env-coherence rather than reporting a second, less useful failure.
        if (creds is not null && identity is not null)
            RunEnvCoherenceCheck(creds, identity, checks);

        // ── service ──────────────────────────────────────────────────────────
        await RunServiceCheckAsync(checks, ct);


        // ── cloud-auth / node-presence / agents-stale (need an authenticated client) ──
        if (creds is not null)
        {
            using var http = BuildAuthenticatedHttpClient(creds, handlerOverride);
            await RunCloudAuthCheckAsync(http, checks, ct);
            await RunNodePresenceAndAgentsStaleChecksAsync(http, identity, checks, ct);
        }

        // ── grpc-gateway (only needs local identity, not credentials) ───────────
        if (identity is not null)
            await RunGrpcGatewayCheckAsync(identity, tcpProbe, checks);

        // ── version (needs neither credentials nor identity) ────────────────────
        await RunVersionCheckAsync(handlerOverride, checks);

        var ok = checks.All(c => c.Status != "fail");
        var report = new DoctorReport(ok, checks);

        if (json)
        {
            await output.WriteLineAsync(
                JsonSerializer.Serialize(report, KoratCliJsonContext.Default.DoctorReport));
        }
        else
        {
            WriteHumanReport(checks, output);
        }

        return ok ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Individual checks
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads credentials and appends the "credentials" check. Returns the loaded credentials
    /// (or <see langword="null"/> when missing/unreadable) so later checks can reuse them
    /// without a second disk read.
    /// </summary>
    private static async Task<CliCredentials?> RunCredentialsCheckAsync(
        CredentialStore credStore, List<DoctorCheck> checks, CancellationToken ct)
    {
        CliCredentials? creds;
        try
        {
            creds = await credStore.LoadAsync(ct);
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("credentials", "fail",
                $"could not read credentials file: {ex.Message}",
                "run `korat login`"));
            return null;
        }

        if (creds is null)
        {
            checks.Add(new DoctorCheck("credentials", "fail",
                "no credentials — run `korat login`",
                "run `korat login`"));
            return null;
        }

        // Сюда учётные данные приходят уже после попытки обновления: LoadAsync обновляет
        // истёкший пропуск сам. Значит, истёкший пропуск здесь — это не «пора обновить», а
        // «обновить не вышло», и сказать надо именно это.
        if (creds.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var why = string.IsNullOrWhiteSpace(creds.RefreshToken)
                ? "no refresh token was issued"
                : "renewal did not succeed — the sign-in provider is unreachable or the session was ended";
            checks.Add(new DoctorCheck("credentials", "fail",
                $"access token expired {creds.ExpiresAt:yyyy-MM-dd HH:mm} UTC ({why}); cloud {creds.CloudUrl}",
                "run `korat login`"));
            return creds;
        }

        var renewal = string.IsNullOrWhiteSpace(creds.RefreshToken)
            ? ", no automatic renewal"
            : ", renews automatically";
        var issuer = string.IsNullOrWhiteSpace(creds.Issuer) ? string.Empty : $", issued by {creds.Issuer}";
        checks.Add(new DoctorCheck("credentials", "ok",
            $"valid — scope={creds.Scope}, expires {creds.ExpiresAt:yyyy-MM-dd HH:mm} UTC{renewal}{issuer}",
            null));
        return creds;
    }

    /// <summary>
    /// Loads config.json once for the whole report (env-coherence, node-presence,
    /// grpc-gateway all need it). A load failure is reported here as the "env-coherence"
    /// check — that's the check whose job is comparing this file against credentials — so
    /// callers simply skip whatever they need <see cref="LocalIdentity"/> for when this
    /// returns <see langword="null"/>.
    /// </summary>
    private static LocalIdentity? LoadIdentityForChecks(LocalIdentityStore idStore, List<DoctorCheck> checks)
    {
        try
        {
            return idStore.LoadOrCreate();
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("env-coherence", "fail",
                $"could not read local runtime config: {ex.Message}",
                "run `korat login` to recreate it"));
            return null;
        }
    }

    /// <summary>
    /// Compares the cloud that issued the CLI token with the cloud this machine's node
    /// (config.json) publishes to. A mismatch is the classic "creds point at prod, node
    /// publishes to dev" split-brain — the fix hint names BOTH urls and the exact command.
    /// </summary>
    private static void RunEnvCoherenceCheck(
        CliCredentials creds, LocalIdentity identity, List<DoctorCheck> checks)
    {
        var credsUrl = creds.CloudUrl.TrimEnd('/');
        var nodeUrl = identity.CloudUrl.TrimEnd('/');

        if (string.Equals(credsUrl, nodeUrl, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new DoctorCheck("env-coherence", "ok",
                $"credentials and this machine's publisher runtime both point at {creds.CloudUrl}",
                null));
            return;
        }

        checks.Add(new DoctorCheck("env-coherence", "fail",
            $"credentials → {creds.CloudUrl} but this machine's publisher runtime publishes to {identity.CloudUrl}",
            $"run `korat login --cloud {identity.CloudUrl}` to align (or `korat login --cloud {creds.CloudUrl}` if that one is intended)"));
    }

    /// <summary>
    /// Reuses <see cref="ServiceCommand.TryGetController"/> (the same detection `korat status`
    /// and `korat service status` use) rather than duplicating OS-controller selection here.
    /// Never fails the whole report — an absent background service is a soft warning; the CLI
    /// still works via `korat connect --bridge` per-session.
    /// </summary>
    private static async Task RunServiceCheckAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        try
        {
            var ctrl = ServiceCommand.TryGetController();
            if (ctrl is null)
            {
                checks.Add(new DoctorCheck("service", "warn",
                    "OS service management is not supported on this platform",
                    "use `korat connect --bridge` per session instead"));
                return;
            }

            var status = await ctrl.GetStatusAsync(ct);
            if (status.IsInstalled && status.IsRunning)
            {
                checks.Add(new DoctorCheck("service", "ok",
                    status.Detail ?? "installed and running", null));
            }
            else
            {
                checks.Add(new DoctorCheck("service", "warn",
                    status.Detail ?? "not installed/running",
                    "run `korat service install`"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("service", "warn",
                $"could not determine service status: {ex.Message}",
                "run `korat service status`"));
        }
    }

    /// <summary>
    /// Creates <paramref name="dir"/> if missing and probes it with a real write+delete of a
    /// throwaway file — the only reliable cross-platform writability test (permission bits
    /// alone can be misleading, e.g. under ACLs or when running as a different effective
    /// user than the owner). Any failure (blocked by an existing file at that path,
    /// read-only filesystem, permission denied, ...) reports not-writable.
    /// </summary>
    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probePath = Path.Combine(dir, $".doctor-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds an authenticated <see cref="HttpClient"/> against <paramref name="creds"/>'s
    /// cloud, mirroring <see cref="StatusCommand.ExecuteAsync"/>'s idiom (BaseAddress +
    /// Bearer header, disposeHandler only when we own the handler). Final-review LOW fix:
    /// bounded by <see cref="CloudCheckTimeout"/> so a blackholed network fails the check
    /// in ~10s instead of the BCL's 100s default. Internal (not private) so tests can assert
    /// the timeout directly without waiting out a real hang.
    /// </summary>
    internal static HttpClient BuildAuthenticatedHttpClient(CliCredentials creds, HttpMessageHandler? handlerOverride)
    {
        var handler = handlerOverride ?? new HttpClientHandler();
        var http = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(creds.CloudUrl.TrimEnd('/') + "/"),
            Timeout = CloudCheckTimeout,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        return http;
    }

    /// <summary>
    /// GET <c>/api/auth/me</c> with the CLI's Bearer token. 200 → ok (email surfaced so the
    /// owner can confirm which account they're authenticated as); 401 → the token doesn't
    /// belong to this cloud (classic symptom: stale creds after a login on a different
    /// cloud); any other failure (bad status, timeout, DNS, TLS, …) → "unreachable".
    /// </summary>
    private static async Task RunCloudAuthCheckAsync(HttpClient http, List<DoctorCheck> checks, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync("/api/auth/me", ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                checks.Add(new DoctorCheck("cloud-auth", "fail",
                    "token not valid for this cloud",
                    // Главный новый отказ — действующий токен, чей владелец ни разу не входил
                    // сюда в браузере. Повторный login его не лечит: связь учёток создаётся
                    // только браузерным входом. Совет «войдите ещё раз» отправлял бы человека
                    // по кругу ровно в том случае, который стал самым частым.
                    "sign in to this cloud in a browser once to link your account, then re-run `korat login`"));
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                checks.Add(new DoctorCheck("cloud-auth", "fail",
                    $"cloud returned {(int)resp.StatusCode} {resp.StatusCode}",
                    "re-run `korat login`"));
                return;
            }

            var me = await resp.Content.ReadFromJsonAsync(KoratCliJsonContext.Default.AuthMeDto, ct);
            checks.Add(new DoctorCheck("cloud-auth", "ok",
                $"authenticated as {(string.IsNullOrEmpty(me?.Email) ? "(unknown email)" : me.Email)}",
                null));
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("cloud-auth", "fail",
                $"unreachable — {ex.Message}",
                "check network/firewall, or the cloud may be down"));
        }
    }

    /// <summary>
    /// GET <c>/api/space</c> once and derives two checks from it:
    /// <list type="bullet">
    /// <item><b>node-presence</b>: is THIS machine's node (by NodeId) in the space and fresh —
    /// effective freshness from <c>LastSeenAt</c> age vs the payload's
    /// <c>presenceStaleSeconds</c>, never the raw stored <c>Status</c> (019 rule).</item>
    /// <item><b>agents-stale</b> (0..N, warn-only): every agent-kind node idle longer than
    /// <see cref="StaleAgentThreshold"/> — its bridge may simply be disabled in the MCP
    /// client config, so this never fails the whole report.</item>
    /// </list>
    /// A network failure here reports "node-presence" as fail/"unreachable" and skips
    /// agents-stale entirely (nothing to derive it from).
    /// </summary>
    private static async Task RunNodePresenceAndAgentsStaleChecksAsync(
        HttpClient http, LocalIdentity? identity, List<DoctorCheck> checks, CancellationToken ct)
    {
        if (identity is null)
            return; // could not load local config — already reported under "env-coherence"

        SpaceOverviewResponse? space;
        try
        {
            space = await http.GetFromJsonAsync("/api/space", KoratCliJsonContext.Default.SpaceOverviewResponse, ct);
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("node-presence", "fail",
                $"unreachable — {ex.Message}",
                "check network/firewall, or the cloud may be down"));
            return;
        }

        if (space is null)
        {
            checks.Add(new DoctorCheck("node-presence", "fail",
                "cloud returned an empty space overview",
                "re-run `korat login`"));
            return;
        }

        var staleSeconds = space.PresenceStaleSeconds > 0 ? space.PresenceStaleSeconds : DefaultPresenceStaleSeconds;
        // Final-review LOW fix: use the cloud's serverTime as "now" for freshness math
        // instead of this machine's local clock. The payload is stamped with serverTime
        // specifically to let callers avoid trusting local UtcNow (019) — a skewed local
        // clock could otherwise make a fresh node look stale (or vice versa). Falls back to
        // local UtcNow only when an old/partial cloud omits the field.
        var now = space.ServerTime ?? DateTimeOffset.UtcNow;
        var own = space.Nodes.FirstOrDefault(n =>
            string.Equals(n.Id?.Value, identity.NodeId, StringComparison.Ordinal));

        if (own is null)
        {
            checks.Add(new DoctorCheck("node-presence", "fail",
                "this machine's publisher runtime is not present in the Space",
                "run `korat service install` / check `korat service status`"));
        }
        else
        {
            var age = own.LastSeenAt is { } seen ? now - seen : (TimeSpan?)null;
            if (age is null || age.Value.TotalSeconds > staleSeconds)
            {
                checks.Add(new DoctorCheck("node-presence", "fail",
                    $"this machine's publisher runtime is offline (last seen {DescribeLastSeen(own.LastSeenAt)})",
                    "run `korat service install` / check `korat service status`"));
            }
            else
            {
                checks.Add(new DoctorCheck("node-presence", "ok",
                    $"this machine's publisher runtime is online (last seen {DescribeLastSeen(own.LastSeenAt)})",
                    null));
            }
        }

        // ── agents-stale (warn-only, 0..N entries) ──────────────────────────────
        foreach (var node in space.Nodes)
        {
            if (!string.Equals(node.Kind, "agent", StringComparison.OrdinalIgnoreCase))
                continue;
            if (node.LastSeenAt is not { } lastSeen || now - lastSeen <= StaleAgentThreshold)
                continue;

            checks.Add(new DoctorCheck("agents-stale", "warn",
                $"consumer runtime '{node.DisplayName}' last seen {lastSeen:yyyy-MM-dd} — its bridge may be disabled in the MCP client config",
                "check that consumer's MCP client config and re-run `korat connect --agent <name> --bridge`"));
        }
    }

    private static string DescribeLastSeen(DateTimeOffset? lastSeenAt) =>
        lastSeenAt is { } seen ? seen.ToString("yyyy-MM-dd HH:mm") + " UTC" : "never";

    /// <summary>
    /// TCP-connects to <see cref="LocalIdentity.CloudGrpcUrl"/>'s host:port within 2s. This is
    /// the gRPC gateway the node service/bridge streams frames over — separate from the REST
    /// API checked by cloud-auth/node-presence, so a firewall that only blocks the gRPC port
    /// (or vice versa) is diagnosed correctly instead of being lumped into one generic check.
    /// </summary>
    private static async Task RunGrpcGatewayCheckAsync(
        LocalIdentity identity, Func<string, int, TimeSpan, Task<bool>>? tcpProbe, List<DoctorCheck> checks)
    {
        string host;
        int port;
        try
        {
            var uri = new Uri(identity.CloudGrpcUrl);
            host = uri.Host;
            port = uri.IsDefaultPort
                ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : uri.Port;
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("grpc-gateway", "fail",
                $"could not parse gateway url '{identity.CloudGrpcUrl}': {ex.Message}",
                "run `korat login` to reset the local config"));
            return;
        }

        var probe = tcpProbe ?? DefaultTcpProbeAsync;
        bool reachable;
        try
        {
            reachable = await probe(host, port, GrpcProbeTimeout);
        }
        catch
        {
            reachable = false;
        }

        if (reachable)
        {
            checks.Add(new DoctorCheck("grpc-gateway", "ok",
                $"gateway reachable at {host}:{port}", null));
        }
        else
        {
            checks.Add(new DoctorCheck("grpc-gateway", "fail",
                $"gateway unreachable at {host}:{port}",
                "gateway unreachable — check network/firewall"));
        }
    }

    private static async Task<bool> DefaultTcpProbeAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compares the running CLI's version against the latest published release, reusing
    /// <see cref="UpgradeCommand.ResolveLatestVersionAsync"/> — the SAME redirect-resolve
    /// logic <c>korat upgrade</c> uses — so the two surfaces can never drift. Never fails the
    /// report: an outdated CLI (or a machine that can't reach GitHub) is a nudge, not a
    /// diagnosed problem.
    /// </summary>
    private static async Task RunVersionCheckAsync(HttpMessageHandler? handlerOverride, List<DoctorCheck> checks)
    {
        string? latest;
        try
        {
            // Final-review LOW fix: bound the doctor's own resolve call with CloudCheckTimeout
            // (default null=100s is unchanged for `korat upgrade` itself — see the XmlDoc on
            // ResolveLatestVersionAsync).
            latest = await UpgradeCommand.ResolveLatestVersionAsync(handlerOverride, CloudCheckTimeout);
        }
        catch
        {
            latest = null;
        }

        if (latest is null)
        {
            checks.Add(new DoctorCheck("version", "warn",
                "could not check for updates (offline?)", null));
            return;
        }

        var current = CliVersion.Bare();
        var latestClean = latest.TrimStart('v', 'V');

        if (string.Equals(current, latestClean, StringComparison.Ordinal))
        {
            checks.Add(new DoctorCheck("version", "ok", $"up to date (v{current})", null));
        }
        else
        {
            // Final-review LOW fix: `latest` already carries the "v" prefix (e.g. "v0.4.1")
            // — prepending another one produced "vv0.4.1 available".
            checks.Add(new DoctorCheck("version", "warn",
                $"{latest} available (running v{current})",
                "run `korat upgrade`"));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Report rendering
    // ─────────────────────────────────────────────────────────────────────────

    private static void WriteHumanReport(List<DoctorCheck> checks, TextWriter output)
    {
        output.WriteLine("── Korat Doctor ─────────────────────────────────────────");
        foreach (var c in checks)
        {
            var glyph = c.Status switch
            {
                "ok" => "✅",
                "warn" => "⚠️",
                _ => "❌",
            };
            // Keep established JSON ids for automation, while the human report uses the
            // public relay vocabulary.
            var displayId = c.Id switch
            {
                "node-presence" => "runtime-presence",
                "agents-stale" => "consumer-runtimes-stale",
                _ => c.Id,
            };
            output.WriteLine($"{glyph} {displayId}: {c.Detail}");
            if (!string.IsNullOrEmpty(c.Fix))
                output.WriteLine($"   fix: {c.Fix}");
        }
        output.WriteLine("─────────────────────────────────────────────────────────");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DTOs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One diagnostic result. <see cref="Status"/> is one of "ok"|"warn"|"fail".</summary>
    internal sealed record DoctorCheck(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("detail")] string Detail,
        [property: JsonPropertyName("fix")] string? Fix);

    /// <summary><c>--json</c> report shape: <c>{"ok":bool,"checks":[...]}</c>.</summary>
    internal sealed record DoctorReport(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("checks")] List<DoctorCheck> Checks);

    /// <summary>A2: minimal parse of <c>GET /api/auth/me</c> — only the email is displayed.</summary>
    internal sealed class AuthMeDto
    {
        public string Email { get; set; } = string.Empty;
    }
}
