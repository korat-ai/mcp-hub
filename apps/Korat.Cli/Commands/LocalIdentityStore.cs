using System.Text.Json;
using Korat.Cli.Config;
using Korat.Domain;
using Korat.Cli;

namespace Korat.Cli.Commands;

/// <summary>Локальный config.json: NodeId, CloudUrl, SpaceId, McpServers (non-secret config).</summary>
public sealed class LocalIdentityStore
{
    private readonly string? _configPathOverride;

    /// <summary>Default store — resolves config.json via <see cref="KoratConfigPaths"/>.</summary>
    public LocalIdentityStore() { }

    /// <summary>
    /// Seam constructor — read &amp; write config.json at an explicit path instead of the
    /// OS-resolved location, so callers/tests can isolate from the real <c>~/.korat</c>.
    /// </summary>
    public LocalIdentityStore(string configPathOverride) => _configPathOverride = configPathOverride;

    public LocalIdentity LoadOrCreate()
    {
        var existingPath = _configPathOverride is not null
            ? (File.Exists(_configPathOverride) ? _configPathOverride : null)
            : KoratConfigPaths.FindExistingConfigPath();
        if (existingPath is not null)
        {
            try
            {
                var json = File.ReadAllText(existingPath);
                var loaded = JsonSerializer.Deserialize(json, KoratCliJsonContext.Default.LocalIdentity);
                if (loaded is not null)
                    return loaded;
            }
            catch
            {
                // Corrupt file — back it up and mint a new identity.
            }

            // Back up the corrupt/unreadable file so the user can inspect it.
            var backupPath = existingPath + $".bak.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try { File.Copy(existingPath, backupPath, overwrite: false); } catch { /* best-effort */ }
        }

        return Save(CreateNew());
    }

