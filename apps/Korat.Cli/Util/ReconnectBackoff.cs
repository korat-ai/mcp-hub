namespace Korat.Cli.Util;

/// <summary>
/// Exponential backoff for outer reconnect loops in the publisher daemon and
/// <c>korat up</c>. Delays double on each call (1 s, 2 s, 4 s, … capped at 30 s).
/// Call <see cref="Reset"/> after a long-lived connection so the next disconnect
/// starts from the minimum delay again.
/// </summary>
internal sealed class ReconnectBackoff
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    // A connection is considered "long-lived" if it stays up for at least this
    // duration before dying. On a long-lived connection the backoff resets so
    // a brief blip (cloud restart) doesn't permanently park the delay at max.
    private static readonly TimeSpan LongLivedThreshold = TimeSpan.FromSeconds(60);

    private readonly Func<DateTimeOffset> _now;
    private TimeSpan _current = MinDelay;
    private DateTimeOffset? _connectedAt;

    /// <summary>
    /// Production constructor — uses the real wall clock.
    /// </summary>
    public ReconnectBackoff() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>
    /// Test constructor — inject a controllable clock so the 60 s long-lived-reset
    /// branch can be exercised without actual wall-clock delays.
    /// </summary>
    internal ReconnectBackoff(Func<DateTimeOffset> now) => _now = now;

    /// <summary>
    /// Record the moment the connection was successfully established.
    /// Call this once per successful ConnectAsync so <see cref="OnDisconnect"/>
    /// can decide whether to reset the delay.
    /// </summary>
    public void OnConnected() => _connectedAt = _now();

    /// <summary>
    /// Called when the connection is lost. Resets the delay if the connection
    /// was long-lived; otherwise advances to the next backoff step.
    /// Returns the delay to wait before the next reconnect attempt.
    /// </summary>
    public TimeSpan OnDisconnect()
    {
        if (_connectedAt.HasValue &&
            _now() - _connectedAt.Value >= LongLivedThreshold)
        {
            _current = MinDelay;
        }

        var delay = _current;
        // Advance for next call (capped at MaxDelay).
        _current = _current * 2 < MaxDelay ? _current * 2 : MaxDelay;
        _connectedAt = null;
        return delay;
    }

    /// <summary>Explicit reset — useful for tests.</summary>
    public void Reset()
    {
        _current = MinDelay;
        _connectedAt = null;
    }

    /// <summary>
    /// Returns the backoff sequence starting from <paramref name="start"/>
    /// (for unit testing only). Does not mutate state.
    /// </summary>
    public static IEnumerable<TimeSpan> Sequence(TimeSpan start, int count)
    {
        var cur = start;
        for (var i = 0; i < count; i++)
        {
            yield return cur;
            cur = cur * 2 < MaxDelay ? cur * 2 : MaxDelay;
        }
    }
}
