using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace Korat.Cloud.Push;

/// <summary>
/// FCM (Android) alert sender — Task 8 (mobile-push increment 2, design §4a/§4d). Sends a
/// DATA-ONLY message (no `notification` block): a combined notification+data FCM message does not
/// invoke `onMessageReceived` when the Android app is backgrounded/killed — exactly the case this
/// feature exists for (design §HIGH-2) — so the client (Plan 4) builds its own notification from
/// `data`.
///
/// `Message.Token` sets the classic FCM registration token — the SDK marks it
/// `[Obsolete("Use Fid instead")]`, favoring the newer Firebase Installation ID addressing mode,
/// but our Android client (`RegisterPushToken{token, platform="fcm"}`) sends the classic
/// registration token from `FirebaseMessaging.getInstance().getToken()`, NOT a FID — `Token`
/// remains the objectively correct field for our data flow, so the obsolete warning is
/// deliberately suppressed here.
/// </summary>
public sealed class FcmAlertSender(IFcmMessagingClient client, ILogger<FcmAlertSender> log) : IAlertPushSender
{
    public async Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
    {
        try
        {
            var data = new Dictionary<string, string>(content.Data, StringComparer.Ordinal)
            {
                ["title"] = content.Title,
                ["body"] = content.Body,
            };
            content.Data.TryGetValue("accessRequestId", out var collapseKey);

#pragma warning disable CS0618 // Token is Obsolete("Use Fid instead") — see class doc: our client sends the classic FCM registration token, not a FID.
            var message = new Message
            {
                Token = token,
                Data = data,
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    CollapseKey = collapseKey,
                },
            };
#pragma warning restore CS0618

            await client.SendAsync(message, ct);
            return AlertSendResult.Delivered;
        }
        catch (FirebaseMessagingException ex) when (
            ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
            ex.ErrorCode == FirebaseAdmin.ErrorCode.NotFound)
        {
            log.LogInformation(
                "FCM token invalid for token {TokenPrefix}... — {Reason}.",
                TokenPrefix(token), ex.MessagingErrorCode?.ToString() ?? ex.ErrorCode.ToString());
            return AlertSendResult.TokenInvalid;
        }
        catch (OperationCanceledException)
        {
            return AlertSendResult.TransientFailure;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "FCM send failed for token {TokenPrefix}... ({Platform}).", TokenPrefix(token), platform);
            return AlertSendResult.TransientFailure;
        }
    }

    private static string TokenPrefix(string token) => token.Length >= 8 ? token[..8] : token;
}
