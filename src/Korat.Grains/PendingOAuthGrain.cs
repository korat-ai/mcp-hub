using Korat.GrainInterfaces;

namespace Korat.Grains;

/// <summary>Increment 2 (HTTP MCP OAuth): see IPendingOAuthGrain. Mirrors DeviceCodeGrain's
/// pending/non-durable/burn-on-consume shape exactly.</summary>
public sealed class PendingOAuthGrain(TimeProvider time) : Grain, IPendingOAuthGrain
{
    private PendingOAuthState? _state;
    private DateTimeOffset _deadline = DateTimeOffset.MaxValue;
    private bool _consumed;

    public Task InitializeAsync(PendingOAuthState state, TimeSpan ttl)
    {
        _state = state;
        _deadline = time.GetUtcNow().Add(ttl);
        _consumed = false;
        // Keep this activation alive through the whole consent window even if Orleans would
        // otherwise idle-GC it before the owner finishes the browser round trip.
        DelayDeactivation(ttl);
        return Task.CompletedTask;
    }

    public Task<PendingOAuthState?> PeekAsync()
    {
        if (_consumed || _state is null || time.GetUtcNow() > _deadline)
            return Task.FromResult<PendingOAuthState?>(null);
        return Task.FromResult(_state); // non-consuming — deliberately does NOT set _consumed
    }

    public Task<PendingOAuthState?> ConsumeAsync()
    {
        if (_consumed || _state is null || time.GetUtcNow() > _deadline)
            return Task.FromResult<PendingOAuthState?>(null);
        _consumed = true; // single-use — a replayed callback with the same state gets null.
        return Task.FromResult(_state);
    }
}

/// <summary>Increment 2: see IPendingOAuthPointerGrain.</summary>
public sealed class PendingOAuthPointerGrain(TimeProvider time) : Grain, IPendingOAuthPointerGrain
{
    private string? _currentState;
    private DateTimeOffset _deadline = DateTimeOffset.MaxValue;

    public Task SetCurrentStateAsync(string state, TimeSpan ttl)
    {
        _currentState = state;
        _deadline = time.GetUtcNow().Add(ttl);
        DelayDeactivation(ttl);
        return Task.CompletedTask;
    }

    public Task<string?> GetCurrentStateAsync()
    {
        if (_currentState is null || time.GetUtcNow() > _deadline)
            return Task.FromResult<string?>(null);
        return Task.FromResult(_currentState);
    }
}
