using System.Collections.Immutable;

namespace Korat.Cloud.Mcp.Space;

/// <summary>
/// Space-MCP inc-2a, Task 7 (SF-6): the per-consumer index of LIVE aggregator sessions —
/// keyed by the durable <c>ConsumerId.Value</c> (cagg_…), listing the Mcp-Session-Id grain
/// keys currently open for it. Consent revocation (OAuthConsentEndpoints, Task 8) fans out
/// TerminateAsync over this list via <see cref="TerminateAllAsync"/>. Deliberately VOLATILE (no
/// persistence): the aggregator activations it indexes are volatile too — a silo restart kills
/// sessions and index together, so the index can never dangle past its sessions. Registration is
/// fail-CLOSED on the initialize path (SF-1: registered BEFORE the aggregator flips
/// <c>_initialized = true</c> — a registration failure aborts init before the session is
/// reachable, rather than leaving a live-but-unindexed session invisible to revocation);
/// unregistration is best-effort (teardown must never be blocked).
/// </summary>
public interface ISpaceMcpConsumerSessionsGrain : IGrainWithStringKey
{
    Task RegisterAsync(string mcpSessionId);
    Task UnregisterAsync(string mcpSessionId);
    Task<ImmutableArray<string>> ListAsync();

    /// <summary>Snapshots the currently-registered session set, then fans <c>TerminateAsync</c>
    /// out to each — Task 8's consent-revoke entry point (SF-6). Snapshots via <c>.ToList()</c>
    /// BEFORE the loop — REQUIRED, not defensive: this grain is <c>[Reentrant]</c> (each
    /// aggregator's own <see cref="UnregisterAsync"/> callback, fired from inside its
    /// <c>TerminateAsync</c>, must be able to interleave and complete WHILE this method is still
    /// suspended awaiting that very call — a non-reentrant grain would deadlock on that
    /// call-back). Each snapshotted id is removed from the live set in a <c>finally</c> as its
    /// own <c>TerminateAsync</c> call settles (success or failure) — only that one id, never a
    /// blanket clear, so a session that registers concurrently mid-fan-out survives. A session
    /// whose aggregator grain has already deactivated (or was never truly initialized) is a
    /// harmless no-op — Orleans reactivates it fresh and <c>TerminateAsync</c> on an
    /// uninitialized activation is a no-op by construction (nothing to tear down).</summary>
    Task TerminateAllAsync();
}
