using System.Text.Json;
using Korat.Cloud.Maintenance;
using Korat.Cloud.Web.Oauth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2b, Task 6: the TTL sweep deletes an OLD, NEVER-CONSENTED DCR client but leaves
/// (a) a consented DCR client, (b) a RECENT DCR client, and (c) the seeded pre-registered client
/// (which is not DCR-marked). This bounds the DCR-re-registration churn (spec open-q #3):
/// per-launch re-registrations that never reach consent self-expire.
///
/// MF-3 (plan-review): a client whose ONLY authorization was later REVOKED must NOT be kept
/// forever — <see cref="DcrRegistrationReaperService.SweepCoreAsync"/> treats only
/// <see cref="Statuses.Valid"/> authorizations as "consented ⇒ keep" (verified against the
/// installed OpenIddict 7.5.0: <c>FindByApplicationIdAsync</c> returns ALL authorizations
/// regardless of status, so using it directly would wrongly keep a revoked-only client forever).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class DcrRegistrationReaperTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    // Hardening (registration-flood DoS): TTL moved from hours to minutes — junk self-drains in
    // minutes, not hours. 15m mirrors the new record default (long enough to cover a first-time
    // interactive sign-in + consent — see SpaceMcpDcrOptions.UnconsentedTtlMinutes's doc comment);
    // old/recent below straddle it exactly (old = TTL+1m ago ⇒ reaped, recent = inside the TTL ⇒
    // kept) so this test proves MINUTE granularity, not just "some long time ago vs some short
    // time ago".
    private static readonly SpaceMcpDcrOptions Options = new() { UnconsentedTtlMinutes = 15 };

    private static async Task<string> CreateDcrClientAsync(
        IOpenIddictApplicationManager apps, DateTimeOffset registeredAt, CancellationToken ct)
    {
        var clientId = "dcr_" + Guid.NewGuid().ToString("N");
        var descriptor = SpaceMcpOAuthClientSeeder.BuildDescriptor(new SpaceMcpOAuthOptions
        {
            ClientId = clientId,
            DisplayName = "reaper-test",
            RedirectUris = ["http://127.0.0.1:5000/cb"],
        });
        descriptor.Properties[KoratOAuthConstants.DcrMarkerProperty] = JsonSerializer.SerializeToElement("1");
        descriptor.Properties[KoratOAuthConstants.DcrRegisteredAtProperty] =
            JsonSerializer.SerializeToElement(registeredAt.ToString("O"));
        await apps.CreateAsync(descriptor, ct);
        return clientId;
    }

    [Fact]
    public async Task Sweep_DeletesOldUnconsented_KeepsConsentedRecentAndSeeded()
    {
        await fixture.EnsureOAuthClientAsync("http://127.0.0.1:45123/callback"); // the seeded, NON-DCR client
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var ct = CancellationToken.None;

        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(16);   // TTL(15m) + 1m ago ⇒ reaped
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5); // inside the 15m TTL ⇒ kept

        var oldUnconsented = await CreateDcrClientAsync(apps, old, ct);
        var oldConsented = await CreateDcrClientAsync(apps, old, ct);
        var recentUnconsented = await CreateDcrClientAsync(apps, recent, ct);

        // Give oldConsented a permanent authorization (it was consented at least once) ⇒ keep it.
        var consentedApp = await apps.FindByClientIdAsync(oldConsented, ct);
        var consentedAppId = (await apps.GetIdAsync(consentedApp!, ct))!;
        await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = consentedAppId,
            Status = Statuses.Valid,
            Subject = Guid.NewGuid().ToString("N"),
            Type = AuthorizationTypes.Permanent,
        }, ct);

        var deleted = await DcrRegistrationReaperService.SweepCoreAsync(
            apps, auths, Options, NullLogger.Instance, ct);

        Assert.True(deleted >= 1);
        Assert.Null(await apps.FindByClientIdAsync(oldUnconsented, ct));      // swept
        Assert.NotNull(await apps.FindByClientIdAsync(oldConsented, ct));     // consented — kept
        Assert.NotNull(await apps.FindByClientIdAsync(recentUnconsented, ct));// recent — kept
        Assert.NotNull(await apps.FindByClientIdAsync(KoratOAuthConstants.DefaultClientId, ct)); // seeded — kept
    }

    /// <summary>MF-3: a DCR client past TTL whose ONLY authorization was REVOKED must be reaped,
    /// not kept forever. <c>FindByApplicationIdAsync</c> returns ALL authorizations regardless of
    /// status, so a naive "any authorization ⇒ keep" check (the plan-review-flagged bug) would
    /// wrongly retain this row indefinitely. The sweep's status-filtered check must reap it.</summary>
    [Fact]
    public async Task Sweep_RevokedOnlyAuthorization_IsReaped()
    {
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var ct = CancellationToken.None;

        var old = DateTimeOffset.UtcNow - TimeSpan.FromHours(48);
        var revokedOnly = await CreateDcrClientAsync(apps, old, ct);

        var app = await apps.FindByClientIdAsync(revokedOnly, ct);
        var appId = (await apps.GetIdAsync(app!, ct))!;
        await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = appId,
            Status = Statuses.Revoked, // consented once, then revoked — NOT a live consent
            Subject = Guid.NewGuid().ToString("N"),
            Type = AuthorizationTypes.Permanent,
        }, ct);

        var deleted = await DcrRegistrationReaperService.SweepCoreAsync(
            apps, auths, Options, NullLogger.Instance, ct);

        Assert.True(deleted >= 1);
        Assert.Null(await apps.FindByClientIdAsync(revokedOnly, ct)); // revoked-only ⇒ reaped, not kept forever
    }

    [Fact]
    public async Task Sweep_IsIdempotent_SecondRunNoThrow()
    {
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var ct = CancellationToken.None;
        await CreateDcrClientAsync(apps, DateTimeOffset.UtcNow - TimeSpan.FromHours(48), ct);

        await DcrRegistrationReaperService.SweepCoreAsync(apps, auths, Options, NullLogger.Instance, ct);
        await DcrRegistrationReaperService.SweepCoreAsync(apps, auths, Options, NullLogger.Instance, ct); // no throw
    }

    /// <summary>Registration-flood-DoS hardening: the default TTL moved from 2h (SF-2) to 15
    /// MINUTES — long enough to cover a first-time interactive sign-in (incl. 2FA) + consent
    /// (the TTL clock starts at registration, not at consent-page-load), while the row cap it
    /// protects is now the PRIMARY unconsented-only gate
    /// (<see cref="SpaceMcpDcrOptions.MaxUnconsentedClients"/>), so junk still drains fast enough
    /// to keep that budget from filling under a sustained flood. Pins the corrected default so a
    /// future edit can't silently regress it.</summary>
    [Fact]
    public void UnconsentedTtlMinutes_DefaultsToFifteenMinutes()
    {
        Assert.Equal(15, new SpaceMcpDcrOptions().UnconsentedTtlMinutes);
    }

    /// <summary>Sibling pin: the sweep now runs every 5 minutes (was hourly) so a filled
    /// unconsented budget still drains within roughly one TTL window under a sustained
    /// registration flood.</summary>
    [Fact]
    public void SweepIntervalMinutes_DefaultsToFiveMinutes()
    {
        Assert.Equal(5, new SpaceMcpDcrOptions().SweepIntervalMinutes);
    }
}
