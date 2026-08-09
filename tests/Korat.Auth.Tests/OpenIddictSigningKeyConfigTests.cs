using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Korat.Cloud.Web.Auth.Security;
using Microsoft.Extensions.Configuration;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="OpenIddictSigningKey"/> resolution logic.
/// These tests exercise the helper directly — no host spin-up required.
/// </summary>
public class OpenIddictSigningKeyConfigTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Generates a self-signed certificate with a private key and exports it as
    /// a PKCS#12 byte array (no password), suitable for base64-encoding.
    /// </summary>
    private static byte[] GeneratePfxBytes()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=korat-test-signing",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        return cert.Export(X509ContentType.Pfx);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    // ---------------------------------------------------------------------------
    // IsAvailable
    // ---------------------------------------------------------------------------

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenNeitherSourceConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        Assert.False(OpenIddictSigningKey.IsAvailable(config));
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenPathConfiguredButFileDoesNotExist()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningKeyPath"] = "/nonexistent/path/signing.pfx"
        });
        Assert.False(OpenIddictSigningKey.IsAvailable(config));
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenValidPathExists()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, GeneratePfxBytes());
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["OpenIddict:SigningKeyPath"] = tmp
            });
            Assert.True(OpenIddictSigningKey.IsAvailable(config));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenBase64IsSet()
    {
        var b64 = Convert.ToBase64String(GeneratePfxBytes());
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningKeyBase64"] = b64
        });
        Assert.True(OpenIddictSigningKey.IsAvailable(config));
    }

    // ---------------------------------------------------------------------------
    // Resolve — returns null when nothing configured
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_ReturnsNull_WhenNeitherSourceConfigured()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        Assert.Null(OpenIddictSigningKey.Resolve(config));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPathConfiguredButFileDoesNotExist()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningKeyPath"] = "/nonexistent/path/signing.pfx"
        });
        Assert.Null(OpenIddictSigningKey.Resolve(config));
    }

    // ---------------------------------------------------------------------------
    // Resolve — file path source
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_LoadsCertWithPrivateKey_WhenValidPathConfigured()
    {
        var pfx = GeneratePfxBytes();
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, pfx);
            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["OpenIddict:SigningKeyPath"] = tmp
            });

            var cert = OpenIddictSigningKey.Resolve(config);

            Assert.NotNull(cert);
            Assert.True(cert.HasPrivateKey, "Certificate loaded from path must carry the private key.");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---------------------------------------------------------------------------
    // Resolve — base64 source
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_LoadsCertWithPrivateKey_WhenValidBase64Configured()
    {
        var b64 = Convert.ToBase64String(GeneratePfxBytes());
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningKeyBase64"] = b64
        });

        var cert = OpenIddictSigningKey.Resolve(config);

        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey, "Certificate loaded from base64 must carry the private key.");
    }

    // ---------------------------------------------------------------------------
    // Resolve — precedence: path wins over base64
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_PrefersPathOverBase64_WhenBothConfigured()
    {
        var pathPfx = GeneratePfxBytes();
        var base64Pfx = GeneratePfxBytes();

        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, pathPfx);

            var config = BuildConfig(new Dictionary<string, string?>
            {
                ["OpenIddict:SigningKeyPath"] = tmp,
                ["OpenIddict:SigningKeyBase64"] = Convert.ToBase64String(base64Pfx)
            });

            var certFromPath = X509CertificateLoader.LoadPkcs12(pathPfx, password: null);
            var resolved = OpenIddictSigningKey.Resolve(config);

            Assert.NotNull(resolved);
            // Thumbprints uniquely identify the cert: resolved should match the path cert, not the base64 one.
            Assert.Equal(certFromPath.Thumbprint, resolved.Thumbprint);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---------------------------------------------------------------------------
    // Resolve — base64 used when path file does not exist
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_FallsBackToBase64_WhenPathFileDoesNotExist()
    {
        var b64 = Convert.ToBase64String(GeneratePfxBytes());
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OpenIddict:SigningKeyPath"] = "/nonexistent/path/signing.pfx",
            ["OpenIddict:SigningKeyBase64"] = b64
        });

        var cert = OpenIddictSigningKey.Resolve(config);

        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey);
    }
}
