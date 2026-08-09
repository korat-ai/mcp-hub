namespace Korat.Domain.Persistence;

/// <summary>
/// PR-2 Task 1/3: the generalized #55 envelope-encryption primitive (AES-256-GCM under a
/// per-space DEK, itself wrapped by a KEK from Fly-secret config). Lives in <c>Korat.Domain</c>
/// (alongside <see cref="IMetadataRepository"/>) — NOT in <c>apps/Korat.Cloud</c> — so that
/// <c>Korat.Grains</c> (e.g. <c>ThreadGrain</c>, PR-2 Task 3) can consume it without an illegal
/// reverse project reference (Korat.Grains never depends on the Cloud host app). The concrete
/// implementation (<c>EnvelopeCrypto</c>, orchestrating <c>SpaceDekProvider</c> +
/// <c>EnvelopeCipher</c>) still lives in <c>apps/Korat.Cloud/Web/Security/EnvelopeCrypto.cs</c>
/// and is registered for both the web host and the Orleans silo DI containers.
///
/// The <c>aad</c> string binds the ciphertext to a logical record (e.g. the raw
/// <c>pointId.Value</c> for inference-point secrets — SECURITY-CRITICAL: this exact AAD keeps
/// every existing stored BYOK/BYO secret decryptable; see EnvelopeCryptoTests — or
/// <c>$"channel:{bindingId}"</c> / <c>$"msg:{messageId}"</c> for the newer Channels/Threads
/// surfaces) so ciphertext cannot be spliced between records (or spaces — spaceId is also part
/// of the AAD).
/// </summary>
public interface IEnvelopeCrypto
{
    /// <summary>
    /// AES-256-GCM under the space DEK (KEK from Fly secret). <paramref name="aad"/> binds the
    /// ciphertext to a logical record so ciphertext can't be swapped between records. Returns
    /// the opaque envelope string (<c>kenv1.…</c>).
    /// FAIL-CLOSED: throws <see cref="InvalidOperationException"/> when the envelope KEK is not
    /// configured — the generic primitive never falls back to a weaker format.
    /// </summary>
    Task<string> EncryptAsync(SpaceId spaceId, string aad, string plaintext, CancellationToken ct);

    /// <summary>
    /// Decrypts an envelope produced by <see cref="EncryptAsync"/> with the same (spaceId, aad).
    /// Throws <see cref="FormatException"/> for a structurally invalid envelope,
    /// <see cref="System.Security.Cryptography.CryptographicException"/> for a missing DEK row /
    /// AAD mismatch / tamper, and <see cref="InvalidOperationException"/> for KEK
    /// misconfiguration (fail-closed).
    /// </summary>
    Task<string> DecryptAsync(SpaceId spaceId, string aad, string ciphertext, CancellationToken ct);
}
