using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;

namespace Korat.Grains;

/// <summary>Grain клиента агента (Cursor и т.д.) на узле.</summary>
public sealed class ConsumerGrain(IMetadataRepository repository) : Grain, IConsumerGrain
{
    private Consumer _state = new()
    {
        Id = default,
        SpaceId = default,
        NodeId = default,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Rehydrate from DB on every activation so NodeId (used for anti-spoofing in
        // NodeGatewayService) survives idle deactivation and silo failover.
        var persisted = await repository.GetAgentClientAsync(
            new ConsumerId(this.GetPrimaryKeyString()), cancellationToken);
        if (persisted is not null)
            _state = persisted;
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task<Consumer> RegisterAsync(SpaceId spaceId, NodeId nodeId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        _state = new Consumer
        {
            Id = new ConsumerId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            NodeId = nodeId,
            DisplayName = displayName,
            Status = ConsumerStatus.Online,
            LastSeenAt = now,
            // Preserve CreatedAt on re-registration (node reconnect) — only use `now` on first register.
            CreatedAt = _state.CreatedAt == default ? now : _state.CreatedAt,
            UpdatedAt = now
        };
        await repository.UpsertAgentClientAsync(_state);
        return _state;
    }

    public Task<Consumer> GetAsync() => Task.FromResult(_state);
}
