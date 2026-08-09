using System.Collections.Immutable;
using Orleans.Concurrency;

namespace Korat.Cloud.Mcp.Space;

/// <inheritdoc cref="ISpaceMcpConsumerSessionsGrain"/>
///
/// <c>[Reentrant]</c> — REQUIRED, not cosmetic: <see cref="TerminateAllAsync"/> awaits each
/// session's <see cref="ISpaceMcpAggregatorGrain.TerminateAsync"/> in turn, and that same
/// aggregator's teardown calls straight back into THIS grain (<see cref="UnregisterAsync"/>,
/// the Task 7 aggregator hook) BEFORE its own <c>TerminateAsync</c> call returns. On a
/// non-reentrant grain that callback would queue behind <see cref="TerminateAllAsync"/>'s own
/// still-in-flight turn — a same-activation call-back deadlock (verified empirically: the
/// unmarked version hung every <see cref="TerminateAllAsync"/> call until the 30s Orleans
/// response timeout). Marking this grain reentrant lets the queued <c>UnregisterAsync</c>
/// callback interleave and complete while <see cref="TerminateAllAsync"/> is suspended awaiting
/// that very session's <c>TerminateAsync</c>, unblocking it. <see cref="RegisterAsync"/>,
/// <see cref="UnregisterAsync"/>, and <see cref="ListAsync"/> have no internal <c>await</c>, so
/// they always run their single HashSet operation to completion atomically regardless of
/// reentrancy — nothing here needs mutual exclusion, only <see cref="TerminateAllAsync"/>'s own
/// snapshot-vs-mutate care (see its own doc comment).
[Reentrant]
public sealed class SpaceMcpConsumerSessionsGrain(ILogger<SpaceMcpConsumerSessionsGrain> logger)
    : Grain, ISpaceMcpConsumerSessionsGrain
{
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);

    public Task RegisterAsync(string mcpSessionId)
    {
        _sessions.Add(mcpSessionId); // idempotent — an initialize retry re-registers harmlessly
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string mcpSessionId)
    {
        _sessions.Remove(mcpSessionId);
        if (_sessions.Count == 0)
            DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<ImmutableArray<string>> ListAsync() => Task.FromResult(_sessions.ToImmutableArray());

    /// <summary>Task 7 (SF-6): snapshot-then-fan-out over the CURRENT session set — Task 8's
    /// consent-revoke entry point. <c>.ToList()</c> before the loop is REQUIRED here (not just
    /// defensive): this grain is <c>[Reentrant]</c> (see the class doc comment for why), so a
    /// queued <see cref="UnregisterAsync"/> callback CAN genuinely interleave and mutate
    /// <see cref="_sessions"/> while this loop is suspended mid-await — enumerating the live
    /// <see cref="_sessions"/> directly would risk "Collection was modified"; enumerating a
    /// separate materialized <c>List&lt;string&gt;</c> snapshot cannot.
    ///
    /// Each per-session <c>TerminateAsync</c> call is independently try/caught so one session's
    /// failure never aborts the fan-out for the rest, and the snapshotted id is removed from
    /// <see cref="_sessions"/> in a <c>finally</c> regardless of outcome — deliberately removing
    /// ONLY that one id (never <c>Clear()</c> the whole set): a genuinely NEW session that
    /// registers concurrently mid-fan-out (interleaved, same reentrancy) must survive this call
    /// intact, not be wiped by a blanket clear. This finally-remove is also what makes a
    /// "phantom" entry harmless: a session id that was registered but never actually initialized
    /// (or whose activation already deactivated) never sends its OWN <c>UnregisterAsync</c>
    /// callback (the aggregator only calls it from inside its <c>if (_initialized)</c> teardown
    /// block) — without this explicit removal such an entry would linger in the registry forever.
    /// <c>TerminateAsync</c> itself is idempotent (guarded by the aggregator's own
    /// <c>_tornDown</c> flag) — a session already terminated (e.g. by its own DELETE, racing this
    /// same revoke) is a safe no-op, not a double-terminate error. A session id whose aggregator
    /// grain has long since deactivated is likewise harmless: Orleans reactivates it fresh on
    /// this call, finds <c>_initialized == false</c>, and <c>TerminateAsync</c> no-ops on an
    /// un-initialized activation (nothing to tear down) — "already gone" needs no special
    /// casing.</summary>
    public async Task TerminateAllAsync()
    {
        var snapshot = _sessions.ToList();
        foreach (var mcpSessionId in snapshot)
        {
            try
            {
                await GrainFactory.GetGrain<ISpaceMcpAggregatorGrain>(mcpSessionId).TerminateAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Space-MCP: TerminateAllAsync failed to terminate session mcpSessionId={McpSessionId}",
                    mcpSessionId);
            }
            finally
            {
                _sessions.Remove(mcpSessionId);
            }
        }

        if (_sessions.Count == 0)
            DeactivateOnIdle();
    }
}
