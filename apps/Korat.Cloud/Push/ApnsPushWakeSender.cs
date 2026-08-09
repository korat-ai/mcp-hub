using Microsoft.Extensions.Logging;

namespace Korat.Cloud.Push;

/// <summary>
/// Sends APNs silent pushes (content-available:1) via the shared <see cref="ApnsTransport"/>.
/// 031 (mobile-push increment 2): the ES256/JWT/HTTP plumbing moved into ApnsTransport so the new
/// alert sender can share the same JWT cache; THIS class now owns only the wake-specific header
/// set + body + status-to-result mapping — byte-identical to the pre-refactor behavior (priority
/// 5, apns-expiration 1800s, apns-push-type background, same apns-topic via the transport).
///
/// Response handling (unchanged):
///   200                                    → <see cref="PushWakeResult.Sent"/>
///   410 Unregistered / 400 BadDeviceToken   → <see cref="PushWakeResult.TokenInvalid"/>
///   429 / 5xx / other 400 / residual 403    → <see cref="PushWakeResult.Failed"/> (no auto-retry;
///     the 403-provider-token-refresh-once retry, if applicable, already happened inside the
///     transport and is transparent here)
/// </summary>
public sealed class ApnsPushWakeSender(ApnsTransport transport, ILogger<ApnsPushWakeSender> log) : IPushWakeSender
{
    // Body sent for every silent wake push.
    private static readonly byte[] WakeBody = """{"aps":{"content-available":1}}"""u8.ToArray();

    /// <inheritdoc/>
    public async Task<PushWakeResult> SendWakeAsync(string token, string platform, CancellationToken ct)
    {
        try
        {
            // Expiration must SURVIVE APNs server-side deferral. Background (priority-5) pushes
            // are budgeted per app per device (~2-3/hour guaranteed); beyond that APNs HOLDS the
            // push and delivers opportunistically. Verified on-device 2026-06-12: APNs returned
            // 200 but apsd never received the push — a 60s expiration killed every held push
            // before delivery. 30 min keeps a deferred wake viable. De-dup of rapid-fire wakes is
            // handled by NodeWakeCoordinator (WakeDedupSeconds), not TTL.
            var expiration = DateTimeOffset.UtcNow.AddSeconds(1800).ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                ["apns-push-type"] = "background",
                ["apns-priority"] = "5",
                ["apns-expiration"] = expiration.ToString(),
            };

            var (status, body) = await transport.SendAsync(token, platform, headers, WakeBody, ct);
            return MapResult(status, body, token, platform);
        }
        catch (OperationCanceledException)
        {
            return PushWakeResult.Failed;
        }
        catch (Exception ex)
        {
            var tokenPrefix = token.Length >= 8 ? token[..8] : token;
            log.LogWarning(ex,
                "APNs send failed for token {TokenPrefix}... ({Platform}).",
                tokenPrefix, platform);
            return PushWakeResult.Failed;
        }
    }

    private PushWakeResult MapResult(int status, string? body, string token, string platform)
    {
        var tokenPrefix = token.Length >= 8 ? token[..8] : token;

        if (status == 200)
            return PushWakeResult.Sent;

        if (status == 410 || status == 400)
        {
            if (status == 400 && (body is null || !body.Contains("BadDeviceToken", StringComparison.OrdinalIgnoreCase)))
            {
                log.LogWarning(
                    "APNs rejected push to token {TokenPrefix}... ({Platform}) with 400: {Body}",
                    tokenPrefix, platform, body);
                return PushWakeResult.Failed;
            }

            log.LogInformation(
                "APNs token invalid for token {TokenPrefix}... ({Platform}), HTTP {Status} — clearing.",
                tokenPrefix, platform, status);
            return PushWakeResult.TokenInvalid;
        }

        if (status == 429 || status >= 500)
        {
            log.LogWarning(
                "APNs transient error for token {TokenPrefix}... ({Platform}), HTTP {Status}. No retry.",
                tokenPrefix, platform, status);
            return PushWakeResult.Failed;
        }

        log.LogWarning(
            "APNs unexpected response for token {TokenPrefix}... ({Platform}), HTTP {Status}.",
            tokenPrefix, platform, status);
        return PushWakeResult.Failed;
    }
}
