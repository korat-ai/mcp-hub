using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;

namespace Korat.Cloud.Maintenance;

/// <summary>
/// Step-C: background reaper that PERSISTS a terminal Closed state for long-stale Active/Opening
/// sessions (hygiene/consistency, NOT security — Steps A/B already cut access + tore down live
/// streams; the streams here are already dead). "Stale/ghost" is otherwise only DERIVED at read
/// time (Endpoints.cs:447-460) and never written back, so dead sessions accumulate as Active in the
/// DB forever. Keys off DB-persisted Node.LastSeenAt (NodeGrain heartbeats upsert it ~every 25s),
/// so a live node's sessions are never reaped. Closes via ISessionGrain.CloseAsync (single
/// cluster-wide activation → multi-silo idempotent; no leader election). Best-effort: a sweep
/// failure is logged + swallowed; the next tick retries.
///
/// Multi-silo safety: this service runs on every silo with no leader election. That is SAFE because
/// CloseAsync is routed to the single cluster-wide grain activation and StateTransitions.CloseSession
/// is idempotent (re-closing a Closed session is a no-op). The next sweep's ListReapableSessionsAsync
/// query excludes already-Closed sessions, so subsequent sweeps are cheap and side-effect-free.
/// </summary>
public sealed class SessionReaperService(
    IMetadataRepository repository,
    IClusterClient clusterClient,
    IConfiguration configuration,
    ILogger<SessionReaperService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private TimeSpan ReapGrace
    {
        get
        {
            var minutes = configuration.GetValue<double?>("Korat:Cloud:SessionReapGraceMinutes");
            return minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : SessionReaperRules.ReapGrace;
        }
    }

    /// <summary>MUST-FIX F2 (adversarial review, second pass): config-with-default for the
    /// sentinel-session absolute-age backstop — mirrors <see cref="ReapGrace"/>'s own pattern
    /// exactly.</summary>
    private TimeSpan SpaceMcpSessionMaxAge
    {
        get
        {
            var hours = configuration.GetValue<double?>("Korat:Cloud:SpaceMcpSessionMaxAgeHours");
            return hours is > 0 ? TimeSpan.FromHours(hours.Value) : SessionReaperRules.DefaultSpaceMcpSessionMaxAge;
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
                logger.LogError(ex, "Session reaper sweep failed");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - ReapGrace;
        var sentinelSessionAgeCutoff = DateTimeOffset.UtcNow - SpaceMcpSessionMaxAge;
        var candidates = await repository.ListReapableSessionsAsync(cutoff, sentinelSessionAgeCutoff, ct);
        if (candidates.Count == 0)
            return;

        var reaped = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // CloseAsync is idempotent (StateTransitions.CloseSession unconditionally sets the
                // terminal values) and grain-mediated (single activation cluster-wide) → safe to run
                // concurrently on every silo. Abandoned = source-agnostic ghost reconciliation.
                await clusterClient.GetGrain<ISessionGrain>(c.Id.Value).CloseAsync(SessionCloseReason.Abandoned);
                reaped++;
                logger.LogInformation(
                    "Reaped stale session sessionId={SessionId} spaceId={SpaceId} clientNodeId={ClientNodeId} publisherNodeId={PublisherNodeId}",
                    c.Id.Value, c.SpaceId.Value, c.ClientNodeId.Value, c.PublisherNodeId.Value);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One grain failure must not abort the whole sweep; log + continue.
                logger.LogWarning(ex, "Failed to reap session sessionId={SessionId}", c.Id.Value);
            }
        }

        if (reaped > 0)
            logger.LogInformation("Session reaper swept {Candidates} candidate(s), reaped {Reaped}", candidates.Count, reaped);
    }
}
