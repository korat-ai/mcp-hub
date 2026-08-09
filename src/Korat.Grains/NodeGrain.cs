using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace Korat.Grains;

/// <summary>Grain подключённого узла: presence и привязка к gateway.</summary>
public sealed class NodeGrain(IMetadataRepository repository, ILogger<NodeGrain> logger) : Grain, INodeGrain
{
    private Node _state = new()
    {
        Id = default,
        SpaceId = default,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    // 029: VOLATILE capability set advertised in NodeHello. Reset on activation (= reconnect).
    private readonly HashSet<string> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var persisted = await repository.GetNodeAsync(new NodeId(this.GetPrimaryKeyString()), cancellationToken);
        if (persisted is not null)
        {
            _state = persisted;
            // cloud-m9: repopulate the volatile _capabilities set from the persisted capabilities
            // list so HasCapabilityAsync returns the correct answer even on cross-silo activations
            // (e.g. a console ListSessionsAsync or an OrleansSessionRouteResolver cache miss that
            // activates the grain on a silo that never processed the node's Hello handshake).
            _capabilities.Clear();
            foreach (var cap in _state.Capabilities)
                _capabilities.Add(cap);
        }
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task<Node> ConnectAsync(
        SpaceId spaceId, string displayName, GatewayId gatewayId,
        NodeKind kind = NodeKind.Publisher,
        IReadOnlyList<string>? capabilities = null,
        string? hostname = null,
        string? os = null,
        string? arch = null,
        string? cliVersion = null)
    {
        var now = DateTimeOffset.UtcNow;
        // FIX: preserve push-token fields across reconnects. Before this fix, rebuilding _state
        // from scratch dropped PushToken/PushPlatform/PushTokenUpdatedAt so a reconnect (e.g.
        // after the app returns to foreground) silently cleared wake capability until the next
        // RegisterPushToken message arrived. Copying from the prior _state keeps the token intact.
        // cloud-m9: build the capability list from the Hello message so it can be persisted.
        var capList = capabilities is not null ? [.. capabilities] : (List<string>)[];
        // 029: update volatile capability set from the Hello message.
        _capabilities.Clear();
        foreach (var cap in capList)
            _capabilities.Add(cap);

        _state = new Node
        {
            Id = new NodeId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            DisplayName = displayName,
            Status = NodeStatus.Online,
            Kind = kind,
            CurrentGatewayId = gatewayId,
            LastSeenAt = now,
            CreatedAt = _state.CreatedAt == default ? now : _state.CreatedAt,
            UpdatedAt = now,
            // Preserve push-token fields from the previous state so a reconnect doesn't
            // wipe wake capability until the next RegisterPushToken arrives.
            PushToken = _state.PushToken,
            PushPlatform = _state.PushPlatform,
            PushTokenUpdatedAt = _state.PushTokenUpdatedAt,
            // cloud-m9: persist capabilities so OnActivateAsync can repopulate the volatile
            // _capabilities set on cross-silo activations (e.g. HasCapabilityAsync calls from
            // another silo's RequestSession handler). Empty list = no capabilities.
            Capabilities = capList,
            // Node host metadata: refreshed on EVERY hello (not preserved from prior _state like
            // PushToken) — a hello that omits them means the connecting CLI genuinely has none
            // to report (legacy CLI), so the previous value is stale and should be cleared too.
            Hostname = hostname,
            Os = os,
            Arch = arch,
            CliVersion = cliVersion,
            // B3-review (blocker): preserve the owner-editable Note across reconnects, like the
            // push-token fields above. Note is set/cleared ONLY via PATCH /api/nodes/{id} — a
            // hello must never touch it (OnActivateAsync hydrates _state.Note from the repo, so
            // this holds even on fresh/cross-silo activations).
            Note = _state.Note,
        };

        await repository.UpsertNodeAsync(_state);
        return _state;
    }

    /// <summary>029: true when this node advertised the given capability in its last NodeHello.</summary>
    public Task<bool> HasCapabilityAsync(string capability) =>
        Task.FromResult(_capabilities.Contains(capability));

    public async Task HeartbeatAsync(GatewayId gatewayId)
    {
        if (_state.Id == default)
            return;

        _state.Status = NodeStatus.Online;
        _state.CurrentGatewayId = gatewayId;
        _state.LastSeenAt = DateTimeOffset.UtcNow;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(_state);
    }

    public async Task MarkOfflineAsync()
    {
        _state.Status = NodeStatus.Offline;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(_state);
    }

    public Task<Node> GetAsync() => Task.FromResult(_state);

    public async Task RegisterPushTokenAsync(string token, string platform)
    {
        if (_state.Id == default)
            return;

        var now = DateTimeOffset.UtcNow;
        var clearing = string.IsNullOrEmpty(token);

        if (clearing)
        {
            logger.LogInformation(
                "Node {NodeId}: clearing push token (APNs 410 or explicit clear).",
                _state.Id.Value);
            _state.PushToken = null;
            _state.PushPlatform = null;
        }
        else
        {
            // Never log the full token — 8-char prefix only.
            logger.LogInformation(
                "Node {NodeId}: registering push token prefix={Prefix}... platform={Platform}.",
                _state.Id.Value,
                token.Length >= 8 ? token[..8] : token,
                platform);
            _state.PushToken = token;
            _state.PushPlatform = platform;
        }

        _state.PushTokenUpdatedAt = now;
        _state.UpdatedAt = now;
        await repository.UpsertNodeAsync(_state);
    }

    /// <summary>
    /// 031: compare-and-clear. Clears PushToken/PushPlatform ONLY if the stored token still equals
    /// <paramref name="deadToken"/> — see interface doc for the race this closes. No-op (does not
    /// persist) if the node was never persisted, the stored token differs (already rotated), or
    /// is already empty.
    /// </summary>
    public async Task ClearPushTokenIfMatchesAsync(string deadToken)
    {
        if (_state.Id == default)
            return;
        if (string.IsNullOrEmpty(_state.PushToken) || !string.Equals(_state.PushToken, deadToken, StringComparison.Ordinal))
            return; // already rotated to a different token, or already clear — do not clobber.

        logger.LogInformation(
            "Node {NodeId}: compare-and-clear push token (matched dead token prefix={Prefix}...).",
            _state.Id.Value,
            deadToken.Length >= 8 ? deadToken[..8] : deadToken);

        _state.PushToken = null;
        _state.PushPlatform = null;
        _state.PushTokenUpdatedAt = DateTimeOffset.UtcNow;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(_state);
    }

    /// <summary>
    /// Node-visibility-doctor design (2026-07-02): sets/clears the owner-editable Note.
    /// Trims whitespace; null/empty/whitespace-only clears it (stored as null). Length is
    /// validated by the caller (PATCH /api/nodes/{id}) — not re-checked here.
    /// </summary>
    public async Task<Node> SetNoteAsync(string? note)
    {
        // B3-review (low): mirror the sibling mutators' never-persisted guard (HeartbeatAsync,
        // RegisterPushTokenAsync return early). Throwing here — the method must return a Node,
        // and upserting would create a corrupt Nodes row with an empty-string PK.
        if (_state.Id == default)
            throw new InvalidOperationException(
                $"NodeGrain {this.GetPrimaryKeyString()}: SetNoteAsync called on a never-persisted node.");

        _state.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(_state);
        return _state;
    }

    /// <summary>
    /// #165 (`korat nodes prune`): hard-delete the node row. Mirrors
    /// McpServerGrain.RemoveAsync — resets state and deactivates so a later reactivation finds
    /// no row to reload. No-op if never persisted / already removed.
    /// </summary>
    public async Task RemoveAsync()
    {
        if (_state.Id == default)
            return; // never persisted / already removed — nothing to delete.

        await repository.DeleteNodeAsync(new NodeId(this.GetPrimaryKeyString()));
        _capabilities.Clear();
        _state = new Node
        {
            Id = default,
            SpaceId = default,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        DeactivateOnIdle();
    }

    public async Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        // FIX: same as ConnectAsync — preserve push-token fields so tests that call
        // RegisterPushTokenAsync before MarkOnlineForTestingAsync retain the token.
        _state = new Node
        {
            Id = new NodeId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            DisplayName = displayName,
            Status = NodeStatus.Online,
            CurrentGatewayId = default,
            LastSeenAt = now,
            CreatedAt = _state.CreatedAt == default ? now : _state.CreatedAt,
            UpdatedAt = now,
            PushToken = _state.PushToken,
            PushPlatform = _state.PushPlatform,
            PushTokenUpdatedAt = _state.PushTokenUpdatedAt,
            // B3-review (blocker): same as ConnectAsync — the owner-editable Note survives.
            Note = _state.Note,
        };
        await repository.UpsertNodeAsync(_state);
        return _state;
    }
}
