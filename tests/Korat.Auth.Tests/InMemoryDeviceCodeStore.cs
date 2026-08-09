using System.Security.Cryptography;
using Korat.Cloud.Web.Auth.Services;

namespace Korat.Auth.Tests;

/// <summary>
/// In-memory IDeviceCodeStore double for unit tests.
/// Validates interface semantics without Orleans. Orleans grain wiring is
/// covered by the integration tests in Task 5 (CliDeviceFlowEndpointsTests).
/// This matches the InMemory disclaimer convention used across auth service tests.
///
/// Accepts an optional <see cref="TimeProvider"/> so tests can advance the clock
/// to verify TTL expiry behaviour (cov C1).
/// </summary>
internal sealed class InMemoryDeviceCodeStore : IDeviceCodeStore
{
    private sealed record Entry(
        string DeviceCode,
        string UserCode,
        DeviceCodeStatus Status,
        Guid? UserId,
        DateTimeOffset Expiry);

    private readonly Dictionary<string, Entry> _byDevice = new();
    private readonly Dictionary<string, string> _userToDevice = new();
    private readonly TimeProvider _time;

    public InMemoryDeviceCodeStore(TimeProvider? time = null) =>
        _time = time ?? TimeProvider.System;

    private DateTimeOffset Now => _time.GetUtcNow();

    public Task<DeviceCodeEntry> CreateAsync(TimeSpan ttl, CancellationToken ct)
    {
        string deviceCode;
        string userCode;
        var expiry = Now.Add(ttl);

        // Mirrors GrainDeviceCodeStore: retry if user_code collides with a live entry.
        const int MaxAttempts = 5;
        var attempt = 0;
        do
        {
            if (++attempt > MaxAttempts)
                throw new InvalidOperationException("Failed to allocate a unique user_code after multiple attempts.");

            deviceCode = "dev-" + Guid.NewGuid().ToString("N");
            userCode = GenerateUserCode();
        }
        while (_userToDevice.TryGetValue(userCode, out var existing)
            && _byDevice.TryGetValue(existing, out var existingEntry)
            && existingEntry.Status == DeviceCodeStatus.Pending
            && Now <= existingEntry.Expiry);

        var entry = new Entry(deviceCode, userCode, DeviceCodeStatus.Pending, null, expiry);
        _byDevice[deviceCode] = entry;
        _userToDevice[userCode] = deviceCode;

        return Task.FromResult(new DeviceCodeEntry(deviceCode, userCode, DeviceCodeStatus.Pending, null));
    }

    public Task<bool> ApproveAsync(string userCode, Guid userId, CancellationToken ct)
    {
        if (!_userToDevice.TryGetValue(userCode, out var deviceCode)) return Task.FromResult(false);
        if (!_byDevice.TryGetValue(deviceCode, out var entry)) return Task.FromResult(false);
        if (entry.Status != DeviceCodeStatus.Pending || Now > entry.Expiry)
            return Task.FromResult(false);

        _byDevice[deviceCode] = entry with { Status = DeviceCodeStatus.Approved, UserId = userId };
        return Task.FromResult(true);
    }

    public Task<bool> DenyAsync(string userCode, CancellationToken ct)
    {
        if (!_userToDevice.TryGetValue(userCode, out var deviceCode)) return Task.FromResult(false);
        if (!_byDevice.TryGetValue(deviceCode, out var entry)) return Task.FromResult(false);
        if (entry.Status != DeviceCodeStatus.Pending || Now > entry.Expiry)
            return Task.FromResult(false);

        _byDevice[deviceCode] = entry with { Status = DeviceCodeStatus.Denied };
        // Mirror GrainDeviceCodeStore: clear the user_code mapping on deny so the code is single-use.
        _userToDevice.Remove(userCode);
        return Task.FromResult(true);
    }

    public Task<DeviceCodeEntry?> GetStatusAsync(string deviceCode, CancellationToken ct)
    {
        if (!_byDevice.TryGetValue(deviceCode, out var entry))
            return Task.FromResult<DeviceCodeEntry?>(null);

        // Mirror DeviceCodeGrain.ExpireIfNeeded: both Pending AND Approved entries are
        // considered Expired once the TTL deadline has passed (an approved-but-unconsumed
        // code is not consumable beyond expires_in).
        var effectiveStatus =
            Now > entry.Expiry && (entry.Status == DeviceCodeStatus.Pending || entry.Status == DeviceCodeStatus.Approved)
                ? DeviceCodeStatus.Expired
                : entry.Status;

        return Task.FromResult<DeviceCodeEntry?>(
            new DeviceCodeEntry(entry.DeviceCode, entry.UserCode, effectiveStatus, entry.UserId));
    }

    public Task MarkConsumedAsync(string deviceCode, CancellationToken ct)
    {
        if (_byDevice.TryGetValue(deviceCode, out var entry) && entry.Status == DeviceCodeStatus.Approved)
        {
            _byDevice[deviceCode] = entry with { Status = DeviceCodeStatus.Expired };
            // Mirror GrainDeviceCodeStore: clear the user_code mapping so the code is single-use
            // end-to-end (cannot be resolved again within the TTL window).
            _userToDevice.Remove(entry.UserCode);
        }
        return Task.CompletedTask;
    }

    private static string GenerateUserCode()
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}
