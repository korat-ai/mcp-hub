using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Security.Envelope;

/// <summary>
/// Configuration for the per-space envelope encryption system.
/// Bound from <c>Korat:Envelope</c> in app configuration / Fly secrets.
///
/// In production: supply via Fly secrets:
///   KORAT__ENVELOPE__ACTIVEKEKID=k1
///   KORAT__ENVELOPE__KEKS__k1=&lt;base64-32-bytes&gt;
///
/// In tests: supply via IConfiguration / appsettings overrides.
///
/// STARTUP VALIDATION: call <see cref="Validate"/> (or use ValidateOnStart via AddOptions) to
/// fail fast at startup when KEK material is invalid. This prevents silent fall-back to
/// DataProtection ciphertext when the KEK is misconfigured.
/// </summary>
public sealed class EnvelopeOptions
{
    public const string SectionKey = "Korat:Envelope";

    /// <summary>
    /// Regex for a safe kek_id charset. The envelope format is dot-separated
    /// (kenv1.{kekId}.{dekVersion}.{nonce}.{ctTag}), so a kekId containing '.' would
    /// corrupt ParseEnvelope's Split('.') count and cause FormatException on every read
    /// after write (data loss). Reject at bind time to fail fast.
    /// Allowed: A-Z a-z 0-9 _ -
    /// </summary>
    private static readonly Regex SafeKekIdRegex =
        new(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The KEK id that is used for all NEW encrypt operations.
    /// Must match a key in <see cref="Keks"/>.
    /// Null or empty → KEK is not configured; service runs in legacy DataProtection mode.
    /// </summary>
    public string? ActiveKekId { get; init; }

    /// <summary>
    /// Map of kek_id → base64-encoded 32-byte key material.
    /// Multiple entries allow gradual KEK rotation (add new k2, rewrap, then remove k1).
    /// </summary>
    public Dictionary<string, string> Keks { get; init; } = [];

    /// <summary>True when at least the active KEK is configured and valid.</summary>
    public bool IsEnvelopeEnabled =>
        !string.IsNullOrWhiteSpace(ActiveKekId) &&
        Keks.ContainsKey(ActiveKekId!);

    /// <summary>
    /// Returns the raw 32-byte key material for a given kek_id, or null if not found / invalid.
    /// The caller must ZeroMemory the array after use if sensitive.
    /// </summary>
    public byte[]? TryGetKek(string kekId)
    {
        if (!Keks.TryGetValue(kekId, out var b64))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(b64);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates all KEK entries and the ActiveKekId. Throws <see cref="InvalidOperationException"/>
    /// on the first violation so startup fails fast rather than silently degrading at first write.
    ///
    /// Validations:
    ///   1. Every kek_id must match <c>^[A-Za-z0-9_-]+$</c> (no dots — they break envelope parsing).
    ///   2. Every kek_id value must be valid base64 decoding to exactly 32 bytes.
    ///   3. If <see cref="ActiveKekId"/> is set, it must exist as a key in <see cref="Keks"/>.
    ///
    /// When envelope is NOT enabled (ActiveKekId null/empty and Keks empty), this is a no-op
    /// (rolling-deploy safe: pre-KEK-secret phase).
    /// </summary>
    public void Validate()
    {
        foreach (var (kekId, b64) in Keks)
        {
            // MINOR fix: reject kekId chars that corrupt dot-split envelope parsing.
            if (!SafeKekIdRegex.IsMatch(kekId))
                throw new InvalidOperationException(
                    $"Korat:Envelope:Keks key '{kekId}' contains invalid characters. " +
                    $"Only A-Z, a-z, 0-9, '_', '-' are allowed (no dots).");

            // MAJOR companion fix: fail fast on bad base64 or wrong key length.
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(b64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Korat:Envelope:Keks['{kekId}'] is not valid base64.", ex);
            }

            if (bytes.Length != 32)
                throw new InvalidOperationException(
                    $"Korat:Envelope:Keks['{kekId}'] decodes to {bytes.Length} bytes; expected exactly 32.");
        }

        // MAJOR companion fix: ActiveKekId must be present in Keks when set.
        if (!string.IsNullOrWhiteSpace(ActiveKekId) && !Keks.ContainsKey(ActiveKekId!))
            throw new InvalidOperationException(
                $"Korat:Envelope:ActiveKekId '{ActiveKekId}' is not present in Korat:Envelope:Keks. " +
                $"Add the corresponding KEK entry or clear ActiveKekId.");
    }
}

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> adapter for <see cref="EnvelopeOptions"/> so that
/// <c>ValidateOnStart()</c> in Program.cs triggers <see cref="EnvelopeOptions.Validate"/> before
/// the host begins serving traffic. Registered as a singleton in DI.
/// </summary>
public sealed class EnvelopeOptionsValidator : IValidateOptions<EnvelopeOptions>
{
    public ValidateOptionsResult Validate(string? name, EnvelopeOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
