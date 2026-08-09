namespace Korat.GrainInterfaces;

public enum DeviceCodeGrainStatus { Pending, Approved, Denied, Expired }

[GenerateSerializer]
public sealed record DeviceCodeState(
    [property: Id(0)] DeviceCodeGrainStatus Status,
    [property: Id(1)] string UserCode,
    [property: Id(2)] Guid? UserId);

/// <summary>
/// Short-lived grain (keyed by device_code) that holds one device-flow handshake.
/// The grain deactivates naturally when Orleans evicts it after TTL has elapsed.
/// Status flow: Pending → Approved(userId) | Denied. ConsumeAsync burns Approved → Expired (single-use).
/// </summary>
public interface IDeviceCodeGrain : IGrainWithStringKey
{
    /// <summary>
    /// Stores the user_code and records the TTL deadline; must be called once after creation.
    /// Returns the device_code (grain key) for convenience.
    /// </summary>
    Task<string> InitializeAsync(string userCode, TimeSpan ttl);

    /// <summary>Current state of this handshake.</summary>
    Task<DeviceCodeState> GetAsync();

    /// <summary>Marks the handshake approved for the given user.</summary>
    Task ApproveAsync(Guid userId);

    /// <summary>Marks the handshake denied.</summary>
    Task DenyAsync();

    /// <summary>
    /// Reads and burns an Approved handshake (sets status to Expired so it cannot be consumed twice).
    /// Pending / Denied / Expired entries are returned as-is without state mutation.
    /// </summary>
    Task<DeviceCodeState> ConsumeAsync();
}

/// <summary>
/// Singleton registry grain (key "global") that maps human-typed user_code → device_code.
/// Allows the SPA approval flow to locate the correct IDeviceCodeGrain without knowing device_code.
/// </summary>
public interface IDeviceCodeRegistryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Registers a user_code → device_code mapping with a given TTL.
    /// Returns false (and does NOT overwrite) if a non-expired entry already exists for the same
    /// user_code — callers must generate a fresh user_code and retry on false.
    /// </summary>
    Task<bool> RegisterAsync(string userCode, string deviceCode, TimeSpan ttl);

    /// <summary>Returns the device_code for a user_code, or null if not found / expired.</summary>
    Task<string?> ResolveAsync(string userCode);

    /// <summary>
    /// Removes the user_code mapping unconditionally (single-use enforcement: clears the entry
    /// after the handshake is consumed so the code cannot be resolved again within its TTL window).
    /// </summary>
    Task RemoveAsync(string userCode);
}
