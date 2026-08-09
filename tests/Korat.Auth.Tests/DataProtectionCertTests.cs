using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Korat.Cloud.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Auth.Tests;

/// <summary>
/// 032 C7 unit tests: the optional DataProtection key-ring protection certificate.
/// Verifies (a) loader validation fail-fast paths, (b) cert-protected key ring round-trips
/// protect/unprotect, and (c) the on-disk key XML is actually encrypted (contains an
/// encryptedKey element, not a plaintext masterKey) — the property that makes a DB/dump
/// leak of the key ring useless without the cert's private key.
/// </summary>
public sealed class DataProtectionCertTests
{
    private static X509Certificate2 MakeSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=korat-dp-cert-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void Load_RoundTrips_Valid_Pfx()
    {
        using var cert = MakeSelfSigned();
        var pfxBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, "pw1"));

        using var loaded = DpCertLoader.Load(pfxBase64, "pw1");
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
    }

    [Fact]
    public void Load_Throws_On_Invalid_Base64() =>
        Assert.Throws<InvalidOperationException>(() => DpCertLoader.Load("not-base64!!!", null));

    [Fact]
    public void Load_Throws_On_Wrong_Password()
    {
        using var cert = MakeSelfSigned();
        var pfxBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, "right"));
        Assert.Throws<InvalidOperationException>(() => DpCertLoader.Load(pfxBase64, "wrong"));
    }

    [Fact]
    public void CertProtected_KeyRing_RoundTrips_And_Key_Xml_Is_Encrypted()
    {
        using var cert = MakeSelfSigned();
        var keyDir = Directory.CreateTempSubdirectory("korat-dp-cert-test-");
        try
        {
            string protectedPayload;

            // Provider 1: cert-protected ring, protect a payload.
            {
                var services = new ServiceCollection();
                services.AddDataProtection()
                    .PersistKeysToFileSystem(keyDir)
                    .ProtectKeysWithCertificate(cert)
                    .SetApplicationName("Korat.Cloud.Test");
                using var sp = services.BuildServiceProvider();
                var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("t");
                protectedPayload = protector.Protect("the-secret-cookie-material");
            }

            // The persisted key XML must be cert-encrypted: an <encryptedSecret> envelope
            // (CertificateXmlEncryptor output), never a plaintext <masterKey> blob.
            var keyXml = Directory.GetFiles(keyDir.FullName, "key-*.xml").Select(File.ReadAllText).Single();
            Assert.Contains("encryptedSecret", keyXml);
            Assert.DoesNotContain("masterKey", keyXml);

            // Provider 2 (fresh process simulation): same ring + same cert → can unprotect.
            {
                var services = new ServiceCollection();
                services.AddDataProtection()
                    .PersistKeysToFileSystem(keyDir)
                    .ProtectKeysWithCertificate(cert)
                    .SetApplicationName("Korat.Cloud.Test");
                using var sp = services.BuildServiceProvider();
                var protector = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("t");
                Assert.Equal("the-secret-cookie-material", protector.Unprotect(protectedPayload));
            }
        }
        finally
        {
            try { keyDir.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Plaintext_Keys_Remain_Readable_After_Cert_Is_Enabled()
    {
        // C7 rollout safety: enabling the cert must NOT break existing UNENCRYPTED ring keys
        // (live cookies, OAuth state, legacy DP-format BYOK rows).
        using var cert = MakeSelfSigned();
        var keyDir = Directory.CreateTempSubdirectory("korat-dp-plain-test-");
        try
        {
            string protectedPayload;

            // Phase 1: pre-C7 world — plaintext key ring.
            {
                var services = new ServiceCollection();
                services.AddDataProtection()
                    .PersistKeysToFileSystem(keyDir)
                    .SetApplicationName("Korat.Cloud.Test");
                using var sp = services.BuildServiceProvider();
                protectedPayload = sp.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("t").Protect("legacy-payload");
            }

            // Phase 2: cert enabled — old plaintext key still decrypts old payloads.
            {
                var services = new ServiceCollection();
                services.AddDataProtection()
                    .PersistKeysToFileSystem(keyDir)
                    .ProtectKeysWithCertificate(cert)
                    .SetApplicationName("Korat.Cloud.Test");
                using var sp = services.BuildServiceProvider();
                Assert.Equal("legacy-payload", sp.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("t").Unprotect(protectedPayload));
            }
        }
        finally
        {
            try { keyDir.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }
}
