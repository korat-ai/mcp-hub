using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Security.Envelope;

/// <summary>
/// 032 (#57 Leg 3 C5): the KEK custody seam.
///
/// Wrap/unwrap-SHAPED (not get-bytes-shaped) on purpose: with an external KMS the KEK is
/// non-exportable and the wrap/unwrap happens server-side — an interface that handed out raw
/// KEK bytes could never be backed by a KMS. The future <c>KmsKekProvider</c> (leg3 doc item 3)
/// becomes a pure DI swap; KEK rotation to KMS = introduce a new kekId backed by the new
/// provider and run the standard rewrap flow.
///
/// Contract:
/// - <see cref="UnwrapDekAsync"/> throws <see cref="InvalidOperationException"/> for an UNKNOWN
///   kekId (hard misconfiguration — the KEK must remain available until all rows are rewrapped)
///   and lets <see cref="CryptographicException"/> propagate on authentication failure
///   (tampered wrapped-DEK or wrong key bytes).
/// - Implementations must never log or expose key material.
/// </summary>
public interface IKekProvider
{
    /// <summary>True when a usable active KEK exists (envelope mode on).</summary>
    bool IsEnabled { get; }

    /// <summary>The kekId used for NEW wrap operations; null when envelope is not configured.</summary>
    string? ActiveKekId { get; }

    /// <summary>True when this provider can unwrap material wrapped under <paramref name="kekId"/>.</summary>
    bool KnowsKek(string kekId);

    /// <summary>Wraps a plaintext DEK under <paramref name="kekId"/>, bound to (spaceId, kekId, dekVersion) via AAD.</summary>
    Task<(byte[] Nonce, byte[] WrappedDek)> WrapDekAsync(
        string kekId, byte[] plainDek, string spaceId, int dekVersion, CancellationToken ct = default);

    /// <summary>Unwraps a wrapped DEK. See interface remarks for the failure contract.</summary>
    Task<byte[]> UnwrapDekAsync(
        string kekId, byte[] nonce, byte[] wrappedDek, string spaceId, int dekVersion, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IKekProvider"/>: KEK bytes from configuration / Fly secrets
/// (<see cref="EnvelopeOptions"/>), AES-256-GCM wrap via <see cref="EnvelopeCipher"/> —
/// byte-for-byte the pre-seam behaviour of SpaceDekProvider. KEK bytes are zeroed after
/// each operation and never cached.
/// </summary>
public sealed class ConfigKekProvider(IOptionsMonitor<EnvelopeOptions> options) : IKekProvider
{
    public bool IsEnabled => options.CurrentValue.IsEnvelopeEnabled;

    public string? ActiveKekId
    {
        get
        {
            var opts = options.CurrentValue;
            return opts.IsEnvelopeEnabled ? opts.ActiveKekId : null;
        }
    }

    public bool KnowsKek(string kekId)
    {
        var kek = options.CurrentValue.TryGetKek(kekId);
        if (kek is null)
            return false;
        Array.Clear(kek, 0, kek.Length);
        return true;
    }

    public Task<(byte[] Nonce, byte[] WrappedDek)> WrapDekAsync(
        string kekId, byte[] plainDek, string spaceId, int dekVersion, CancellationToken ct = default)
    {
        var kek = options.CurrentValue.TryGetKek(kekId)
            ?? throw new InvalidOperationException(
                $"Envelope: KEK '{kekId}' is not present in Korat:Envelope:Keks (or is invalid).");
        try
        {
            return Task.FromResult(EnvelopeCipher.WrapDek(kek, plainDek, spaceId, kekId, dekVersion));
        }
        finally
        {
            Array.Clear(kek, 0, kek.Length);
        }
    }

    public Task<byte[]> UnwrapDekAsync(
        string kekId, byte[] nonce, byte[] wrappedDek, string spaceId, int dekVersion, CancellationToken ct = default)
    {
        // MAJOR #55 invariant preserved: an EXISTING DEK row whose KEK is ABSENT from config must
        // THROW InvalidOperationException — never fall back to DataProtection format.
        var kek = options.CurrentValue.TryGetKek(kekId)
            ?? throw new InvalidOperationException(
                $"Envelope: KEK '{kekId}' for space '{spaceId}' v{dekVersion} is not present in " +
                $"Korat:Envelope:Keks. The KEK must remain in config until all rows using it are rewrapped. " +
                $"Run the rewrap (POST /api/admin/envelope/rewrap) before removing a KEK.");
        try
        {
            // CryptographicException (GCM auth failure) propagates to the caller per contract.
            return Task.FromResult(EnvelopeCipher.UnwrapDek(kek, nonce, wrappedDek, spaceId, kekId, dekVersion));
        }
        finally
        {
            Array.Clear(kek, 0, kek.Length);
        }
    }
}
