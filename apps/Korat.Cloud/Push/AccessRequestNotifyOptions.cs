namespace Korat.Cloud.Push;

/// <summary>
/// Configuration for <see cref="AccessRequestNotifier"/>'s per-space storm-control throttle
/// (design §MED-3): a gRPC gateway with no rate limiter + agent-supplied ConsumerId means one
/// node can mint unlimited identities → unlimited new (agent,server) pairs → unlimited pushes.
/// This throttle is IN ADDITION to the per-request apns-collapse-id / FCM collapse_key coalescing.
///
/// Config key: Korat:Notify:ThrottleSeconds — minimum interval between notify fan-outs for the
/// SAME Space. Default: 10 (post-review correction — matches ApnsOptions.WakeDedupSeconds'
/// default so distinct legitimate requests seconds apart still notify; the throttle is a storm
/// safety-valve, not a per-request debounce — see AccessRequestNotifier for the residual-risk note).
/// </summary>
public sealed class AccessRequestNotifyOptions
{
    public const string SectionName = "Korat:Notify";

    public int ThrottleSeconds { get; set; } = 10;
}
