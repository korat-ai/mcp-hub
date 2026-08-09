using System.Collections.Concurrent;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace Korat.Cloud.Push;

/// <summary>
/// Thin abstraction so <see cref="NodeWakeCoordinator"/> can be unit-tested without
/// stubbing the full <see cref="IClusterClient"/> / <see cref="IGrainFactory"/> interfaces.
/// Production registration provides <see cref="ClusterNodeGrainLocator"/>.
/// </summary>
public interface INodeGrainLocator
{
    INodeGrain GetNodeGrain(string nodeId);
}

/// <summary>
/// Production adapter: resolves <see cref="INodeGrain"/> via the Orleans cluster client.
/// </summary>
public sealed class ClusterNodeGrainLocator(IClusterClient cluster) : INodeGrainLocator
{
    public INodeGrain GetNodeGrain(string nodeId) =>
        cluster.GetGrain<INodeGrain>(nodeId);
}

/// <summary>
/// Singleton that attempts to wake a backgrounded iOS node via a silent APNs push when an
/// agent requests a session while the node is offline.
///
/// Behaviour (030 push-to-wake §6; B1 guard added in 031 mobile-push increment 2):
/// 1. Eligibility — node.PushToken non-empty AND sender configured (KeyId present) AND
///    PushPlatform ∈ {apns, apns_sandbox}; returns false immediately for non-wakeable nodes
///    (CLI/Android(fcm)/old iOS — zero added latency). The platform check exists because APNs
///    can never deliver to an `fcm` token — POSTing one would just 400 and burn it for nothing.
/// 2. Dedup — per-silo <see cref="ConcurrentDictionary{TKey,TValue}"/> of last-push times;
///    skips the APNs send (but still waits) if a push went out within
///    <see cref="ApnsOptions.WakeDedupSeconds"/>. Worst case on multi-silo is N-silo pushes,
///    still within APNs budget.
/// 3. Send — best-effort; <see cref="PushWakeResult.TokenInvalid"/> triggers a fire-and-forget
///    <c>RegisterPushTokenAsync("", "")</c> to clear the stale token from the node grain.
/// 4. Wait — polls <c>NodeGrain.GetAsync().Status</c> every 1 s up to
///    <see cref="ApnsOptions.WakeWaitSeconds"/> (default 12 s). NodeGrain.Status is
///    cluster-global (correct on multi-silo Fly). 12 s leaves ≥8 s for the
///    SessionOpened/AccessDenied write + round-trip before the agent's 20 s handshake timeout.
///
/// Startup clamp: <see cref="ApnsOptions.WakeWaitSeconds"/> ≥ 15 is logged as a warning and
/// clamped to 15 (20 s agent handshake − 5 s safety margin). Server-minted cloud aggregation
/// may explicitly request a longer wait (capped at 30 s) because its open budget is 40 s.
/// </summary>
public sealed class NodeWakeCoordinator
{
    // Clamp ceiling: 20 s agent HandshakeTimeout − 5 s safety margin.
    private const int WakeWaitClampSeconds = 15;
    private const int ExtendedWakeWaitClampSeconds = 30;

    private readonly IPushWakeSender _sender;
    private readonly INodeGrainLocator _locator;
    private readonly ApnsOptions _opts;
    private readonly ILogger<NodeWakeCoordinator> _log;

    // Per-silo dedup: NodeId.Value → last-push time.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPushAt = new();

    /// <summary>
    /// True when APNs is configured (KeyId present). False → <see cref="NullPushWakeSender"/>
    /// is active and <see cref="TryWakeAsync"/> always returns false immediately.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_opts.KeyId);