    /// <summary>
    /// Strict load for the live-reconcile path: reads and parses config.json and returns the
    /// identity, or THROWS when the file is missing, empty, or unparseable. Unlike
    /// <see cref="LoadOrCreate"/> it NEVER mints a fresh identity and never backs up — a
    /// transient/corrupt read (e.g. observing the file mid-atomic-save) must not be mistaken for
    /// "owner removed everything", which would drive the reconcile to unpublish every MCP server
    /// AND inference point (the latter is a hard delete + key revoke — see ConfigWatcher / B1).
    /// Callers (<see cref="Service.ConfigWatcher"/>) suppress the reconcile when this throws and
    /// retry on the next file event once the file is whole again.
    /// </summary>
    public LocalIdentity LoadAuthoritative()
    {
        var path = _configPathOverride is not null
            ? (File.Exists(_configPathOverride) ? _configPathOverride : null)
            : KoratConfigPaths.FindExistingConfigPath();
        if (path is null)
            throw new InvalidOperationException("config file not found");

        // A zero-length file is the classic mid-atomic-write observation — treat as a failed read.
        if (new FileInfo(path).Length == 0)
            throw new InvalidOperationException("config file is empty (transient mid-write?)");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, KoratCliJsonContext.Default.LocalIdentity)
            ?? throw new InvalidOperationException("config parsed as null");
    }

    public LocalIdentity Save(LocalIdentity identity)
    {
        var configPath = _configPathOverride ?? KoratConfigPaths.GetWritePath();
        var configDir = Path.GetDirectoryName(configPath)!;
        KoratConfigPaths.EnsureDirSecure(configDir);

        var json = JsonSerializer.Serialize(identity, KoratCliJsonContext.Default.LocalIdentity);

        // FIX-7: write atomically via a temp file in the same directory, then rename.
        // A non-atomic write leaves a partial file on crash/SIGKILL; ConfigWatcher can
        // read the half-written file, JSON parsing fails, and LoadOrCreate mints a fresh
        // identity with a new NodeId, which triggers unpublish-all on the next reconcile.
        var tempPath = Path.Combine(configDir, $".config.tmp.{Path.GetRandomFileName()}");
        try
        {
            File.WriteAllText(tempPath, json);

            // Restrict temp file mode to owner-only on Unix before the rename so the
            // final file has the correct permissions from the moment it becomes visible.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Best-effort — filesystem (e.g. SMB share) may not support chmod.
                }
            }

            // Atomic replace: on POSIX this is a rename(2) which is guaranteed atomic.
            // On Windows (when supported) File.Move with overwrite is best-effort atomic.
            File.Move(tempPath, configPath, overwrite: true);
        }
        catch
        {
            // Clean up the temp file if the move fails; rethrow so the caller knows.
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }

        return identity;
    }

    /// <summary>
    /// Returns false when the identity is missing required fields (NodeId, CloudUrl).
    /// Authentication is handled separately via <c>~/.korat/credentials</c> (SP4 access token).
    /// </summary>
    public static bool TryValidateIdentity(LocalIdentity identity, out string error)
    {
        if (string.IsNullOrWhiteSpace(identity.NodeId) || string.IsNullOrWhiteSpace(identity.CloudUrl))
        {
            error = "Missing local identity. Run `korat login` first.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Persists the server-authoritative SpaceId returned in <c>GatewayHello.resolved_space_id</c>
    /// into the stored identity, replacing any placeholder value (e.g. the legacy <c>"default"</c>
    /// seed or empty string written before the first successful connect).
    ///
    /// Callers (UpCommand, ConnectCommand) invoke this once after a successful
    /// <c>NodeGatewayConnection.ConnectAsync</c> so the client always stores its real Space.
    ///
    /// No-op when <paramref name="resolvedSpaceId"/> is null or empty (server did not populate
    /// the field — old deployment or partial rollout).
    /// </summary>
    public void PersistResolvedSpaceId(LocalIdentity identity, string? resolvedSpaceId)
    {
        if (string.IsNullOrEmpty(resolvedSpaceId))
            return;

        identity.SpaceId = resolvedSpaceId;
        Save(identity);
    }

    private static LocalIdentity CreateNew() => new()
    {
        SpaceId = string.Empty,
        NodeId = NodeId.New().Value,
        CloudUrl = "http://localhost:5191",
    };
}

/// <summary>
/// Local non-secret node configuration persisted between CLI runs.
/// Authentication credentials are stored separately in <c>~/.korat/credentials</c>
/// as <c>CliCredentials</c> after a successful <c>korat login</c> (SP4).
/// </summary>
public sealed class LocalIdentity
{
    /// <summary>
    /// Server-authoritative SpaceId for this node.
    /// Starts as empty string on a fresh install (before first successful connect).
    /// Populated with the real Space on first connect via
    /// <see cref="LocalIdentityStore.PersistResolvedSpaceId"/>.
    /// Legacy configs written before this change may still contain <c>"default"</c>;
    /// those are back-compat-loaded and overwritten on next connect.
    /// </summary>
    public string SpaceId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string CloudUrl { get; set; } = "http://localhost:5191";
    /// <summary>
    /// gRPC endpoint. Dev cloud binds gRPC to a separate HTTP/2-only port because Kestrel
    /// can't multiplex HTTP/1.1 and HTTP/2 on the same plain-text endpoint. Production
    /// uses a single TLS endpoint and this can equal <see cref="CloudUrl"/>.
    /// </summary>
    public string CloudGrpcUrl { get; set; } = "http://localhost:5192";

    /// <summary>
    /// MCP servers registered locally via `korat mcp add`. Persisted client-side so
    /// `korat up --serve <name>` can resolve the launch command without an extra
    /// cloud round-trip and without needing a new gateway message type.
    /// </summary>
    public List<LocalMcpServer> McpServers { get; set; } = new();

    /// <summary>
    /// 017: named agent identities used by `korat connect --agent &lt;name&gt;`.
    /// Each agent has its own NodeId (distinct from the publisher NodeId) so the
    /// cloud routing table keeps agent and publisher streams separate, fixing the
    /// loopback bug when both run on the same machine.
    /// </summary>
    public List<AgentIdentity> Agents { get; set; } = new();

    /// <summary>
    /// 029: inference points registered via `korat agent add`.
    /// Each entry describes a headless agent that can serve OpenAI-compatible
    /// completions over the relay. Missing from legacy config.json → defaults to
    /// empty list (back-compat: JSON deserialization of an absent array field
    /// uses the property initializer = new List).
    /// </summary>
    public List<InferencePointIdentity> InferencePoints { get; set; } = new();
}

/// <summary>
/// Local-side record of an MCP server this CLI has published. Mirrors enough of the
/// cloud-side <c>McpServer</c> for the stdio bridge to spawn it on demand.
/// </summary>
public sealed class LocalMcpServer
{
    public string DisplayName { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
}

/// <summary>
/// 017: a named agent identity stored in config.json.
/// <para>
/// Each agent (e.g. "default", "cursor", "claude") is assigned its own
/// <see cref="NodeId"/> and <see cref="AgentClientId"/> on first use. Both are
/// distinct from the publisher's NodeId so that publisher and agent streams do not
/// collide in the cloud routing table and frames are delivered correctly.
/// </para>
/// </summary>
public sealed class AgentIdentity
{
    /// <summary>Human-readable name, e.g. "default", "cursor", "claude".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Stable node address for this agent — sent as NodeHello.NodeId when
    /// `korat connect --agent &lt;name&gt;` opens the gateway stream.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Agent-client identity sent in RequestSession. The cloud requires
    /// AgentClientGrain.NodeId == conn.NodeId (this agent's NodeId) for the
    /// mismatch guard. On empty grain state the cloud allows through (deferred
    /// persistent-state hardening tracked separately).
    /// </summary>
    public string AgentClientId { get; set; } = string.Empty;

    /// <summary>
    /// PR-5 (agent-id-identity, additive): the cloud-side <c>Agent.Id</c> this local
    /// identity is bound to, when known. Recorded by
    /// <see cref="Commands.ConnectCommand.ResolveOrCreateAgent"/> on create/migrate, from the
    /// hidden <c>--agent-id</c> value the hosted-agent bridge (korat space-bridge MCP config)
    /// passes alongside <c>--agent agent-{name}-{id8}</c>. Null for: identities created
    /// before this field existed, plain (non-hosted) `korat connect --agent &lt;name&gt;`
    /// usage that never supplies --agent-id, and a mixed-version rollout window. Its purpose
    /// is a reuse guard — once recorded, a name reused under a DIFFERENT AgentId (e.g. an
    /// id8-slot collision after delete+recreate; fable #188) is detected and the stale slot
    /// is replaced with a fresh identity rather than inherited. See ResolveOrCreateAgent for
    /// the full compat-shim + mismatch design.
    /// </summary>
    public string? AgentId { get; set; }
}

/// <summary>
/// 029: a headless agent registered as an Inference Point via `korat agent add`.
/// Persisted in config.json alongside <see cref="AgentIdentity"/> and
/// <see cref="LocalMcpServer"/>; mirrors the same local-registry pattern.
///
/// The <see cref="InferencePointId"/> is assigned by the cloud after
/// <c>PublishInferencePoint</c> is acked; it is empty until the node service
/// has successfully registered with the cloud for the first time.
/// </summary>
public sealed class InferencePointIdentity
{
    /// <summary>
    /// User-chosen name, e.g. "claude", "my-claude". Becomes the
    /// <c>{agent_name}</c> path segment in the inference URL.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The agent binary kind: "claude" | "codex" | "cursor" | etc.
    /// Determines which <c>IInferenceProvider</c> is instantiated.
    /// </summary>
    public string AgentKind { get; set; } = string.Empty;

    /// <summary>
    /// Publisher node this point lives on. Matches the publisher's
    /// <see cref="LocalIdentity.NodeId"/> so the cloud can route requests
    /// to the correct connected stream.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Cloud-assigned stable id for this point. Empty until the node service
    /// has sent PublishInferencePoint and received the ack.
    /// Assigned once; stable across reconnects (idempotent by (node, name)).
    /// </summary>
    public string InferencePointId { get; set; } = string.Empty;

    /// <summary>Model ids discovered from the agent binary (e.g. via <c>claude models list</c>).</summary>
    public List<string> Models { get; set; } = new();

    /// <summary>Default model used when the request omits <c>model</c> or sends an unknown one.</summary>
    public string DefaultModel { get; set; } = string.Empty;
}
