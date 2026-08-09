using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Grains;

/// <summary>Grain одного MCP-сервера: команда запуска и флаг Disabled.</summary>
public sealed class McpServerGrain(IMetadataRepository repository) : Grain, IMcpServerGrain
{
    private McpServer _state = new()
    {
        Id = default,
        SpaceId = default,
        PublisherNodeId = default,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var persisted = await repository.GetMcpServerAsync(new McpServerId(this.GetPrimaryKeyString()), cancellationToken);
        if (persisted is not null)
            _state = persisted;
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task<McpServer> PublishAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, string command, string args)
    {
        var now = DateTimeOffset.UtcNow;
        _state = new McpServer
        {
            Id = new McpServerId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            PublisherNodeId = publisherNodeId,
            DisplayName = displayName,
            LaunchCommand = command,
            LaunchArguments = args,
            Status = McpServerStatus.Published,
            // 021: a (re)publish always asserts the server — the node is explicitly declaring it.
            IsAsserted = true,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.UpsertMcpServerAsync(_state);
        return _state;
    }

    public async Task<McpServer> UpdateCommandAsync(string command, string args)
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        // Р27: remember what this server WAS, so the owner approving the resulting access request
        // can see a diff instead of a bare "something changed". Only recorded on an actual change —
        // a daemon re-declaring the identical definition on every reconnect must not overwrite the
        // history with "was X, now X" and bury the real change.
        if (!string.Equals(_state.LaunchCommand, command, StringComparison.Ordinal)
            || !string.Equals(_state.LaunchArguments, args, StringComparison.Ordinal))
        {
            _state.PreviousLaunchCommand = _state.LaunchCommand;
            _state.PreviousLaunchArguments = _state.LaunchArguments;
            _state.DefinitionChangedAt = DateTimeOffset.UtcNow;
        }

        _state.LaunchCommand = command;
        _state.LaunchArguments = args;
        _state.Status = McpServerStatus.Published;
        // 021: an update (reconnect re-publish of the same server) re-asserts it.
        _state.IsAsserted = true;
        _state.LastSeenAt = DateTimeOffset.UtcNow;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertMcpServerAsync(_state);
        return _state;
    }