    /// <summary>
    /// Production constructor: resolves the grain locator from <see cref="ClusterNodeGrainLocator"/>.
    /// </summary>
    public NodeWakeCoordinator(
        IPushWakeSender sender,
        INodeGrainLocator locator,
        IOptions<ApnsOptions> opts,
        ILogger<NodeWakeCoordinator> log)
    {
        _sender = sender;
        _locator = locator;
        _log = log;

        var options = opts.Value;

        // Startup clamp: WakeWaitSeconds >= 15 → warn + clamp.
        if (options.WakeWaitSeconds >= WakeWaitClampSeconds)
        {
            log.LogWarning(
                "Korat:Apns:WakeWaitSeconds={Configured} is >= the clamp ceiling {Clamp} " +
                "(20 s agent handshake timeout − 5 s margin). Clamping to {Clamp} s " +
                "to ensure AccessDenied(node_waking) arrives before the agent times out.",
                options.WakeWaitSeconds, WakeWaitClampSeconds, WakeWaitClampSeconds);
            options = new ApnsOptions
            {
                KeyId = options.KeyId,
                TeamId = options.TeamId,
                BundleId = options.BundleId,
                PrivateKeyPem = options.PrivateKeyPem,
                WakeWaitSeconds = WakeWaitClampSeconds,
                WakeDedupSeconds = options.WakeDedupSeconds,
            };
        }

        _opts = options;
    }

