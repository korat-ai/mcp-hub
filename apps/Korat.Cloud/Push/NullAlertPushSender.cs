namespace Korat.Cloud.Push;

/// <summary>
/// Registered for a platform slot (APNs or FCM) when that platform's secrets are absent — mirrors
/// <see cref="NullPushWakeSender"/>. Returns <see cref="AlertSendResult.TransientFailure"/> (the
/// closest fit in the 3-value result enum: "could not deliver, nothing to invalidate") so a
/// missing config on ONE platform never affects the other (§4a, §6 "per-platform no-op").
/// </summary>
public sealed class NullAlertPushSender : IAlertPushSender
{
    public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
        => Task.FromResult(AlertSendResult.TransientFailure);
}
