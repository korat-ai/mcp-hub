using Korat.Cloud.Web.Oauth;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Fable holistic review FIX 2: <see cref="DcrCapWarningThrottle"/> is what keeps a SUSTAINED
/// registration flood — which trips a /connect/register cap gate on every request — from flooding
/// the logs once DcrEndpoints starts logging a warning on each gate trip. Unit-tested directly
/// against the singleton (per the plan's own note: simpler than wiring a log-capture into the
/// shared integration host, and the throttle's own contract — "true at most once per ~60s window,
/// per gate" — is what actually needs proving here, not DcrEndpoints' call site).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class DcrCapWarningThrottleTests
{
    [Fact]
    public void ShouldLog_TwoRapidCallsSameGate_OnlyTheFirstLogs()
    {
        var throttle = new DcrCapWarningThrottle();

        // Simulates two rapid at-capacity /connect/register requests hitting the same gate.
        var first = throttle.ShouldLog(DcrCapWarningThrottle.Gate.UnconsentedPrimary);
        var second = throttle.ShouldLog(DcrCapWarningThrottle.Gate.UnconsentedPrimary);

        Assert.True(first, "the first call in a fresh window must log — the operator needs to see the FIRST cap hit.");
        Assert.False(second, "a second call inside the same ~60s window must be suppressed — a sustained flood must not flood the logs.");
    }
}