    /// <summary>
    /// Called from <c>HandleRequestSessionAsync</c> when the publisher node is Offline.
    /// Returns <c>true</c> if the node came Online within the wake window.
    /// Returns <c>false</c> immediately (zero added latency) when the node is not wake-capable
    /// (no PushToken, or sender not configured).
    /// </summary>
    public async Task<bool> TryWakeAsync(Node node, CancellationToken ct, TimeSpan? waitOverride = null)
    {
        // Ordinary gRPC agents retain the configured/clamped wait. The cloud aggregator can use
        // a larger request budget so a single tools/call can wait for silent-push delivery and the
        // iOS background connection instead of forcing the caller to retry manually.
        var wakeWait = waitOverride is { } requested
            ? TimeSpan.FromSeconds(Math.Clamp(requested.TotalSeconds, 0, ExtendedWakeWaitClampSeconds))
            : TimeSpan.FromSeconds(_opts.WakeWaitSeconds);

        // (1) Eligibility check — no added latency for non-wakeable nodes.
        // Three conditions must ALL hold: the node has a push token, the sender is configured
        // (KeyId present ⇒ ApnsPushWakeSender; absent ⇒ NullPushWakeSender → NotConfigured), AND
        // (031 B1 guard) the token's platform is one APNs can actually deliver to. Without this
        // guard an `fcm`-platform token would be POSTed straight to Apple, which 400s it —
        // wasting the token for a wake that could never succeed via APNs. This guard MUST be in
        // place before Android FCM token registration (design doc Plan 4) is exercised against
        // any shared environment.
        var isApnsPlatform = node.PushPlatform == "apns" || node.PushPlatform == "apns_sandbox";
        if (string.IsNullOrEmpty(node.PushToken) || string.IsNullOrEmpty(_opts.KeyId) || !isApnsPlatform)
        {
            _log.LogDebug(
                "TryWakeAsync: node {NodeId} not wake-eligible (PushToken={HasToken}, SenderConfigured={Configured}, PushPlatform={Platform}).",
                node.Id.Value,
                !string.IsNullOrEmpty(node.PushToken),
                !string.IsNullOrEmpty(_opts.KeyId),
                node.PushPlatform ?? "(none)");
            return false;
        }

        // (2) Dedup — skip send if a push went out within WakeDedupSeconds.
        var now = DateTimeOffset.UtcNow;
        var dedupWindow = TimeSpan.FromSeconds(_opts.WakeDedupSeconds);
        var shouldSend = true;

        // FIX (dedup-timestamp): only READ here to decide whether to send; timestamp is
        // updated AFTER a successful send (Sent or TokenInvalid) so a failed first send
        // doesn't block retries for the full dedup window.
        _lastPushAt.TryGetValue(node.Id.Value, out var lastPushAt);
        if (lastPushAt != default && now - lastPushAt < dedupWindow)
        {
            shouldSend = false;  // within dedup window — skip send, still wait
        }

        // Opportunistic prune: remove entries older than 2× the dedup window to bound memory.
        var pruneThreshold = now - TimeSpan.FromSeconds(_opts.WakeDedupSeconds * 2);
        foreach (var kvp in _lastPushAt)
        {
            if (kvp.Value < pruneThreshold)
                _lastPushAt.TryRemove(kvp.Key, out _);
        }

        if (shouldSend)
        {
            // (3) Send best-effort push.
            var result = await _sender.SendWakeAsync(node.PushToken, node.PushPlatform ?? "apns", ct);

            switch (result)
            {
                case PushWakeResult.Sent:
                    // FIX (dedup-timestamp): record timestamp AFTER a successful send so a
                    // transient failure on the first call doesn't block retries for the dedup window.
                    _lastPushAt[node.Id.Value] = now;
                    _log.LogInformation(
                        "APNs silent push sent to node {NodeId} (token prefix {TokenPrefix}...).",
                        node.Id.Value,
                        node.PushToken.Length >= 8 ? node.PushToken[..8] : node.PushToken);
                    break;

                case PushWakeResult.TokenInvalid:
                    // Record timestamp so we don't retry the invalid token repeatedly.
                    _lastPushAt[node.Id.Value] = now;
                    // Fire-and-forget clear — we still wait in case an older push already worked.
                    // 031: compare-and-clear (ClearPushTokenIfMatchesAsync) instead of the old
                    // unconditional RegisterPushTokenAsync("", "") — closes a race where the app
                    // re-registers a FRESH token between this failed send and the clear landing.
                    var nodeId = node.Id;
                    var deadToken = node.PushToken;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _locator.GetNodeGrain(nodeId.Value)
                                .ClearPushTokenIfMatchesAsync(deadToken);
                            _log.LogInformation(
                                "Cleared stale APNs push token for node {NodeId} (410/BadDeviceToken).",
                                nodeId.Value);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex,
                                "Failed to clear stale APNs push token for node {NodeId}.",
                                nodeId.Value);
                        }
                    }, CancellationToken.None);

                    // Fall through to wait — node may already be waking from an earlier push.
                    break;

                case PushWakeResult.Failed:
                    // Do NOT record timestamp — a transient failure should allow an immediate retry.
                    _log.LogWarning(
                        "APNs push failed (transient) for node {NodeId} — still waiting.",
                        node.Id.Value);
                    break;

                case PushWakeResult.NotConfigured:
                    // Shouldn't happen here (checked above), but be safe.
                    return false;
            }
        }
        else
        {
            _log.LogDebug(
                "APNs push deduped for node {NodeId} — within {DedupSeconds} s window; still waiting.",
                node.Id.Value, _opts.WakeDedupSeconds);
        }

        // (4) Poll NodeGrain.Status every 1 s up to the selected wake window.
        var deadline = now.Add(wakeWait);
        var nodeGrain = _locator.GetNodeGrain(node.Id.Value);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // FIX: wrap Task.Delay inside the try so OperationCanceledException from
                // cancellation propagates cleanly and exits the loop without a throw.
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                var current = await nodeGrain.GetAsync();
                if (current.Status == NodeStatus.Online)
                {
                    _log.LogInformation(
                        "Node {NodeId} came Online within wake window ({WakeWaitSeconds} s).",
                        node.Id.Value, wakeWait.TotalSeconds);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Grain call failure is non-fatal — continue polling.
                _log.LogDebug(ex,
                    "Transient error polling NodeGrain.Status for node {NodeId}.",
                    node.Id.Value);
            }
        }

        _log.LogInformation(
            "Node {NodeId} did not come Online within {WakeWaitSeconds} s wake window.",
            node.Id.Value, wakeWait.TotalSeconds);
        return false;
    }
}
