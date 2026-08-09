using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Maintenance;

/// <summary>
/// 032 (#57 Leg 3 C1): periodically anchors the audit chain head OUTSIDE the database.
///
/// Honest threat model: an attacker with DB write access can rewrite the whole hash chain.
/// The anchor is the answer — every 6 h (and on graceful shutdown) the head
/// <c>(Seq, hex(LastHash))</c> is:
///   1. written back into the chain as a chained <c>audit.anchor</c> event (required),
///   2. emitted to ILogger("Korat.Audit") → the Fly log stream (off-box copy),
///   3. emitted to the configured Sentry-compatible error-tracking service.
/// Verification cross-checks anchors against the table: rewriting the chain without also
/// controlling the error-tracking sink and log history becomes detectable.
///
/// Multi-silo: every silo anchors independently — duplicate anchors are harmless (each is
/// just another chained event) and increase external evidence density.
/// </summary>
public sealed class AuditAnchorService(
    IAuditLog auditLog,
    IDbContextFactory<KoratDbContext> dbFactory,
    ILoggerFactory loggerFactory,
    ILogger<AuditAnchorService> logger) : BackgroundService
{
    private static readonly TimeSpan AnchorInterval = TimeSpan.FromHours(6);

    private readonly ILogger _auditMirror = loggerFactory.CreateLogger("Korat.Audit");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AnchorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await AnchorOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Audit anchor tick failed; retrying next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown: emit one final anchor so the externally-recorded head is as
            // fresh as possible (best-effort; bounded by host shutdown timeout).
            try
            {
                await AnchorOnceAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final shutdown audit anchor failed (best-effort).");
            }
        }
    }

    /// <summary>One anchor cycle. Internal for direct invocation from integration tests.</summary>
    internal async Task AnchorOnceAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var head = await db.AuditChainHead.AsNoTracking().SingleOrDefaultAsync(h => h.Id == 1, ct);
        if (head is null)
            return; // nothing recorded yet — nothing to anchor

        var headHashHex = Convert.ToHexString(head.LastHash);

        // 1. Chain the anchor itself (required: an unanchorable chain is an incident).
        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.AuditAnchor,
            TargetType: "audit_chain",
            TargetId: "head",
            DetailsJson: AuditDetails.Json(new { anchoredSeq = head.LastSeq, anchoredHash = headHashHex })),
            required: true, ct);

        // 2 + 3. External, append-only-ish sinks. The exact format is grep-stable — the IR
        // runbook compares these lines against the table.
        _auditMirror.LogInformation(
            "audit-anchor seq={AnchoredSeq} hash={AnchoredHash}", head.LastSeq, headHashHex);

        // GlitchTip sink. The message is a CONSTANT ("audit.anchor") so every 6 h anchor collapses
        // into ONE issue that can be muted once — GlitchTip groups by message text and does NOT
        // honour a custom fingerprint, so a per-(seq,hash) message spawned a fresh issue (and a
        // fresh alert) every 6 h. The seq + hash move to structured event data, NOT the message,
        // so tamper-evidence is preserved (each event still carries its own seq/hash for the IR
        // runbook). The grep-stable Fly-log line above keeps the full "audit-anchor seq=… hash=…"
        // text for table cross-checks.
        var anchorEvent = new SentryEvent
        {
            Message = "audit.anchor",
            Level = SentryLevel.Info,
        };
        anchorEvent.SetFingerprint(new[] { "korat-audit-anchor" });
        anchorEvent.SetTag("kind", "audit-anchor");
        anchorEvent.SetExtra("seq", head.LastSeq);
        anchorEvent.SetExtra("hash", headHashHex);
        SentrySdk.CaptureEvent(anchorEvent);
    }
}
