using Microsoft.Extensions.Logging;

namespace Korat.Cloud.Push;

/// <summary>
/// 031 (mobile-push increment 2), Task 7: wraps a detached notify call with a bounded internal
/// timeout and a catch-all log, so a hung or throwing notifier NEVER surfaces into the caller
/// (the agent's RequestSession round-trip) or propagates a fault into the fire-and-forget
/// <c>Task.Run</c> at the call site. Extracted as its own class so this contract is unit-testable
/// without a live Orleans cluster. Internal — glue only, not part of the public Push surface.
/// </summary>
internal static class DetachedNotifyRunner
{
    /// <param name="notify">The notify call. NEVER invoked with the request's own cancellation
    /// token by the caller (NodeGatewayService) — the agent typically disconnects immediately
    /// after receiving AccessPending, which would cancel that token before the notify's HTTP
    /// sends complete. Instead this method mints its OWN <see cref="CancellationTokenSource"/>
    /// scoped to <paramref name="timeout"/> and passes ITS token in, so a "timed-out" fan-out
    /// actually cancels the underlying work (e.g. the HTTP sends) instead of merely abandoning
    /// the await and letting it keep running in the background up to HttpClient's ~100s default.
    /// A SINGLE timer drives both the wait and the work cancellation — the wait is anchored to
    /// <c>cts.Token</c> (not a second, independent <paramref name="timeout"/> duration passed to
    /// <see cref="Task.WaitAsync(TimeSpan)"/>), so there is no race where the wait's own timer
    /// fires and this method returns (disposing <paramref name="cts"/> ⇒ its still-pending timer
    /// is discarded) before the CTS's timer has fired — a race that would leave the token NEVER
    /// cancelled and the detached work running unbounded.</param>
    public static async Task RunAsync(
        Func<CancellationToken, Task> notify, TimeSpan timeout, ILogger logger, string spaceId)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await notify(cts.Token).WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Access-request notify failed or timed out for space {SpaceId}.", spaceId);
        }
    }
}
