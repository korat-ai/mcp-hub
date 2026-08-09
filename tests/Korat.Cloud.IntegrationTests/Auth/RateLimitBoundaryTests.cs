using System.Net;
using System.Net.Http.Json;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Rate-limit boundary tests.
///
/// Verifies that representative rate-limit policies return 429 when their permit limit
/// is exhausted and that the /api/auth/cli/revoke-all endpoint is rate-limited (fix #5).
///
/// Note: rate limiter policies use a fixed-window FixedWindowRateLimiterOptions with
/// QueueLimit=0 so excess requests return 429 immediately with no queuing.
/// The test host runs all requests from the same "IP" (RemoteIpAddress=null → "anon")
/// which means per-IP policies share a single partition across all tests in a fixture.
/// To avoid cross-test pollution, each test uses a unique per-session key where available.
/// </summary>
public sealed class RateLimitBoundaryTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── MagicLinkRequest policy: 5 permits / hour per IP ─────────────────────

    [Fact]
    public async Task MagicLinkRequest_Returns429_AfterPermitLimitExhausted()
    {
        // MagicLinkRequestPolicy: 5 permits / hour per IP. The endpoint is POST /signin/magic-link
        // (NOT /api/auth/magic-link/request) and is antiforgery-protected. UseAntiforgery runs
        // BEFORE UseRateLimiter in the pipeline, so we need an antiforgery-token client to get
        // past antiforgery and have requests actually reach (and be counted by) the limiter.
        // The handler returns 204 (anti-enumeration) or 400 (bad-origin) — both still count toward
        // the limiter. We fire past the limit and assert a 429 appears. Robust to shared-partition
        // pollution from other tests: the 1-hour fixed window does not roll within a test run, so
        // once the window is exhausted every further request is 429.
        var seeded = await fixture.SeedUserAsync(
            $"rate-limit-ml-{Guid.NewGuid():N}@example.com", "Rate Limit ML");
        var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);
        var targetEmail = $"ml-target-{Guid.NewGuid():N}@example.com";

        HttpResponseMessage? last = null;
        var hitLimit = false;
        for (var i = 0; i < 8; i++) // > the 5-permit window
        {
            last = await client.PostAsJsonAsync("/signin/magic-link", new { email = targetEmail });
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
            {
                hitLimit = true;
                break;
            }
        }

        Assert.True(hitLimit,
            $"Expected 429 after exceeding MagicLinkRequestPolicy (5/hr), last status: {last?.StatusCode}");
    }

    // ── CliDeviceCode policy: 20 permits / minute per IP ─────────────────────

    [Fact]
    public async Task CliDeviceCode_Returns429_AfterPermitLimitExhausted()
    {
        // CliDeviceCodePolicy: 20/min per IP.
        var client = fixture.Factory.CreateClient();

        for (var i = 0; i < 20; i++)
        {
            var r = await client.PostAsync("/api/auth/cli/device-code", null);
            // Expect 200 or other non-429 for the first 20.
            Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode);
        }

        var overLimit = await client.PostAsync("/api/auth/cli/device-code", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    // ── /revoke-all is rate-limited (fix #5) ─────────────────────────────────

    [Fact]
    public async Task CliRevokeAll_IsRateLimited_WhenLimitExceeded()
    {
        // /api/auth/cli/revoke-all uses AuthDefaultPolicy (60/min per session).
        // We verify it eventually returns 429 by exhausting the session budget.
        // Use an authenticated+antiforgery client so we reach the rate limiter
        // (antiforgery fires before the rate limiter; without a valid token we'd
        // only get 400 and never reach the rate-limit check).
        var seeded = await fixture.SeedUserAsync(
            $"revoke-all-rl-{Guid.NewGuid():N}@example.com", "Rate Limit Test");
        var authedClient = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Exhaust the AuthDefaultPolicy budget (60 permits / min).
        // Each successful or failed request counts against the budget.
        HttpResponseMessage? lastResponse = null;
        var hitLimit = false;
        for (var i = 0; i < 65; i++)
        {
            lastResponse = await authedClient.PostAsync("/api/auth/cli/revoke-all", null);
            if (lastResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                hitLimit = true;
                break;
            }
        }

        Assert.True(hitLimit,
            $"Expected 429 after exceeding AuthDefaultPolicy limit (60/min) for /revoke-all, " +
            $"last status: {lastResponse?.StatusCode}");
    }
}
