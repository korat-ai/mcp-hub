using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Maintenance;

/// <summary>
/// 032 (#57 Leg 3 C1): audit-trail retention. Deletes audit events older than 400 days,
/// then writes a chained <c>audit.prune_checkpoint</c> event whose DetailsJson records
/// <c>{prunedThroughSeq, prunedThroughHash}</c> — <see cref="AuditVerifier"/> reseeds from
/// the checkpoint, so chain verification survives pruning.
///
/// Daily cadence, first sweep on the first tick (mirrors McpServerReaperService). Concurrent
/// runs across silos are safe: deletion is idempotent and the checkpoint event is just another
/// chained row (a duplicate checkpoint reseeds verification at the same point).
/// </summary>
public sealed class AuditPruneService(
    IAuditLog auditLog,
    IDbContextFactory<KoratDbContext> dbFactory,
    ILogger<AuditPruneService> logger) : BackgroundService
{
    /// <summary>Retention window (leg3 design item 2): 400 days.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PruneOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit prune sweep failed; retrying next tick.");
            }
        }
    }

    /// <summary>One prune cycle at the standard 400-day retention cutoff.</summary>
    internal Task<int> PruneOnceAsync(CancellationToken ct) =>
        PruneOnceAsync(DateTimeOffset.UtcNow - Retention, ct);

    /// <summary>One prune cycle. Internal cutoff overload for direct invocation from integration tests.</summary>
    internal async Task<int> PruneOnceAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Identify the prune horizon: the newest expired row. Everything at or below its Seq
        // is older (Seq order ≈ time order; both are append-ordered by the chain head lock).
        var horizon = await db.AuditEvents.AsNoTracking()
            .Where(e => e.OccurredAtUtc < cutoff)
            .OrderByDescending(e => e.Seq)
            .FirstOrDefaultAsync(ct);
        if (horizon is null)
            return 0;

        var prunedThroughSeq = horizon.Seq;
        var prunedThroughHash = Convert.ToHexString(horizon.RowHash);

        // CHECKPOINT FIRST (fail-closed ordering): if the checkpoint write fails, nothing has
        // been deleted and verification still passes from the previous seed. Deleting first
        // would leave an unverifiable gap on a crash between delete and checkpoint.
        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.AuditPruneCheckpoint,
            TargetType: "audit_chain",
            TargetId: "prune",
            // Property names are parsed back by AuditVerifier.ResolveSeedAsync — keep stable.
            DetailsJson: AuditDetails.Json(new { prunedThroughSeq, prunedThroughHash })),
            required: true, ct);

        int deleted;
        if (db.Database.IsInMemory())
        {
            // InMemory (tests): ExecuteDeleteAsync is unsupported.
            var rows = await db.AuditEvents.Where(e => e.Seq <= prunedThroughSeq).ToListAsync(ct);
            db.AuditEvents.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            deleted = rows.Count;
        }
        else
        {
            deleted = await db.AuditEvents
                .Where(e => e.Seq <= prunedThroughSeq)
                .ExecuteDeleteAsync(ct);
        }

        logger.LogInformation(
            "Audit prune: deleted {Deleted} event(s) through seq {PrunedThroughSeq} (cutoff {Cutoff:O}).",
            deleted, prunedThroughSeq, cutoff);
        return deleted;
    }
}
