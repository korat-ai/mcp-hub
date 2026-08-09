using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Grains;

/// <summary>Grain облачного gateway: heartbeat и выдача session-home id.</summary>
public sealed class GatewayGrain : Grain, IGatewayGrain
{
    private Gateway _state = new()
    {
        Id = default,
        Status = GatewayStatus.Offline
    };

    public Task RegisterAsync()
    {
        _state = new Gateway
        {
            Id = new GatewayId(this.GetPrimaryKeyString()),
            Status = GatewayStatus.Online,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync()
    {
        _state.Status = GatewayStatus.Online;
        _state.LastHeartbeatAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task<GatewayId> AssignSessionHomeAsync() =>
        Task.FromResult(new GatewayId(this.GetPrimaryKeyString()));

    public Task<Gateway> GetAsync() => Task.FromResult(_state);
}
