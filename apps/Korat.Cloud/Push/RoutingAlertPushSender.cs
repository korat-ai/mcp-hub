namespace Korat.Cloud.Push;

/// <summary>
/// The single <see cref="IAlertPushSender"/> <see cref="AccessRequestNotifier"/> depends on.
/// Routes by platform to the APNs or FCM leg — each leg is independently either the real sender
/// or a <see cref="NullAlertPushSender"/>, wired in Program.cs based on which secrets are present
/// (§4a: "a missing FCM config must not affect APNs and vice-versa").
/// </summary>
public sealed class RoutingAlertPushSender(IAlertPushSender apnsSender, IAlertPushSender fcmSender) : IAlertPushSender
{
    public Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
        => platform switch
        {
            "apns" or "apns_sandbox" => apnsSender.SendAlertAsync(token, platform, content, ct),
            "fcm" => fcmSender.SendAlertAsync(token, platform, content, ct),
            // Unknown/legacy platform string — never crash the fan-out over one bad row.
            _ => Task.FromResult(AlertSendResult.TransientFailure),
        };
}
