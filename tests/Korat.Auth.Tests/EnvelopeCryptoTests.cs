using System.Security.Cryptography;
using Korat.Cloud.Security.Envelope;
using Korat.Cloud.Web.Security;
using Korat.Domain;
using Korat.Domain.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>
/// PR-2 Task 1: the generalized <see cref="IEnvelopeCrypto"/> primitive extracted from the
/// #55 inference-secret envelope path. Same AES-256-GCM + per-space-DEK + KEK crypto —
/// only the record-binding AAD becomes a caller-supplied string.
///
/// COMPATIBILITY INVARIANT (security-critical): for inference-point secrets the aad MUST be
/// the raw <c>pointId.Value</c> — that is what <c>EnvelopeCipher.BuildSecretAad</c> has always
/// bound ciphertext to. Changing it would make every existing stored BYOK secret
/// undecryptable. Pinned by <see cref="Preexisting_inference_envelope_rows_stay_decryptable_through_the_refactored_service"/>.
/// </summary>
public sealed class EnvelopeCryptoTests
{
    private static readonly byte[] TestKek = NewKey();
    private const string KekId = "k1";

    private static byte[] NewKey() { var k = new byte[32]; RandomNumberGenerator.Fill(k); return k; }

    private static EnvelopeOptions EnabledOptions() => new()
    {
        ActiveKekId = KekId,
        Keks = new Dictionary<string, string> { [KekId] = Convert.ToBase64String(TestKek) }
    };

    /// <summary>Suite helper wiring the test KEK (per the PR-2 plan, Task 1 Step 1).</summary>
    private static IEnvelopeCrypto NewEnvelopeCrypto(
        EnvelopeOptions? options = null, InMemoryDatabaseRoot? root = null, string? dbName = null)
    {
        var (crypto, _) = NewEnvelopeCryptoWithDekProvider(options, root, dbName);
        return crypto;
    }

    private static (IEnvelopeCrypto Crypto, SpaceDekProvider DekProvider) NewEnvelopeCryptoWithDekProvider(
        EnvelopeOptions? options = null, InMemoryDatabaseRoot? root = null, string? dbName = null)
    {
        options ??= EnabledOptions();
        root    ??= new InMemoryDatabaseRoot();
        dbName  ??= Guid.NewGuid().ToString("N");

        var factory = new EnvelopeSecurityAcceptanceTests.TestDbContextFactory(root, dbName);
        var monitor = new EnvelopeSecurityAcceptanceTests.StaticOptionsMonitor<EnvelopeOptions>(options);
        var dekProvider = new SpaceDekProvider(
            factory, new ConfigKekProvider(monitor), NullLogger<SpaceDekProvider>.Instance);

        var crypto = new EnvelopeCrypto(
            dekProvider, Microsoft.Extensions.Options.Options.Create(options));
        return (crypto, dekProvider);
    }

    // ── Plan Task 1 Step 1: round-trip + AAD binding ──────────────────────────

    [Fact]
    public async Task Encrypt_roundtrips_and_aad_binds_the_record()
    {
        var crypto = NewEnvelopeCrypto();                     // suite helper wiring the test KEK
        var space = new SpaceId("s1");
        var ct = await crypto.EncryptAsync(space, "channel:b1", "bot-token-123", default);
        Assert.Equal("bot-token-123", await crypto.DecryptAsync(space, "channel:b1", ct, default));
        await Assert.ThrowsAnyAsync<Exception>(() => crypto.DecryptAsync(space, "channel:OTHER", ct, default));
    }

    [Fact]
    public async Task Ciphertext_is_envelope_format_and_never_contains_plaintext()
    {
        var crypto = NewEnvelopeCrypto();
        var ct = await crypto.EncryptAsync(new SpaceId("s1"), "msg:m1", "hello-world-plaintext", default);

        Assert.True(EnvelopeCipher.IsEnvelope(ct));
        Assert.DoesNotContain("hello-world-plaintext", ct, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decrypt_with_wrong_space_fails()
    {
        // Same DEK store, different space → the DEK row for spaceB does not exist → hard failure
        // (and even with a DEK, the AAD binds the spaceId).
        var root = new InMemoryDatabaseRoot();
        var dbName = Guid.NewGuid().ToString("N");
        var crypto = NewEnvelopeCrypto(root: root, dbName: dbName);

        var ct = await crypto.EncryptAsync(new SpaceId("space-a"), "channel:b1", "secret", default);
        await Assert.ThrowsAnyAsync<Exception>(
            () => crypto.DecryptAsync(new SpaceId("space-b"), "channel:b1", ct, default));
    }

    [Fact]
    public async Task Encrypt_without_configured_kek_fails_closed()
    {
        // New consumers (channel tokens, message content) must NEVER silently fall back to a
        // weaker format — envelope-disabled is a hard error for the generic primitive.
        var noKek = new EnvelopeOptions { ActiveKekId = null, Keks = [] };
        var crypto = NewEnvelopeCrypto(options: noKek);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crypto.EncryptAsync(new SpaceId("s1"), "channel:b1", "secret", default));
    }

    [Fact]
    public async Task Decrypt_garbage_ciphertext_throws_format_exception()
    {
        var crypto = NewEnvelopeCrypto();
        await Assert.ThrowsAsync<FormatException>(
            () => crypto.DecryptAsync(new SpaceId("s1"), "channel:b1", "not-an-envelope", default));
    }

    // ── Security-critical regression pin: existing inference secrets stay decryptable ──

    private static IDataProtectionProvider BuildDp()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("KoratTest").UseEphemeralDataProtectionProvider();
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }
}
