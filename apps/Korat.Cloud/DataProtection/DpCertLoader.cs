using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Korat.Cloud.DataProtection;

/// <summary>
/// 032 C7 (#57 Leg 3 item 4): loads the optional DataProtection key-ring protection
/// certificate from configuration / Fly secrets:
///
///   KORAT__DATAPROTECTION__CERTPFXBASE64  — base64-encoded PKCS#12 bundle (cert + private key)
///   KORAT__DATAPROTECTION__CERTPASSWORD   — optional PFX password
///
/// When configured, new DP key-ring entries are written cert-ENCRYPTED — a Postgres dump
/// alone can no longer forge session cookies / antiforgery tokens once the ring rotates
/// (~90 days), the same "DB dump alone is useless" property #55 gave inference secrets.
///
/// ROLLING-DEPLOY RULE (same shape as the #55 KEK): set the Fly secret only AFTER every
/// machine runs this code — an old machine cannot decrypt a NEW cert-protected ring key.
/// Existing UNENCRYPTED ring keys remain readable forever (DataProtection reads plaintext
/// key descriptors regardless of the configured protector), so legacy DP-format inference
/// secrets, OAuth state, and live cookies are unaffected by enabling this.
///
/// FAIL-FAST: an unloadable PFX throws at startup — silently running without key-ring
/// protection when the operator believes it is on would be worse than a crash loop.
/// </summary>
internal static class DpCertLoader
{
    internal static X509Certificate2? TryLoadFromConfig(IConfiguration configuration)
    {
        var pfxBase64 = configuration["Korat:DataProtection:CertPfxBase64"];
        if (string.IsNullOrWhiteSpace(pfxBase64))
            return null;

        var password = configuration["Korat:DataProtection:CertPassword"];
        return Load(pfxBase64, password);
    }

    internal static X509Certificate2 Load(string pfxBase64, string? password)
    {
        byte[] pfxBytes;
        try
        {
            pfxBytes = Convert.FromBase64String(pfxBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Korat:DataProtection:CertPfxBase64 is not valid base64.", ex);
        }

        // Ephemeral: never touch an OS cert store; the key lives in process memory only.
        // macOS (local dev / CI agents) does not support EphemeralKeySet — fall back to the
        // default key set there. Production runs on Linux (Fly container) where Ephemeral works.
        var storageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password, storageFlags);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Korat:DataProtection:CertPfxBase64 could not be loaded as a PKCS#12 bundle " +
                "(wrong password or corrupt data).", ex);
        }

        if (!cert.HasPrivateKey)
            throw new InvalidOperationException(
                "Korat:DataProtection certificate has no private key — the key ring could be " +
                "encrypted but never decrypted. Supply a full PKCS#12 bundle.");

        return cert;
    }
}
