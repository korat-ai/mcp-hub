using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;

namespace Korat.Cloud.Maintenance;

/// <summary>
/// 024: background reaper that hard-deletes <c>Published</c> MCP servers whose owner node has been
/// offline longer than the purge horizon (catalog hygiene — soft-retired-and-never-DELETEd rows,
/// from a decommissioned node, would otherwise accumulate forever as Unavailable entries).
///
/// Design (see specs/024-orphan-server-reaper): a BackgroundService (Orleans reminders are not
/// configured in this silo), 6h cadence, first sweep on the first tick (not at T=0 — avoids racing
/// startup migration/hydrate). Keys off the DB-persisted <c>Node.LastSeenAt</c>, which heartbeats
/// refresh every ~25s, so a live node's servers are never reaped. Deletes via
/// <see cref="ISpaceGrain.DeleteMcpServerAsync"/> (the same hard-delete path as the owner DELETE
/// endpoint) so grain caches stay consistent; that call is idempotent, so running on multiple
/// silos concurrently is harmless (no leader election needed at this scale). Best-effort: a sweep
/// failure is logged and swallowed; the next tick retries.
/// </summary>
public sealed class McpServerReaperService(
    IMetadataRepository repository,
    IClusterClient clusterClient,
    IConfiguration configuration,
    ILogger<McpServerReaperService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// 022/Step-A: sentinel UserId for reaper-initiated grant revocations. The reaper has no
    /// real user context; a well-known zero-based Guid records "system reaper" in the audit
    /// trail on RevokedByUserId. The human-facing revoke endpoint threads the real owner UserId.
    /// </summary>
    private static readonly UserId ReaperUserId = new(Guid.Parse("00000000-0000-0000-0000-fffffffffffe"));

    private TimeSpan PurgeThreshold
    {
        get
        {
            var hours = configuration.GetValue<double?>("Korat:Cloud:ServerPurgeThresholdHours");
            return hours is > 0 ? TimeSpan.FromHours(hours.Value) : McpServerReaperRules.PurgeThreshold;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First sweep on the first tick (not immediately) so the silo is fully up.
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Best-effort — a sweep failure must never crash the silo; retry next tick.
                // Log the full exception (this feature is audit-grade; a recurring DB issue must be diagnosable).
                logger.LogError(ex, "MCP server reaper sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - PurgeThreshold;
        var candidates = await repository.ListPurgeableMcpServersAsync(cutoff, ct);
        if (candidates.Count == 0)
            return;

        // No cap on candidate count: deliberate. At current scale a sweep handles a handful; a
        // mass decommission would issue many sequential (idempotent) grain calls within the 6h tick
        // — slow but safe (cancellation honored per-iteration). Add Take(N) if fleet scale grows.
        var reaped = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // 022/Step-A: pass ReaperUserId so the grain can attribute grant revocations to the
            // system reaper. The returned AffectedSessionIds are intentionally ignored here —
            // the reaper's best-effort semantics allow sessions to drain; future work (Step C)
            // can wire termination on the reaper path if needed.
            // Step-B: reaper deletes must NOT tombstone — a returning node may re-publish.
            var result = await clusterClient.GetGrain<ISpaceGrain>(c.SpaceId.Value).DeleteMcpServerAsync(c.Id, ReaperUserId, writeTombstone: false);
            if (result.Deleted)
            {
                reaped++;
                logger.LogInformation(
                    "Reaped orphan MCP server serverId={ServerId} spaceId={SpaceId} ownerNodeId={NodeId} ownerLastSeenAt={LastSeenAt}",
                    c.Id.Value, c.SpaceId.Value, c.PublisherNodeId.Value, c.OwnerLastSeenAt);
            }
        }

        if (reaped > 0)
            logger.LogInformation("MCP server reaper swept {Candidates} candidate(s), reaped {Reaped}", candidates.Count, reaped);
    }
}
