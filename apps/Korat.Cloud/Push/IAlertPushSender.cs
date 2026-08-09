namespace Korat.Cloud.Push;

/// <summary>
/// 031 (mobile-push increment 2): sends a visible alert push (as opposed to <see cref="IPushWakeSender"/>'s
/// silent wake) to one device token. Implementations must never throw into the caller — all errors
/// are captured as <see cref="AlertSendResult"/> values, mirroring <see cref="IPushWakeSender"/>.
/// </summary>
public interface IAlertPushSender
{
    /// <param name="token">Device push token (APNs hex token, or FCM registration token).</param>
    /// <param name="platform">"apns" | "apns_sandbox" | "fcm".</param>
    Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct);
}

/// <summary>Outcome of a single alert-push attempt.</summary>
public enum AlertSendResult
{
    /// <summary>The platform's push service accepted the message.</summary>
    Delivered,

    /// <summary>
    /// A transient failure (network error, 5xx, rate-limited, or the platform sender is a
    /// <see cref="NullAlertPushSender"/> because its secrets are absent). Logged; no automatic
    /// retry — the notifier's own per-space throttle and the agent's natural retry cadence
    /// provide pacing.
    /// </summary>
    TransientFailure,

    /// <summary>
    /// The device token is no longer valid (APNs 410/400-BadDeviceToken, FCM Unregistered/NotFound).
    /// The caller should compare-and-clear the stored token (see <c>INodeGrain.ClearPushTokenIfMatchesAsync</c>).
    /// </summary>
    TokenInvalid,
}

/// <summary>
/// 031: the alert payload, platform-agnostic. Never crosses a grain boundary (built and consumed
/// entirely inside Korat.Cloud), so — unlike <c>CreateAccessRequestResult</c> — this is a PLAIN
/// record with no <c>[GenerateSerializer]</c>.
/// </summary>
/// <param name="Title">Notification title (APNs `aps.alert.title` / FCM `data.title`).</param>
/// <param name="Body">Notification body — already sanitized/truncated by the caller (see
/// <c>AlertContentFormatter</c>). APNs renders it into `aps.alert.body`; FCM is data-only (§4d)
/// so it goes into `data.body` for the Android client to render itself.</param>
/// <param name="Data">Custom payload, e.g. <c>{ "type": "access_request", "accessRequestId": "…" }</c>.</param>
public sealed record AlertContent(string Title, string Body, IReadOnlyDictionary<string, string> Data);
