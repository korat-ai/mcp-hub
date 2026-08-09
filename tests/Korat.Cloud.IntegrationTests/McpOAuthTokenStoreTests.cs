using System.Collections.Generic;
using Korat.Cloud.Mcp.Oauth;
using Korat.Cloud.Security.Envelope;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 2, Task 1: proves the OAuth token document survives a JSON + envelope round trip
/// through the real repository methods, using the SAME KEK-aware WithWebHostBuilder pattern
/// HttpMcpProxyGrainTests.CreateServerAsync already established for the static-secret ciphertext
/// (the shared fixture.Factory has no envelope KEK configured — fail-closed by design).
/// </summary>
public sealed class McpOAuthTokenStoreTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task SetGetClear_OAuthToken_RoundTripsThroughEnvelopeAndJson()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-store-{Guid.NewGuid():N}@example.com", "OAuth Store Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-oauth-store-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);

        var doc = new McpOAuthTokenDocument(
            AccessToken: "at-12345", RefreshToken: "rt-67890",
            AccessExpiry: DateTimeOffset.UtcNow.AddHours(1),
            TokenEndpoint: "https://as.example.test/token", Issuer: "https://as.example.test",
            ClientId: "client-abc", ClientSecret: "client-secret-xyz");

        var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));
        using var scope = kekFactory.Services.CreateScope();
        var envelopeCrypto = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
        var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();

        var json = McpOAuthTokenDocument.Serialize(doc);
        var ciphertext = await envelopeCrypto.EncryptAsync(server.SpaceId, McpServerSecretCrypto.OAuthAad(server.Id), json, default);
        await repository.SetMcpServerOAuthTokenAsync(server.Id, ciphertext, default);

        var storedCiphertext = await repository.GetMcpServerOAuthTokenCiphertextAsync(server.Id, default);
        Assert.NotNull(storedCiphertext);
        Assert.DoesNotContain("at-12345", storedCiphertext); // never plaintext at rest

        var decryptedJson = await envelopeCrypto.DecryptAsync(server.SpaceId, McpServerSecretCrypto.OAuthAad(server.Id), storedCiphertext!, default);
        var roundTripped = McpOAuthTokenDocument.Deserialize(decryptedJson);
        Assert.Equal(doc, roundTripped);

        await repository.ClearMcpServerOAuthTokenAsync(server.Id, default);
        Assert.Null(await repository.GetMcpServerOAuthTokenCiphertextAsync(server.Id, default));
    }

    [Fact]
    public void ToString_RedactsSecretMembers_KeepsDiagnostics()
    {
        // T1 opus-gate defense-in-depth: McpOAuthTokenDocument is a positional record whose
        // auto-generated ToString would print AccessToken/RefreshToken/ClientSecret verbatim — a
        // single LogError(ex, "...{Doc}", doc) in the Task 4/5 logged paths would then leak them.
        var doc = new McpOAuthTokenDocument(
            AccessToken: "at-SECRET-12345", RefreshToken: "rt-SECRET-67890",
            AccessExpiry: DateTimeOffset.UtcNow.AddHours(1),
            TokenEndpoint: "https://as.example.test/token", Issuer: "https://as.example.test",
            ClientId: "client-abc", ClientSecret: "cs-SECRET-xyz");

        var s = doc.ToString();

        Assert.DoesNotContain("at-SECRET-12345", s);
        Assert.DoesNotContain("rt-SECRET-67890", s);
        Assert.DoesNotContain("cs-SECRET-xyz", s);
        Assert.Contains("client-abc", s);                         // non-sensitive diagnostics kept
        Assert.Contains("https://as.example.test/token", s);
        Assert.Contains("HasRefreshToken = True", s);
        Assert.Contains("HasClientSecret = True", s);
    }
}