    /// <summary>
    /// 021 (Layer 1): flip the IsAsserted bit without touching Status or other fields.
    /// Called by SpaceGrain.SyncMcpServersAsync during the soft-retire pass (asserted=false)
    /// after the upsert pass so a server present in the set is never transiently retired.
    /// </summary>
    public async Task<McpServer> SetAssertedAsync(bool asserted)
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        _state.IsAsserted = asserted;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertMcpServerAsync(_state);
        return _state;
    }

    // UNWIRED/DEFERRED: the `userId` parameter is not used — DisableAsync does not record who
    // disabled the server (no audit column on McpServerRecord). Adding an audit column requires
    // a migration that is outside this agent's scope. The parameter is kept in the interface
    // signature (touching it would ripple into apps/Korat.Cloud callers, which another agent owns).
    // TODO: add DisabledByUserId column to McpServerRecord + migration to wire up the audit trail.
    //
    // Returns true if this call actually transitioned the server to Disabled, false if it was
    // already Disabled (idempotent no-op — no repository write). Callers (the /disable endpoint)
    // use this to skip the audit-log write + UpdatedAt bump on a repeat disable.
    public async Task<bool> DisableAsync(UserId userId)
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        var changed = StateTransitions.DisableMcpServer(_state, DateTimeOffset.UtcNow);
        if (changed)
        {
            await repository.UpsertMcpServerAsync(_state);
            // Increment 1 (spec §6): disabling an http_cloud server closes its upstream MCP
            // session so a re-enable re-initializes cleanly instead of reusing stale state.
            if (Korat.Domain.McpServerTransports.IsHttpCloud(_state.Transport))
                await GrainFactory.GetGrain<IHttpMcpProxyGrain>(_state.Id.Value).EvictAsync();
        }
        return changed;
    }

    // Symmetric re-enable (mirrors IInferencePointGrain.EnableAsync). Same UNWIRED/DEFERRED note
    // as DisableAsync above applies to the unused `userId` parameter (no audit column yet).
    //
    // Returns true if this call actually transitioned the server to Published, false if it was
    // already Published (idempotent no-op — no repository write), same convention as
    // DisableAsync above.
    public async Task<bool> EnableAsync(UserId userId)
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        var hasUsableOAuthToken = true;
        if (Korat.Domain.McpServerAuthModes.IsOAuth(_state.AuthMode))
            hasUsableOAuthToken = await repository.GetMcpServerOAuthTokenCiphertextAsync(_state.Id) is not null;

        var changed = StateTransitions.EnableMcpServer(_state, DateTimeOffset.UtcNow, hasUsableOAuthToken);
        if (changed)
            await repository.UpsertMcpServerAsync(_state);
        return changed;
    }

    public async Task<McpServer> PublishHttpCloudAsync(
        SpaceId spaceId, string displayName, string remoteUrl, string authMode, string? authHeaderName, string? secretHint)
    {
        var now = DateTimeOffset.UtcNow;
        _state = new McpServer
        {
            Id = new McpServerId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            PublisherNodeId = new NodeId(string.Empty), // http_cloud: no relay node
            DisplayName = displayName,
            Transport = McpServerTransports.HttpCloud,
            RemoteUrl = remoteUrl,
            AuthMode = authMode,
            AuthHeaderName = authHeaderName,
            SecretHint = secretHint,
            Status = McpServerAuthModes.IsOAuth(authMode) ? McpServerStatus.NeedsReauth : McpServerStatus.Published,
            IsAsserted = true, // http_cloud has no SyncMcpServers soft-retire path — always asserted
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.UpsertMcpServerAsync(_state);
        return _state;
    }

    public async Task<McpServer> UpdateHttpCloudConfigAsync(
        string? remoteUrl, string? authMode, string? authHeaderName, string? secretHint,
        bool clearAuthHeaderName = false, bool clearSecretHint = false)
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        if (remoteUrl is not null)
            _state.RemoteUrl = remoteUrl;
        if (authMode is not null)
            _state.AuthMode = authMode;
        if (clearAuthHeaderName)
            _state.AuthHeaderName = null;
        else if (authHeaderName is not null)
            _state.AuthHeaderName = authHeaderName;
        // Finding 16, M4: clearSecretHint is checked BEFORE the "secretHint is not null" branch
        // so a clear always wins over a stale non-null hint — the two flags are never both
        // meaningful in the same call (the caller passes secretHint only when setting a NEW
        // secret, clearSecretHint only when clearing), but this ordering makes clear the
        // deliberate precedent, exactly mirroring the clearAuthHeaderName branch above.
        if (clearSecretHint)
            _state.SecretHint = null;
        else if (secretHint is not null)
            _state.SecretHint = secretHint;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpsertMcpServerAsync(_state);
        return _state;
    }

    public async Task<McpServer> MarkOAuthConnectedAsync()
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);
        // T1 opus-nit, closed in Task 4: a server the owner has since Disabled must stay
        // Disabled — a completed OAuth callback (possibly delayed/replayed) must never fail-open
        // a disabled server back to Published just because a token arrived. Admission (Status)
        // and dispatch-time token presence are independent gates; this is the admission gate's
        // own guard, not a substitute for HttpMcpProxyGrain's oauth-missing-token check (Task 5).
        if (_state.Status == McpServerStatus.Disabled)
            return _state;
        if (_state.Status != McpServerStatus.Published)
        {
            _state.Status = McpServerStatus.Published;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            await repository.UpsertMcpServerAsync(_state);
            await GrainFactory.GetGrain<IHttpMcpProxyGrain>(_state.Id.Value).EvictAsync();
        }
        return _state;
    }

    public async Task<McpServer> MarkNeedsReauthAsync()
    {
        if (_state.Id == default)
            throw new KoratDomainException(KoratErrorCode.NotFound);
        if (_state.Status != McpServerStatus.NeedsReauth)
        {
            _state.Status = McpServerStatus.NeedsReauth;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            await repository.UpsertMcpServerAsync(_state);
            await GrainFactory.GetGrain<IHttpMcpProxyGrain>(_state.Id.Value).EvictAsync();
        }
        return _state;
    }

    // `korat mcp remove` / unpublish: hard-delete the row (distinct from DisableAsync, which
    // keeps the server in the catalog as Disabled). After this the server is gone for good —
    // a grain rehydrate won't find a row to reload. Reset state + deactivate this grain.
    public async Task RemoveAsync()
    {
        if (_state.Id == default)
            return; // never published / already removed — nothing to delete.

        var wasHttpCloud = Korat.Domain.McpServerTransports.IsHttpCloud(_state.Transport);
        var serverId = _state.Id;
        await repository.DeleteMcpServerAsync(new McpServerId(this.GetPrimaryKeyString()));
        _state = new McpServer
        {
            Id = default,
            SpaceId = default,
            PublisherNodeId = default,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (wasHttpCloud)
            await GrainFactory.GetGrain<IHttpMcpProxyGrain>(serverId.Value).EvictAsync();
        DeactivateOnIdle();
    }

    public Task<McpServer> GetAsync() => Task.FromResult(_state);
}
