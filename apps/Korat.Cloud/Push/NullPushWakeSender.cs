namespace Korat.Cloud.Push;

/// <summary>
/// Registered when <c>Korat:Apns:KeyId</c> is absent. Returns <see cref="PushWakeResult.NotConfigured"/>
/// so the wake path degrades gracefully to today's immediate ServerUnavailable response.
/// </summary>
public sealed class NullPushWakeSender : IPushWakeSender
{
    public Task<PushWakeResult> SendWakeAsync(string token, string platform, CancellationToken ct)
        => Task.FromResult(PushWakeResult.NotConfigured);
}
