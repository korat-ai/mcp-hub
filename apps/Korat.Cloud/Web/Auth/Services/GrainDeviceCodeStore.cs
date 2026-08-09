using System.Security.Cryptography;
using Korat.GrainInterfaces;
using Orleans;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// Production IDeviceCodeStore backed by Orleans grains.
/// Each device-code handshake lives as a short-lived IDeviceCodeGrain (keyed by device_code).
/// A singleton IDeviceCodeRegistryGrain maps human-typed user_code → device_code.
/// </summary>
public sealed class GrainDeviceCodeStore(IClusterClient cluster) : IDeviceCodeStore
{
    // Crockford base32 alphabet: digits + uppercase letters minus I, L, O, U.
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string RegistryKey = "global";

    public async Task<DeviceCodeEntry> CreateAsync(TimeSpan ttl, CancellationToken ct)
    {
        var registry = cluster.GetGrain<IDeviceCodeRegistryGrain>(RegistryKey);

        // Retry on user_code collision: RFC 8628 §6.1 requires uniqueness of active codes.
        // Collision probability with 8 Crockford-base32 chars (32^8 ≈ 1T) is negligible in
        // practice, but we guard correctly: if the registry refuses to register (existing live
        // entry with the same user_code), regenerate and try again.
        string userCode;
        string deviceCode;
        bool registered;
        const int MaxAttempts = 5;
        var attempt = 0;
        do
        {
            if (++attempt > MaxAttempts)
                throw new InvalidOperationException("Failed to allocate a unique user_code after multiple attempts.");

            deviceCode = "dev-" + Guid.NewGuid().ToString("N");
            userCode = GenerateUserCode();

            var grain = cluster.GetGrain<IDeviceCodeGrain>(deviceCode);
            await grain.InitializeAsync(userCode, ttl);

            registered = await registry.RegisterAsync(userCode, deviceCode, ttl);
        }
        while (!registered);

        return new DeviceCodeEntry(deviceCode, userCode, DeviceCodeStatus.Pending, null);
    }

    public async Task<bool> ApproveAsync(string userCode, Guid userId, CancellationToken ct)
    {
        var deviceCode = await ResolveDeviceCode(userCode);
        if (deviceCode is null) return false;

        var grain = cluster.GetGrain<IDeviceCodeGrain>(deviceCode);
        var state = await grain.GetAsync();
        if (state.Status != DeviceCodeGrainStatus.Pending) return false;

        await grain.ApproveAsync(userId);
        return true;
    }

    public async Task<bool> DenyAsync(string userCode, CancellationToken ct)
    {
        var deviceCode = await ResolveDeviceCode(userCode);
        if (deviceCode is null) return false;

        var grain = cluster.GetGrain<IDeviceCodeGrain>(deviceCode);
        var state = await grain.GetAsync();
        if (state.Status != DeviceCodeGrainStatus.Pending) return false;

        await grain.DenyAsync();

        // Clear the registry mapping so the user_code cannot be resolved again — single-use.
        var registry = cluster.GetGrain<IDeviceCodeRegistryGrain>(RegistryKey);
        await registry.RemoveAsync(userCode);

        return true;
    }

    public async Task<DeviceCodeEntry?> GetStatusAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode)) return null;

        var grain = cluster.GetGrain<IDeviceCodeGrain>(deviceCode);
        var state = await grain.GetAsync();

        // If the grain was never initialized (unknown key) it will have an empty UserCode.
        if (string.IsNullOrEmpty(state.UserCode)) return null;

        return new DeviceCodeEntry(
            deviceCode,
            state.UserCode,
            MapStatus(state.Status),
            state.UserId);
    }

    public async Task MarkConsumedAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode)) return;
        var grain = cluster.GetGrain<IDeviceCodeGrain>(deviceCode);
        var state = await grain.GetAsync();
        await grain.ConsumeAsync();

        // Clear the user_code from the registry so it cannot be resolved again within its TTL
        // window — single-use enforcement end-to-end (prevents replay/brute-force after consume).
        if (!string.IsNullOrEmpty(state.UserCode))
        {
            var registry = cluster.GetGrain<IDeviceCodeRegistryGrain>(RegistryKey);
            await registry.RemoveAsync(state.UserCode);
        }
    }

    private async Task<string?> ResolveDeviceCode(string userCode)
    {
        var registry = cluster.GetGrain<IDeviceCodeRegistryGrain>(RegistryKey);
        return await registry.ResolveAsync(userCode);
    }

    private static DeviceCodeStatus MapStatus(DeviceCodeGrainStatus s) => s switch
    {
        DeviceCodeGrainStatus.Pending => DeviceCodeStatus.Pending,
        DeviceCodeGrainStatus.Approved => DeviceCodeStatus.Approved,
        DeviceCodeGrainStatus.Denied => DeviceCodeStatus.Denied,
        _ => DeviceCodeStatus.Expired,
    };

    /// <summary>
    /// Generates an 8-character Crockford base32 user code (e.g. "A3KM7RNP").
    /// Alphabet: 0-9 A-H J K M N P-T V-Z (no I, L, O, U).
    /// </summary>
    private static string GenerateUserCode()
    {
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = Crockford[RandomNumberGenerator.GetInt32(Crockford.Length)];
        return new string(chars);
    }
}
