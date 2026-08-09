using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Korat.Cloud.Web.Oauth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2b, Task 5: the two capacity bounds on open DCR — a total-rows cap (bounded
/// 503) and a per-IP rate limit (429). Both run on ISOLATED derived hosts (own DI container,
/// own rate-limiter instance) so they neither pollute nor are polluted by the shared fixture's
/// DCR happy-path tests in <see cref="DcrRegistrationTests"/>.
///
/// MF-2 (plan-review correction, flaky-fix): a <c>WithWebHostBuilder</c>-derived host still
/// SHARES the fixture class's in-memory DB — <c>KoratTestHost.ConfigureWebHost</c> binds
/// <c>KoratTestDatabase.Name</c>/<c>Root</c>, STATIC fields reused by every derived host in this
/// class (and by <see cref="DcrRegistrationTests"/>'s own registrations) — and the row cap is
/// <c>CountAsync() >= MaxClients</c> over ALL rows, not just rows this test created. xUnit does
/// not guarantee intra-class test order, so the plan's original "MaxClients=1, first register
/// 201, second register 503" shape is start-count-dependent: if any sibling test's rows already
/// put the live count at or above 1 by the time this test runs, its FIRST register 503s where
/// the assertion expects 201 — flaky, order-dependent. Fixed here by setting MaxClients=0 and
/// asserting the FIRST register call → 503: "rejection at capacity" holds for ANY starting row
/// count (every count is >= 0), so this test no longer depends on running before, after, or
/// alongside any other test in the class.
///
/// Reality note (per the inc-2b Task 5 implementation brief, not in the original plan text):
/// <see cref="Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions.RegisterRateLimitPerMinute"/> itself IS
/// read live via DI and DOES respond to a <c>WithWebHostBuilder</c> override — same DI-factory
/// pattern as <c>MaxClients</c>/<c>MaxRequestBytes</c>/<c>Enabled</c> (see
/// <see cref="DcrRegistrationTests.Register_KillSwitchOff_Returns404"/>'s precedent note). But
/// the RATE LIMITER's actual permit count is a SEPARATE read: <c>Program.cs</c> computes
/// <c>dcrRegisterPerMinute</c> via <c>builder.Configuration.GetSection(...).Get&lt;...&gt;()</c>
/// directly at top level, BEFORE <c>WithWebHostBuilder</c>'s <c>ConfigureAppConfiguration</c>
/// override is merged into <c>builder.Configuration</c> — the same "eager read" shape the
/// Program.cs comment at the <c>SpaceMcpDcrOptions</c> DI-factory registration calls out (and
/// fixed) for the options object itself; this second read of the SAME section was never
/// migrated to a factory, because it feeds a constructor argument
/// (<c>AddKoratRateLimiting(..., dcrRegisterPerMinute)</c>) baked into the
/// <c>PartitionedRateLimiter</c>'s <c>PermitLimit</c> closure at host-build time, not something
/// resolved per-request. So overriding <c>Korat:Cloud:SpaceMcpDcr:RegisterRateLimitPerMinute</c>
/// via <c>ConfigureAppConfiguration</c> has NO effect on the limiter actually enforced — an
/// isolated host always runs the DEFAULT permit
/// (<see cref="Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions.RegisterRateLimitPerMinute"/>'s record
/// default, 20/min). <see cref="PerIpRateLimit_Exceeded_Returns429"/> therefore drives the
/// DEFAULT permit directly — send default-permit + 1 requests from one isolated host (own
/// rate-limiter instance, so no cross-test pollution) and assert a 429 appears — rather than
/// asserting on an override that would be silently ignored and make the test pass vacuously.
/// Widening the production rate-limiter wiring to a lazy per-partition permit read (option (b))
/// would touch <c>RateLimiterRegistration.AddKoratRateLimiting</c>, shared by every OTHER policy
/// in that file, for a test-only need — out of scope here.
///
/// Registration-flood-DoS hardening (below, appended to this class rather than a new one — same
/// "capacity bounds on /connect/register" concern as the row-cap test above): the PRIMARY
/// register-cap gate is now <see cref="SpaceMcpDcrOptions.MaxUnconsentedClients"/> — a count of
/// UNCONSENTED DCR clients only, so a junk-registration flood can never crowd out a real client
/// mid-consent. <see cref="SpaceMcpDcrOptions.MaxClients"/> (the test above) remains a secondary
/// total-rows backstop. Same MF-2 shared-in-memory-DB constraint applies here: every new test
/// below reads the CURRENT unconsented count via <see cref="IUnconsentedDcrClientCounter"/> first
/// and sets its cap RELATIVE to that baseline (baseline + N), so each test's assertions hold
/// regardless of what sibling tests in this class have already created — never an absolute
/// literal cap value, which (unlike <c>RowCap_Exceeded_ReturnsBounded503</c>'s MaxClients=0 trick)
/// would be order-dependent here since these tests need positive headroom to prove.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class DcrBoundsTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    /// <summary>SpaceMcpDcrOptions.RegisterRateLimitPerMinute's record default — see the Reality
    /// note above for why this test drives the default instead of a config override.</summary>
    private const int DefaultRegisterRateLimitPerMinute = 20;

    private static HttpContent RegisterBody(string redirect = "http://127.0.0.1:5000/cb") =>
        new StringContent(new JsonObject
        {
            ["client_name"] = "bounds",
            ["redirect_uris"] = new JsonArray { redirect },
        }.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Directly persists an UNCONSENTED DCR client row (mirrors
    /// DcrRegistrationReaperTests.CreateDcrClientAsync — same descriptor + marker-property shape
    /// DcrEndpoints.HandleRegisterAsync stamps) so a test can push the unconsented count to an
    /// exact value without going through rate-limited HTTP registration.</summary>
    private static async Task<string> CreateUnconsentedDcrClientAsync(
        IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        var clientId = "dcr_" + Guid.NewGuid().ToString("N");
        var descriptor = SpaceMcpOAuthClientSeeder.BuildDescriptor(new SpaceMcpOAuthOptions
        {
            ClientId = clientId,
            DisplayName = "bounds-test",
            RedirectUris = ["http://127.0.0.1:5000/cb"],
        });
        descriptor.Properties[KoratOAuthConstants.DcrMarkerProperty] = JsonSerializer.SerializeToElement("1");
        descriptor.Properties[KoratOAuthConstants.DcrRegisteredAtProperty] =
            JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O"));
        await apps.CreateAsync(descriptor, ct);
        return clientId;
    }

    /// <summary>Grants a currently-VALID authorization to an existing application — makes it
    /// "consented" for both the unconsented-cap counter and the TTL reaper. Mirrors
    /// DcrRegistrationReaperTests' inline authorization-creation pattern.</summary>
    private static async Task ConsentAsync(
        IOpenIddictApplicationManager apps, IOpenIddictAuthorizationManager auths, string clientId, CancellationToken ct)
    {
        var app = await apps.FindByClientIdAsync(clientId, ct);
        var appId = (await apps.GetIdAsync(app!, ct))!;
        await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = appId,
            Status = Statuses.Valid,
            Subject = Guid.NewGuid().ToString("N"),
            Type = AuthorizationTypes.Permanent,
        }, ct);
    }

    /// <summary>FIX 6 (fable holistic review NIT): mirrors <see cref="ConsentAsync"/> but grants a
    /// REVOKED authorization — "consented once, then revoked", NOT a live consent. Proves the
    /// counter's <c>NOT EXISTS ... Status == Valid</c> query treats a revoked-only client the same
    /// way <see cref="DcrRegistrationReaperService"/>'s MF-3 status-filtered check does: only a
    /// currently-VALID authorization excludes a client from "unconsented".</summary>
    private static async Task RevokeAsync(
        IOpenIddictApplicationManager apps, IOpenIddictAuthorizationManager auths, string clientId, CancellationToken ct)
    {
        var app = await apps.FindByClientIdAsync(clientId, ct);
        var appId = (await apps.GetIdAsync(app!, ct))!;
        await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = appId,
            Status = Statuses.Revoked,
            Subject = Guid.NewGuid().ToString("N"),
            Type = AuthorizationTypes.Permanent,
        }, ct);
    }

    [Fact]
    public async Task RowCap_Exceeded_ReturnsBounded503()
    {
        // MF-2: MaxClients=0 on a fresh isolated host — the row cap check is
        // `CountAsync() >= MaxClients`, so with MaxClients=0 EVERY count (including the shared
        // in-memory DB's starting count, whatever it is by the time this test runs) is already
        // "at capacity". The very first registration on this host must 503.
        using var host = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Korat:Cloud:SpaceMcpDcr:MaxClients"] = "0",
            })));
        var client = host.CreateClient();

        var response = await client.PostAsync("/connect/register", RegisterBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("temporarily_unavailable", doc["error"]!.GetValue<string>());
        // FIX 5 (NIT, fable holistic review): every cap-gate 503 carries an advisory back-off for
        // well-behaved clients.
        Assert.Equal(TimeSpan.FromSeconds(120), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task PerIpRateLimit_Exceeded_Returns429()
    {
        // Reality note above: a RegisterRateLimitPerMinute override never reaches the
        // already-built limiter, so this drives the DEFAULT permit (20/min) instead. The test
        // host still needs its OWN isolated instance (fresh rate-limiter, zero prior
        // consumption) so this loop's count is deterministic regardless of how many
        // /connect/register calls any other test in the fixture has made against the SHARED
        // host's limiter. Every request from one HttpClient shares a single partition (the test
        // server sees one "anon" IP for all of them), so the (default + 1)th request within the
        // 1-minute fixed window must 429.
        using var host = fixture.Factory.WithWebHostBuilder(_ => { });
        var client = host.CreateClient();

        var saw429 = false;
        for (var i = 0; i < DefaultRegisterRateLimitPerMinute + 5; i++)
        {
            var r = await client.PostAsync("/connect/register", RegisterBody($"http://127.0.0.1:{5000 + i}/cb"));
            if (r.StatusCode == HttpStatusCode.TooManyRequests) { saw429 = true; break; }
        }
        Assert.True(saw429, "expected a 429 after exceeding the per-IP DCR register limit");
    }

    /// <summary>SpaceMcpDcrOptions.RegisterSubnetRateLimitPerMinute's record default — same
    /// "drive the default, not an override" reason as <see cref="DefaultRegisterRateLimitPerMinute"/>
    /// (see <see cref="SubnetRateLimit_DistinctIpsInSameSubnet_Returns429FromSubnetBucket"/>'s
    /// remarks: the eager-read limitation applies identically to the subnet permit).</summary>
    private const int DefaultRegisterSubnetRateLimitPerMinute = 60;

    /// <summary>
    /// Registration-flood-DoS hardening item 3: behavioral proof that the per-SUBNET window
    /// engages on <c>POST /connect/register</c>, distinctly from the pre-existing per-IP window.
    ///
    /// Two layers of the SAME "eager read, no WithWebHostBuilder override" limitation this
    /// class's Reality note documents for <c>RegisterRateLimitPerMinute</c> ALSO apply here,
    /// and to <c>Korat:Cloud:TrustForwardedIp</c> itself (read the identical way, at the
    /// identical point in Program.cs, before builder.Build()):
    /// <list type="bullet">
    ///   <item>Neither <see cref="DefaultRegisterSubnetRateLimitPerMinute"/> nor the per-IP
    ///   permit can be dialed via a <c>WithWebHostBuilder</c>-time <c>ConfigureAppConfiguration</c>
    ///   override — both must be driven at their record defaults (20/min per-IP,
    ///   60/min per-subnet) on this isolated host.</item>
    ///   <item><c>Korat:Cloud:TrustForwardedIp</c> is fixed <c>false</c> in every test host in
    ///   this suite (no override reaches it either) — so <c>Fly-Client-IP</c> is normally
    ///   ignored and every in-process TestServer request resolves to the SAME client IP
    ///   ("anon", since <c>RemoteIpAddress</c> is null on the in-memory transport). With BOTH
    ///   limiters keyed identically under that default, per-IP (20) always fires before subnet
    ///   (60) in a plain volume test — that would only re-prove the pre-existing per-IP test,
    ///   never isolate the new subnet dimension.</item>
    /// </list>
    /// This test breaks that deadlock the same way <c>DeveloperApiAuthGateTests</c>
    /// ("the flag is read once at host build. Restore the variable afterwards — the test
    /// assembly is non-parallel.") does for <c>KORAT_ENABLE_DEVELOPER_API</c>: set the
    /// <c>KORAT__CLOUD__TRUSTFORWARDEDIP</c> ENVIRONMENT VARIABLE (not app configuration)
    /// BEFORE the host is built. <c>WebApplication.CreateBuilder(args)</c> wires
    /// <c>AddEnvironmentVariables()</c> as one of its OWN default sources, loaded synchronously
    /// at builder-creation time — strictly before Program.cs's <c>trustForwardedIp</c> eager
    /// read — so, unlike a <c>WithWebHostBuilder</c> override, this DOES reach it. The variable
    /// is restored immediately after the client is created (this test assembly disables
    /// parallelization).
    ///
    /// With <c>trustForwardedIp=true</c> on this ONE isolated host, distinct <c>Fly-Client-IP</c>
    /// values are sent, all within one /24 — each gets its OWN per-IP bucket (one request each,
    /// nowhere near the 20/min per-IP cap) but all share the SAME subnet bucket
    /// (<c>dcr-subnet4:203.0.113.0/24</c>, default 60/min). A 429 can therefore ONLY come from
    /// the subnet limiter, never the per-IP one — proving it is wired up and engages.
    /// </summary>
    [Fact]
    public async Task SubnetRateLimit_DistinctIpsInSameSubnet_Returns429FromSubnetBucket()
    {
        var previousTrustForwardedIp = Environment.GetEnvironmentVariable("KORAT__CLOUD__TRUSTFORWARDEDIP");
        Environment.SetEnvironmentVariable("KORAT__CLOUD__TRUSTFORWARDEDIP", "true");
        WebApplicationFactory<Program> host;
        HttpClient client;
        try
        {
            host = fixture.Factory.WithWebHostBuilder(_ => { });
            client = host.CreateClient();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KORAT__CLOUD__TRUSTFORWARDEDIP", previousTrustForwardedIp);
        }

        using (host)
        {
            var firstRejectIndex = -1;
            for (var i = 0; i < DefaultRegisterSubnetRateLimitPerMinute + 5; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/connect/register")
                {
                    Content = RegisterBody($"http://127.0.0.1:{7000 + i}/cb"),
                };
                // TEST-NET-3 (RFC 5737) — reserved, non-routable, safe to hardcode. Every value
                // below is a DISTINCT host within one /24, so the per-IP bucket for each of them
                // sees exactly one request this whole loop.
                request.Headers.Add("Fly-Client-IP", $"203.0.113.{i + 1}");
                var response = await client.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    firstRejectIndex = i;
                    Assert.NotNull(response.Headers.RetryAfter);
                    break;
                }
            }

            // Attribution guard (fable SHOULD-FIX): a 429 alone is NOT enough. If the env-var
            // plumbing that flips trustForwardedIp on ever silently breaks (key rename, eager-read
            // moves ahead of AddEnvironmentVariables, etc.), Fly-Client-IP is ignored and all 65
            // requests collapse into the ONE "anon" per-IP bucket — the 429 would then come from
            // the per-IP limiter at request #21, proving nothing about the subnet dimension (the
            // subnet branch could be deleted and this test would still go green). Each DISTINCT IP
            // spends exactly one per-IP permit, so a 429 that only appears AFTER more than the
            // per-IP allowance of distinct IPs can ONLY be the shared per-subnet bucket.
            Assert.True(firstRejectIndex >= 0, "expected a 429 from the per-subnet DCR register limit");
            Assert.True(firstRejectIndex > DefaultRegisterRateLimitPerMinute,
                $"429 first appeared at request index {firstRejectIndex}; expected it PAST the per-IP " +
                $"limit ({DefaultRegisterRateLimitPerMinute}). A 429 at/below that means the per-IP bucket " +
                "fired (trustForwardedIp plumbing broken) — not the per-subnet limiter under test.");
        }
    }

    // ── Registration-flood-DoS hardening: unconsented-only PRIMARY cap ─────────────────────────

    [Fact]
    public async Task Register_ConsentedDcrClient_DoesNotCountTowardUnconsentedCap()
    {
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var counter = scope.ServiceProvider.GetRequiredService<IUnconsentedDcrClientCounter>();
        var ct = CancellationToken.None;

        // Baseline + 1: exactly one slot of headroom on THIS isolated host's cap, regardless of
        // what sibling tests in this class have already created (MF-2 shared-DB constraint).
        var baseline = await counter.CountAsync(ct);
        using var host = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Korat:Cloud:SpaceMcpDcr:MaxUnconsentedClients"] = (baseline + 1).ToString(),
            })));
        var client = host.CreateClient();

        // A CONSENTED dcr_ client exists alongside the one slot of headroom — proves it does NOT
        // itself consume that slot: the register below still succeeds.
        var consentedClientId = await CreateUnconsentedDcrClientAsync(apps, ct);
        await ConsentAsync(apps, auths, consentedClientId, ct);

        var response = await client.PostAsync("/connect/register", RegisterBody("http://127.0.0.1:50101/cb"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_UnconsentedAtCap_Returns503_ThenConsentingFreesSlot()
    {
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var counter = scope.ServiceProvider.GetRequiredService<IUnconsentedDcrClientCounter>();
        var ct = CancellationToken.None;

        var baseline = await counter.CountAsync(ct);
        using var host = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Korat:Cloud:SpaceMcpDcr:MaxUnconsentedClients"] = (baseline + 1).ToString(),
            })));
        var client = host.CreateClient();

        // Fill the one slot of headroom with an UNCONSENTED dcr_ client — the register cap check
        // now sees baseline+1 unconsented rows >= cap(baseline+1) ⇒ at capacity.
        var fillerClientId = await CreateUnconsentedDcrClientAsync(apps, ct);

        var atCapacity = await client.PostAsync("/connect/register", RegisterBody("http://127.0.0.1:50201/cb"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, atCapacity.StatusCode);
        var doc = JsonNode.Parse(await atCapacity.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("temporarily_unavailable", doc["error"]!.GetValue<string>());

        // Consenting the filler client frees its slot — a flood of junk can never permanently
        // starve registration for a client that actually completes consent.
        await ConsentAsync(apps, auths, fillerClientId, ct);

        var afterConsent = await client.PostAsync("/connect/register", RegisterBody("http://127.0.0.1:50202/cb"));
        Assert.Equal(HttpStatusCode.Created, afterConsent.StatusCode);
    }

    [Fact]
    public async Task CountAsync_CountsOnlyUnconsentedDcrClients()
    {
        using var scope = fixture.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var counter = scope.ServiceProvider.GetRequiredService<IUnconsentedDcrClientCounter>();
        var ct = CancellationToken.None;

        var baseline = await counter.CountAsync(ct);

        // Non-DCR (the seeded pre-registered client, upserted idempotently) never counts.
        await fixture.EnsureOAuthClientAsync("http://127.0.0.1:45123/callback");
        Assert.Equal(baseline, await counter.CountAsync(ct));

        // An UNCONSENTED dcr_ client adds exactly one.
        await CreateUnconsentedDcrClientAsync(apps, ct);
        Assert.Equal(baseline + 1, await counter.CountAsync(ct));

        // A CONSENTED dcr_ client adds nothing — a currently-VALID authorization excludes it.
        var consented = await CreateUnconsentedDcrClientAsync(apps, ct);
        await ConsentAsync(apps, auths, consented, ct);
        Assert.Equal(baseline + 1, await counter.CountAsync(ct));

        // FIX 6 (fable holistic review NIT): a REVOKED-only dcr_ client counts as UNCONSENTED —
        // mirrors DcrRegistrationReaperService's MF-3 (a consented-then-revoked client is not a
        // live consent). Without this, a flood that registers, gets manually
        // consented-then-revoked by an operator, would wrongly keep sitting outside the primary
        // cap forever.
        var revokedOnly = await CreateUnconsentedDcrClientAsync(apps, ct);
        await RevokeAsync(apps, auths, revokedOnly, ct);
        Assert.Equal(baseline + 2, await counter.CountAsync(ct));
    }
}
