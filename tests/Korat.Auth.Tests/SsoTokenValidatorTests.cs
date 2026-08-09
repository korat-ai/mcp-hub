using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>
/// Validation of access tokens issued by Korat SSO.
///
/// The live half — a token this app has never seen, verified against the provider's published
/// keys — only runs when KORAT_SSO_LIVE_TOKEN is set, because a real token expires and cannot
/// be checked into a repository. Obtain one with <c>tools/verify-live-e2e.py</c> in korat-sso.
/// Without it these tests still pin the behaviour that matters when the provider is absent or
/// the input is not ours at all.
/// </summary>
public sealed class SsoTokenValidatorTests
{
    private static SsoTokenValidator Build(string? issuer, string? client = "korat-probe")
    {
        var settings = new Dictionary<string, string?>();
        if (issuer is not null) settings["Sso:Issuer"] = issuer;
        if (issuer is not null && client is not null) settings["Sso:AllowedClients:0"] = client;

        return new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<SsoTokenValidator>.Instance);
    }

    [Fact]
    public async Task Without_an_issuer_the_validator_stays_out_of_the_way()
    {
        var validator = Build(null);

        // Not being an SSO client yet is a normal state, not a failure: this app's own
        // credentials remain the way in until the switch-over. Throwing here would make an
        // optional integration mandatory.
        Assert.False(validator.Enabled);
        Assert.Null(await validator.ValidateAsync("anything", CancellationToken.None));
    }

    [Theory]
    [InlineData("korat_cli_abcdef0123456789")]   // this app's own opaque credential
    [InlineData("")]
    [InlineData("not.a.jwt.at.all.really")]
    [InlineData("only.two")]
    public async Task Credentials_that_are_not_ours_are_rejected_without_a_round_trip(string token)
    {
        // Pointed at an address that would fail if it were ever contacted: these inputs must
        // be turned away on shape alone. Otherwise every CLI-token request would pay for a
        // discovery lookup, and an unreachable provider would stall this app's own auth.
        var validator = Build("https://sso.invalid/");

        Assert.Null(await validator.ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_real_token_from_the_live_provider_is_accepted()
    {
        var token = Environment.GetEnvironmentVariable("KORAT_SSO_LIVE_TOKEN");
        // Inert without the variable rather than failing: CI has no live provider. The run
        // that matters is the local one, right after a token is minted.
        if (string.IsNullOrWhiteSpace(token)) return;

        var validator = Build(Environment.GetEnvironmentVariable("KORAT_SSO_ISSUER") ?? "https://id.korat.dev/");
        var principal = await validator.ValidateAsync(token!, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.NotEmpty(principal!.Subject);

        // The device identifier is what a per-device consumer identity is derived from. If it
        // stops arriving, per-device grants silently collapse into per-person grants.
        Assert.NotNull(principal.DeviceId);
    }

    [Fact]
    public void An_issuer_without_an_allowed_client_list_refuses_to_start()
    {
        // Half a configuration is worse than none. With no issuer the validator is inert
        // and this app's own credentials still work; with an issuer and no client list it
        // would accept tokens minted for any client the provider knows — and nothing about
        // that is visible until someone walks in.
        var failure = Assert.Throws<InvalidOperationException>(
            () => Build("https://id.korat.dev/", client: null));

        Assert.Contains("AllowedClients", failure.Message);
    }

    [Fact]
    public async Task A_token_for_a_client_we_do_not_accept_is_rejected()
    {
        var token = Environment.GetEnvironmentVariable("KORAT_SSO_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return;

        // Signature valid, issuer right, lifetime fine — only the client is a stranger.
        // Accepting this would let the provider decide who gets into this app.
        var validator = Build("https://id.korat.dev/", client: "somebody-elses-app");

        Assert.Null(await validator.ValidateAsync(token!, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_signed_by_someone_else_is_rejected()
    {
        var token = Environment.GetEnvironmentVariable("KORAT_SSO_LIVE_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return;

        // Same token, one bit of the signature flipped. Accepting this would mean the
        // signature is not actually being checked — the failure that looks like success.
        var parts = token!.Split('.');
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A';
        var forged = $"{parts[0]}.{parts[1]}.{new string(signature)}";

        var validator = Build("https://id.korat.dev/");
        Assert.Null(await validator.ValidateAsync(forged, CancellationToken.None));
    }
}
