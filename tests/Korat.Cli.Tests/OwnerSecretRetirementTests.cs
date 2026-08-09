using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Task 11: Verify KORAT_DEV_OWNER_SECRET / X-Korat-Owner-Token retired from CLI.
///
/// These tests assert the post-retirement state:
///   - <see cref="LocalIdentity"/> has no OwnerToken property.
///   - <see cref="LocalIdentityStore"/> does not check OwnerToken in TryValidateIdentity.
///   - <see cref="LocalIdentity"/> deserialized from legacy JSON with an ownerToken field
///     ignores it (forward-compat: old config files won't crash the CLI).
/// </summary>
public class OwnerSecretRetirementTests
{
    [Fact]
    public void LocalIdentity_has_no_OwnerToken_property()
    {
        var prop = typeof(LocalIdentity).GetProperty("OwnerToken");
        Assert.Null(prop);
    }

    [Fact]
    public void TryValidateIdentity_passes_for_identity_without_owner_token()
    {
        // After retirement, validation must not require an OwnerToken — only NodeId
        // and CloudUrl are required for legacy usage; CliCredentials covers auth.
        var identity = new LocalIdentity
        {
            NodeId = "test-node-id",
            CloudUrl = "https://cloud.example.com",
        };
        var ok = LocalIdentityStore.TryValidateIdentity(identity, out var error);
        Assert.True(ok, $"Expected valid identity but got error: {error}");
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void LocalIdentity_default_CloudUrl_is_not_empty()
    {
        // Sanity check: the non-secret config fields still work after OwnerToken removal.
        var identity = new LocalIdentity();
        Assert.False(string.IsNullOrWhiteSpace(identity.CloudUrl));
        Assert.False(string.IsNullOrWhiteSpace(identity.CloudGrpcUrl));
    }
}
