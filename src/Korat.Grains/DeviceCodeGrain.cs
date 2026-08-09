using Korat.GrainInterfaces;

namespace Korat.Grains;

/// <summary>
/// Grain for a single device-code handshake (keyed by device_code).
/// Holds ephemeral state — no DB persistence.
///
/// Lifecycle: grain state expires lazily on the next access once the TTL deadline passes;
/// grain memory is reclaimed by Orleans idle deactivation, not by an explicit TTL timer.
/// This applies to ALL states including Approved — an Approved-but-unconsumed handshake
/// also expires past the deadline (not left consumable indefinitely).
///
/// Status flow: Pending → Approved(userId) | Denied.
/// ConsumeAsync burns Approved → Expired (single-use guarantee).
/// </summary>
public sealed class DeviceCodeGrain(TimeProvider time) : Grain, IDeviceCodeGrain
{
    private DeviceCodeState _state = new(DeviceCodeGrainStatus.Pending, string.Empty, null);
    private DateTimeOffset _deadline = DateTimeOffset.MaxValue;

    public Task<string> InitializeAsync(string userCode, TimeSpan ttl)
    {
        _state = new DeviceCodeState(DeviceCodeGrainStatus.Pending, userCode, null);
        _deadline = time.GetUtcNow().Add(ttl);
        return Task.FromResult(this.GetPrimaryKeyString());
    }

    public Task<DeviceCodeState> GetAsync()
    {
        ExpireIfNeeded();
        return Task.FromResult(_state);
    }

    public Task ApproveAsync(Guid userId)
    {
        ExpireIfNeeded();
        if (_state.Status == DeviceCodeGrainStatus.Pending)
            _state = _state with { Status = DeviceCodeGrainStatus.Approved, UserId = userId };
        return Task.CompletedTask;
    }

    public Task DenyAsync()
    {
        ExpireIfNeeded();
        if (_state.Status == DeviceCodeGrainStatus.Pending)
            _state = _state with { Status = DeviceCodeGrainStatus.Denied };
        return Task.CompletedTask;
    }

    public Task<DeviceCodeState> ConsumeAsync()
    {
        ExpireIfNeeded();
        var snapshot = _state;
        if (_state.Status == DeviceCodeGrainStatus.Approved)
            _state = _state with { Status = DeviceCodeGrainStatus.Expired };
        return Task.FromResult(snapshot);
    }

    private void ExpireIfNeeded()
    {
        // Expire ALL non-terminal states (Pending AND Approved) once the TTL deadline passes.
        // An Approved-but-unconsumed handshake must not remain consumable beyond expires_in —
        // this enforces the TTL contract advertised to the CLI client.
        var status = _state.Status;
        if ((status == DeviceCodeGrainStatus.Pending || status == DeviceCodeGrainStatus.Approved)
            && time.GetUtcNow() > _deadline)
        {
            _state = _state with { Status = DeviceCodeGrainStatus.Expired };
        }
    }
}

/// <summary>
/// Singleton registry grain (grain key "global") that maps user_code → device_code.
/// Stores entries in memory with explicit expiry.
///
/// Expiry and cleanup notes:
/// - Stale entries are removed eagerly on <see cref="RegisterAsync"/> (sweeps expired keys)
///   and lazily on <see cref="ResolveAsync"/> for the queried key.
/// - <see cref="RemoveAsync"/> is called after a handshake is consumed so the user_code is
///   single-use end-to-end (does not stay resolvable for the remainder of its TTL window).
/// - <see cref="RegisterAsync"/> refuses to overwrite a live (non-expired) entry with the same
///   user_code; callers must retry with a fresh code on a false return.
///
/// Scale note: this is a singleton grain — all concurrent logins cluster-wide are serialized
/// through it. This is acceptable for closed-alpha volume. A future improvement would shard
/// the registry by user_code prefix to distribute load.
/// </summary>
public sealed class DeviceCodeRegistryGrain(TimeProvider time) : Grain, IDeviceCodeRegistryGrain
{
    // user_code → (device_code, expiry)
    private readonly Dictionary<string, (string DeviceCode, DateTimeOffset Expiry)> _map = new();

    public Task<bool> RegisterAsync(string userCode, string deviceCode, TimeSpan ttl)
    {
        var now = time.GetUtcNow();

        // Sweep expired entries on every register to bound memory growth from abandoned logins.
        var expired = _map
            .Where(kv => kv.Value.Expiry <= now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
            _map.Remove(key);

        // Refuse to overwrite a still-live entry for the same user_code.
        // The caller must generate a fresh user_code and retry on false — this prevents
        // a collision from redirecting a victim's approval to an attacker's device_code.
        if (_map.TryGetValue(userCode, out var existing) && existing.Expiry > now)
            return Task.FromResult(false);

        _map[userCode] = (deviceCode, now.Add(ttl));
        return Task.FromResult(true);
    }

    public Task<string?> ResolveAsync(string userCode)
    {
        if (_map.TryGetValue(userCode, out var entry))
        {
            if (time.GetUtcNow() <= entry.Expiry)
                return Task.FromResult<string?>(entry.DeviceCode);
            _map.Remove(userCode);
        }
        return Task.FromResult<string?>(null);
    }

    public Task RemoveAsync(string userCode)
    {
        _map.Remove(userCode);
        return Task.CompletedTask;
    }
}
