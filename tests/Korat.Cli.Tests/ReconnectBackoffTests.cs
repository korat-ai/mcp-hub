using Korat.Cli.Util;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="ReconnectBackoff"/>:
/// - Verifies the backoff sequence (1s, 2s, 4s, … capped at 30s).
/// - Verifies that a long-lived connection resets the delay to minimum.
/// - Verifies that a short-lived connection advances the delay normally.
/// </summary>
public class ReconnectBackoffTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Static sequence helper
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sequence_starts_at_1s_and_doubles()
    {
        var seq = ReconnectBackoff.Sequence(TimeSpan.FromSeconds(1), 5).ToList();

        Assert.Equal(TimeSpan.FromSeconds(1), seq[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), seq[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), seq[2]);
        Assert.Equal(TimeSpan.FromSeconds(8), seq[3]);
        Assert.Equal(TimeSpan.FromSeconds(16), seq[4]);
    }

    [Fact]
    public void Sequence_caps_at_30s()
    {
        var seq = ReconnectBackoff.Sequence(TimeSpan.FromSeconds(1), 10).ToList();

        // After enough doublings we must hit and stay at 30s.
        Assert.Equal(TimeSpan.FromSeconds(30), seq[^1]);
        // No element should exceed 30s.
        Assert.All(seq, d => Assert.True(d <= TimeSpan.FromSeconds(30)));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Instance state machine
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnDisconnect_without_OnConnected_returns_increasing_delays()
    {
        var b = new ReconnectBackoff();

        var d1 = b.OnDisconnect();
        var d2 = b.OnDisconnect();
        var d3 = b.OnDisconnect();

        Assert.Equal(TimeSpan.FromSeconds(1), d1);
        Assert.Equal(TimeSpan.FromSeconds(2), d2);
        Assert.Equal(TimeSpan.FromSeconds(4), d3);
    }

    [Fact]
    public void OnDisconnect_caps_at_30s()
    {
        var b = new ReconnectBackoff();

        TimeSpan last = TimeSpan.Zero;
        for (var i = 0; i < 10; i++)
            last = b.OnDisconnect();

        Assert.Equal(TimeSpan.FromSeconds(30), last);
    }

    [Fact]
    public void OnConnected_then_OnDisconnect_resets_delay_when_lived_long_enough()
    {
        // Advance the backoff to max first.
        var b = new ReconnectBackoff();
        for (var i = 0; i < 8; i++) b.OnDisconnect();

        // Simulate a long-lived connection: set connected time well in the past
        // by calling OnConnected then manually advancing — we use the public API
        // by calling OnConnected and then checking that OnDisconnect returns 1s
        // when we claim to have been connected for > 60s.
        // Since we can't travel time, we test the Reset path instead.
        b.Reset();
        var d = b.OnDisconnect();
        Assert.Equal(TimeSpan.FromSeconds(1), d);
    }

    [Fact]
    public void Reset_restarts_sequence_from_minimum()
    {
        var b = new ReconnectBackoff();
        b.OnDisconnect(); // 1s
        b.OnDisconnect(); // 2s
        b.OnDisconnect(); // 4s

        b.Reset();
        var d = b.OnDisconnect();
        Assert.Equal(TimeSpan.FromSeconds(1), d);
    }

    [Fact]
    public void OnConnected_followed_by_immediate_OnDisconnect_does_NOT_reset_delay()
    {
        // A very short-lived connection (<60s) should NOT reset the backoff,
        // so consecutive fast failures don't busy-retry at minimum delay.
        var b = new ReconnectBackoff();
        b.OnDisconnect(); // 1s → current becomes 2s
        b.OnDisconnect(); // 2s → current becomes 4s

        // Connected, then immediately disconnected (no time passes).
        b.OnConnected();
        var d = b.OnDisconnect(); // current is still 4s (not reset)
        Assert.Equal(TimeSpan.FromSeconds(4), d);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // L3: injectable clock tests for the long-lived-reset branch
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LongLived_connection_resets_backoff_to_minimum()
    {
        // Simulate a connection that was alive for exactly 61 s (> 60 s threshold).
        var baseTime = DateTimeOffset.UtcNow;
        var clock = baseTime;
        var b = new ReconnectBackoff(() => clock);

        // Advance the backoff to max first.
        for (var i = 0; i < 8; i++) b.OnDisconnect();

        // Connect at t=0.
        b.OnConnected();

        // Advance clock to t=61s (past the 60 s threshold) before disconnecting.
        clock = baseTime.AddSeconds(61);
        var delay = b.OnDisconnect();

        // Backoff must reset to 1 s (minimum).
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void ShortLived_connection_does_NOT_reset_backoff()
    {
        // Simulate a connection alive for 30 s (< 60 s threshold) — backoff must advance.
        var baseTime = DateTimeOffset.UtcNow;
        var clock = baseTime;
        var b = new ReconnectBackoff(() => clock);

        b.OnDisconnect(); // 1s → current 2s
        b.OnDisconnect(); // 2s → current 4s

        // Connect at t=0, disconnect after 30 s (short-lived).
        b.OnConnected();
        clock = baseTime.AddSeconds(30);
        var delay = b.OnDisconnect(); // must continue from 4s

        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }
}
