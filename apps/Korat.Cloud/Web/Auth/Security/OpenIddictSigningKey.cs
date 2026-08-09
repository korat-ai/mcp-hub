using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Korat.Cloud.Web.Auth.Security;

/// <summary>
/// Resolves the OpenIddict signing/encryption certificate from configuration.
/// Supports two sources with a defined precedence:
///   1. OpenIddict:SigningKeyPath — path to a PKCS#12 (.pfx) file on disk.
///   2. OpenIddict:SigningKeyBase64 — base64-encoded bytes of a PKCS#12 file
///      (intended for secret-manager / Fly secrets where a file path is impractical).
/// </summary>
public static class OpenIddictSigningKey
{
    /// <summary>
    /// Resolves the signing certificate from <paramref name="configuration"/>.
    /// Returns <c>null</c> when neither source is configured (dev/test fallback path).
    /// </summary>
    /// <remarks>
    /// Precedence:
    /// <list type="number">
    ///   <item><description>If <c>OpenIddict:SigningKeyPath</c> is set and the file exists, load from file.</description></item>
    ///   <item><description>Else if <c>OpenIddict:SigningKeyBase64</c> is non-empty, decode and load from bytes.</description></item>
    ///   <item><description>Otherwise return <c>null</c> — callers should use ephemeral dev certs or throw.</description></item>
    /// </list>
    /// </remarks>
    public static X509Certificate2? Resolve(IConfiguration configuration)
    {
        var keyPath = configuration["OpenIddict:SigningKeyPath"];
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            // LoadPkcs12FromFile reads both the certificate and its private key.
            // LoadCertificateFromFile only loads the public cert (DER) — signing would fail.
            return X509CertificateLoader.LoadPkcs12FromFile(keyPath, password: null);
        }

        var keyBase64 = configuration["OpenIddict:SigningKeyBase64"];
        if (!string.IsNullOrEmpty(keyBase64))
        {
            var bytes = Convert.FromBase64String(keyBase64);
            // Load directly from bytes — no temp file needed, no filesystem permissions concern.
            return X509CertificateLoader.LoadPkcs12(bytes, password: null);
        }

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when at least one key source is available in <paramref name="configuration"/>.
    /// Used by the SEC-HIGH-2 startup guard.
    /// </summary>
    public static bool IsAvailable(IConfiguration configuration)
    {
        var keyPath = configuration["OpenIddict:SigningKeyPath"];
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            return true;

        var keyBase64 = configuration["OpenIddict:SigningKeyBase64"];
        return !string.IsNullOrEmpty(keyBase64);
    }
}
