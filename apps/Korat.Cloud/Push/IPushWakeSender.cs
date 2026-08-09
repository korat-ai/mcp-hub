namespace Korat.Cloud.Push;

/// <summary>
/// Sends a silent push notification to wake a backgrounded iOS node (030 push-to-wake).
/// Implementations must never throw into the caller — all errors are captured as
/// <see cref="PushWakeResult"/> values.
/// </summary>
public interface IPushWakeSender
{
    /// <summary>
    /// Best-effort: returns the outcome, never throws into the caller.
    /// </summary>
    /// <param name="token">APNs device token (lowercase hex).</param>
    /// <param name="platform">"apns" for production, "apns_sandbox" for debug/TestFlight builds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PushWakeResult> SendWakeAsync(string token, string platform, CancellationToken ct);
}

/// <summary>Outcome of a single silent-push attempt.</summary>
public enum PushWakeResult
{
    /// <summary>APNs accepted the push (HTTP 200).</summary>
    Sent,

    /// <summary>
    /// The device token is no longer valid (HTTP 410 Unregistered or 400 BadDeviceToken).
    /// The caller should clear the stored token.
    /// </summary>
    TokenInvalid,

    /// <summary>
    /// APNs returned a transient error (HTTP 429 or 5xx). Logged at Warning; no automatic
    /// retry — the dedup window and agent retries provide natural pacing.
    /// </summary>
    Failed,

    /// <summary>
    /// APNs is not configured (KeyId absent). The wake path degrades to the immediate
    /// ServerUnavailable response.
    /// </summary>
    NotConfigured
}
