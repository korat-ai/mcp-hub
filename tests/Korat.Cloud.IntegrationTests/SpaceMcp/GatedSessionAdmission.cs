using System.Collections.Concurrent;
using Korat.Cloud.Gateways.Admission;
using Korat.Domain;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// MUST-FIX F1 (adversarial review, second pass, BLOCKER): a test-only <see cref="ISessionAdmission"/>
/// decorator that reproduces the real-world race window described in the fix — "a granted backend
/// is still inside <c>admission.AdmitAsync</c> (node-wake can take seconds)" when
/// <see cref="SpaceMcpAggregatorGrain.TerminateAsync"/> snapshots its
/// <c>_backendsBySessionId</c> dictionary.
///
/// Runs the REAL <see cref="ISessionAdmission"/> to completion first — so the underlying relay
/// session (DB row, routing-table entry) is genuinely opened exactly as production would open it —
/// then, if a gate was armed for that <c>serverId</c>, blocks RETURNING the already-decided result
/// to the caller until the test calls <see cref="Release"/>. This is a faithful proxy for
/// production's node-wake delay: from the aggregator grain's point of view, it is indistinguishable
/// from <c>AdmitAsync</c> genuinely taking longer to decide.
///
/// Passthrough (zero added latency, zero behavior change) for every <c>serverId</c> that was never
/// armed via <see cref="Arm"/> — safe to install for the whole shared test silo
/// (<c>KoratTestHost.SiloConfigurator</c>), since <c>ISessionAdmission</c> in that container is
/// consumed ONLY by <c>SpaceMcpAggregatorGrain</c> (confirmed: <c>NodeGatewayService</c>, the other
/// consumer, resolves its OWN <c>ISessionAdmission</c> from the separate WEB HOST container, which
/// this decorator is never registered into).
/// </summary>
internal sealed class GatedSessionAdmission(ISessionAdmission inner) : ISessionAdmission
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> ObservedSessionIds = new();

    /// <summary>
    /// Arms the gate for <paramref name="serverId"/> — the NEXT <see cref="AdmitAsync"/> call for
    /// this server will run the real admission to completion, then block returning the result
    /// until <see cref="Release"/> is called. Call BEFORE triggering the admission (e.g. before
    /// <c>grain.InitializeAsync</c>).
    /// </summary>
    public static void Arm(string serverId)
    {
        Gates[serverId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObservedSessionIds[serverId] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Releases a gate armed via <see cref="Arm"/>, letting the held <see cref="AdmitAsync"/>
    /// call finally return to its caller (<c>SpaceMcpAggregatorGrain.OpenBackendAsync</c>). Removes
    /// the entry (not <see cref="AdmitAsync"/> — it only READS the gate via <c>TryGetValue</c>, never
    /// removes it, so a Release racing ahead of AdmitAsync's own lookup can never orphan the
    /// TaskCompletionSource the awaiting call is holding a reference to).</summary>
    public static void Release(string serverId)
    {
        if (Gates.TryRemove(serverId, out var tcs))
            tcs.TrySetResult();
    }

    /// <summary>
    /// Waits (bounded) for the gated <see cref="AdmitAsync"/> call to have observed a REAL minted
    /// relay <c>SessionId</c> for <paramref name="serverId"/> — proves the underlying session
    /// (DB row + routing-table entry) already exists, exactly as production's node-wake delay
    /// would leave it, before the test proceeds to race <c>TerminateAsync</c> against the still-held
    /// gate.
    /// </summary>
    public static async Task<string> WaitForObservedSessionIdAsync(string serverId, TimeSpan timeout)
    {
        var tcs = ObservedSessionIds.GetOrAdd(serverId,
            _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        using var cts = new CancellationTokenSource(timeout);
        await using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task<AdmissionResult> AdmitAsync(McpServerId serverId, ConsumerPrincipal principal, CancellationToken cancellationToken)
    {
        var result = await inner.AdmitAsync(serverId, principal, cancellationToken);

        if (result is AdmissionResult.Opened opened
            && ObservedSessionIds.TryGetValue(serverId.Value, out var observedTcs))
        {
            observedTcs.TrySetResult(opened.SessionId.Value);
        }

        // TryGetValue (NOT TryRemove) — Release(...) owns removing the entry. If AdmitAsync
        // removed it here instead, a Release(...) call that (as in the intended test usage) runs
        // AFTER admission has already completed would find nothing to complete, and this await
        // would hang forever (the real bug this comment replaces).
        if (Gates.TryGetValue(serverId.Value, out var gate))
            await gate.Task; // held until the test calls Release(...).

        return result;
    }
}
