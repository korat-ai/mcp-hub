using System.Security.Cryptography;
using Korat.Cloud.Security.Envelope;
using Korat.Domain;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Security;

/// <summary>
/// Default <see cref="IEnvelopeCrypto"/> (interface lives in <c>Korat.Domain.Persistence</c> —
/// see <c>src/Korat.Domain/IEnvelopeCrypto.cs</c> — so <c>Korat.Grains</c> can consume it without
/// depending on the Cloud host app): thin orchestration over the existing #55 pieces —
/// <see cref="SpaceDekProvider"/> (DEK lifecycle, KEK unwrap, cache) and the pure
/// <see cref="EnvelopeCipher"/>. No behavior change to the crypto itself.
/// Stateless; safe as a singleton (all state lives in the injected SpaceDekProvider).
/// </summary>
public sealed class EnvelopeCrypto(
    SpaceDekProvider dekProvider,
    IOptions<EnvelopeOptions> options) : IEnvelopeCrypto
{
    public async Task<string> EncryptAsync(SpaceId spaceId, string aad, string plaintext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(aad);

        var opts = options.Value;
        if (!opts.IsEnvelopeEnabled)
        {
            // FAIL CLOSED: unlike the inference-secret service (which has a documented legacy
            // DataProtection mode for the rolling pre-KEK deploy phase), the generic primitive
            // has no weaker fallback — writing anything else would be dump-decryptable.
            throw new InvalidOperationException(
                "Envelope encryption is not configured (Korat:Envelope:ActiveKekId / Keks absent). " +
                "Set the KEK Fly secret before storing envelope-encrypted records.");
        }

        var dek = await dekProvider.GetOrCreateDekAsync(spaceId, ct)
            ?? throw new InvalidOperationException(
                $"Envelope is enabled (ActiveKekId='{opts.ActiveKekId}') but the DEK could not be " +
                $"obtained for space '{spaceId.Value}'. Check that Korat:Envelope:Keks['{opts.ActiveKekId}'] " +
                $"is a valid base64-encoded 32-byte key (fail-closed: no weaker format is ever written).");

        return EnvelopeCipher.EncryptSecret(
            dek.Dek, plaintext, spaceId.Value, aad, dek.KekId, dek.DekVersion);
    }

    public async Task<string> DecryptAsync(SpaceId spaceId, string aad, string ciphertext, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentException.ThrowIfNullOrEmpty(aad);

        var (_, dekVersion, _, _) = EnvelopeCipher.ParseEnvelope(ciphertext); // FormatException if invalid

        // Null = DEK row not found (crypto-shred, or ciphertext from another space). KEK-missing /
        // unwrap-auth failures throw InvalidOperationException inside the provider (fail-closed).
        var dek = await dekProvider.GetDekAsync(spaceId, dekVersion, ct)
            ?? throw new CryptographicException(
                $"Envelope: DEK row not found for space '{spaceId.Value}' v{dekVersion} — " +
                "record unrecoverable (crypto-shred or ciphertext copied from another space).");

        return EnvelopeCipher.DecryptSecret(dek.Dek, ciphertext, spaceId.Value, aad);
    }
}
