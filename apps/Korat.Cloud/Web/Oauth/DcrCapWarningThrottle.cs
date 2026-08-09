namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Registration-flood-DoS hardening, fable holistic review FIX 2: today both DCR cap gates
/// (<see cref="SpaceMcpDcrOptions.MaxUnconsentedClients"/> primary,
/// <see cref="SpaceMcpDcrOptions.MaxClients"/> backstop) return a bounded 503 but emit no signal
/// at all — the exact event this defends against (an active registration flood) produces zero
/// operator-visible trace, so the <see cref="SpaceMcpDcrOptions.Enabled"/> kill switch has no
/// trigger. <see cref="DcrEndpoints"/> logs a warning on each gate trip, throttled through this
/// type so a SUSTAINED flood — which trips the gate on every request — does not itself flood the
/// logs.
///
/// A small injected singleton rather than a static field: keeps the throttle testable in
/// isolation (construct one, call <see cref="ShouldLog"/> directly) and scoped per DI container,
/// so each isolated <c>WithWebHostBuilder</c> test host in <c>DcrBoundsTests</c> gets its own
/// throttle state instead of sharing one across the whole test process.
///
/// Thread-safety: one <c>long</c> "last logged" tick per gate, read/written via
/// <see cref="Interlocked"/> against <see cref="Environment.TickCount64"/> (monotonic, does not
/// wrap within any realistic process lifetime — unlike the 32-bit <c>Environment.TickCount</c>,
/// which wraps every ~24.9 days). A compare-exchange loop makes the "claim this window" decision
/// atomic across concurrently racing requests: at most one caller per gate per window observes
/// <see cref="ShouldLog"/> return <see langword="true"/>.
/// </summary>
public sealed class DcrCapWarningThrottle
{
    /// <summary>Which of the two /connect/register cap gates tripped — kept as an enum (not a
    /// free-form string key) so call sites are self-documenting and there is no dictionary/lock
    /// needed for what is, at most, two gates.</summary>
    public enum Gate
    {
        /// <summary><see cref="SpaceMcpDcrOptions.MaxUnconsentedClients"/> — the PRIMARY gate.</summary>
        UnconsentedPrimary,
        /// <summary><see cref="SpaceMcpDcrOptions.MaxClients"/> — the SECONDARY backstop.</summary>
        TotalBackstop,
    }

    private const long WindowMilliseconds = 60_000;

    // long.MinValue is the "never logged yet" sentinel — distinguished explicitly from a real
    // tick value below rather than initialized to 0, which would (incorrectly) suppress the very
    // first warning for up to WindowMilliseconds after a machine boot (TickCount64 is uptime-based
    // and starts near 0, not process-start-based).
    private long _lastUnconsentedPrimaryTick = long.MinValue;
    private long _lastTotalBackstopTick = long.MinValue;

    /// <summary>
    /// Returns <see langword="true"/> at most once per <see cref="WindowMilliseconds"/> per
    /// <paramref name="gate"/> — the caller should log a warning iff this returns
    /// <see langword="true"/>. Every call that observes a fresh window (whether it wins the log
    /// or not) is otherwise a no-op: callers that lose the race simply skip logging this time.
    /// </summary>
    public bool ShouldLog(Gate gate) => gate switch
    {
        Gate.UnconsentedPrimary => ShouldLogCore(ref _lastUnconsentedPrimaryTick),
        Gate.TotalBackstop => ShouldLogCore(ref _lastTotalBackstopTick),
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, "Unknown DCR cap gate."),
    };

    private static bool ShouldLogCore(ref long lastLoggedTick)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            var last = Interlocked.Read(ref lastLoggedTick);
            if (last != long.MinValue && now - last < WindowMilliseconds)
                return false; // still inside the throttle window — suppress.

            // Try to claim this window. If another thread already advanced lastLoggedTick past
            // what we read (CompareExchange fails), loop and re-evaluate against the new value —
            // it may now be inside the window (lost the race, suppress) or still stale (retry).
            if (Interlocked.CompareExchange(ref lastLoggedTick, now, last) == last)
                return true;
        }
    }
}
