using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Korat.Cloud.Push;

/// <summary>
/// APNs alert-push implementation of <see cref="IAlertPushSender"/> (031, mobile-push increment
/// 2, design §4a). Sends a visible, tap-to-open alert via the shared <see cref="ApnsTransport"/>:
/// `apns-push-type: alert`, priority 10, an explicit 1-hour expiration, and `apns-collapse-id` set
/// to the accessRequestId (dedup — design §MED-3). The body is
/// `{"aps":{"alert":{"title","body"},"sound":"default","category":"korat.access-request"}, ...Data}`.
/// The `category` matches what the iOS plan registers as a `UNNotificationCategory` (post-review
/// correction — routing itself keys off `userInfo["type"]`, not the category, but omitting it
/// would leave the registered category dead weight and make the manual test payload diverge from
/// what production actually sends).
/// </summary>
public sealed class ApnsAlertSender(ApnsTransport transport, ILogger<ApnsAlertSender> log) : IAlertPushSender
{
    public async Task<AlertSendResult> SendAlertAsync(string token, string platform, AlertContent content, CancellationToken ct)
    {
        try
        {
            var payload = BuildPayload(content);
            var expiration = DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                ["apns-push-type"] = "alert",
                ["apns-priority"] = "10",
                ["apns-expiration"] = expiration.ToString(),
                ["apns-collapse-id"] = content.Data.TryGetValue("accessRequestId", out var reqId) && !string.IsNullOrEmpty(reqId)
                    ? reqId
                    : Guid.NewGuid().ToString("N"),
            };

            var (status, body) = await transport.SendAsync(token, platform, headers, payload, ct);
            return MapResult(status, body, token, platform);
        }
        catch (OperationCanceledException)
        {
            return AlertSendResult.TransientFailure;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "APNs alert send failed for token {TokenPrefix}... ({Platform}).",
                TokenPrefix(token), platform);
            return AlertSendResult.TransientFailure;
        }
    }

    private AlertSendResult MapResult(int status, string? body, string token, string platform)
    {
        var tokenPrefix = TokenPrefix(token);

        if (status == 200)
        {
            log.LogDebug("APNs alert sent to token {TokenPrefix}... ({Platform})", tokenPrefix, platform);
            return AlertSendResult.Delivered;
        }

        if (status == 410 || status == 400)
        {
            if (status == 400 && (body is null || !body.Contains("BadDeviceToken", StringComparison.OrdinalIgnoreCase)))
            {
                log.LogWarning(
                    "APNs rejected alert to token {TokenPrefix}... ({Platform}) with 400: {Body}",
                    tokenPrefix, platform, body);
                return AlertSendResult.TransientFailure;
            }

            log.LogInformation(
                "APNs token invalid for token {TokenPrefix}... ({Platform}), HTTP {Status}.",
                tokenPrefix, platform, status);
            return AlertSendResult.TokenInvalid;
        }

        log.LogWarning(
            "APNs alert transient error for token {TokenPrefix}... ({Platform}), HTTP {Status}: {Body}",
            tokenPrefix, platform, status, body);
        return AlertSendResult.TransientFailure;
    }

    private static string TokenPrefix(string token) => token.Length >= 8 ? token[..8] : token;

    private static byte[] BuildPayload(AlertContent content)
    {
        var aps = new Dictionary<string, object?>
        {
            ["alert"] = new Dictionary<string, string> { ["title"] = content.Title, ["body"] = content.Body },
            ["sound"] = "default",
            ["category"] = "korat.access-request",
        };
        var payload = new Dictionary<string, object?> { ["aps"] = aps };
        foreach (var (key, value) in content.Data)
            payload[key] = value;
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }
}
