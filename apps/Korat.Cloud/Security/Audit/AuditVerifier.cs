using System.Text.Json;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Security.Audit;

/// <summary>Result of a chain verification run.</summary>
public sealed record AuditVerifyResult(
    bool Ok,
    long CheckedCount,
    long? FirstBrokenSeq,
    bool HeadMismatch,
    long HeadSeq,
    string HeadHashHex);

/// <summary>
/// 032: recomputes the audit hash chain and compares it to the stored rows + chain head.
/// Verification starts from the latest prune checkpoint (chain seed recorded in its
/// DetailsJson), from an explicit <c>fromSeq</c>, or from genesis.
/// A broken row, a Seq gap, or a head mismatch is a high-confidence tamper signal
/// (IR runbook §1) — unless it correlates with a known operational incident.
/// </summary>
public sealed class AuditVerifier(IDbContextFactory<KoratDbContext> dbFactory)
{
    private const int PageSize = 1000;

    public async Task<AuditVerifyResult> VerifyAsync(long? fromSeq = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var head = await db.AuditChainHead.AsNoTracking().SingleOrDefaultAsync(h => h.Id == 1, ct);
        var headSeq = head?.LastSeq ?? 0;
        var headHashHex = Convert.ToHexString(head?.LastHash ?? AuditHasher.GenesisHash);

        var (startSeq, prevHash, seedTampered) = await ResolveSeedAsync(db, fromSeq, ct);

        // SECURITY (cloud-m4): seed resolution signals an impossible checkpoint — fail immediately.
        if (seedTampered)
            return new AuditVerifyResult(false, 0, null, false, headSeq, headHashHex);

        long checkedCount = 0;
        long expectedSeq = startSeq;
        var prev = prevHash;
        byte[]? lastRowHash = null;

        for (var pageStart = startSeq; ; pageStart = expectedSeq)
        {
            var page = await db.AuditEvents.AsNoTracking()
                .Where(e => e.Seq >= pageStart)
                .OrderBy(e => e.Seq)
                .Take(PageSize)
                .ToListAsync(ct);
            if (page.Count == 0)
                break;

            foreach (var row in page)
            {
                // Seq gap (other than rows pruned BEFORE the seed) breaks the chain.
                if (row.Seq != expectedSeq)
                    return new AuditVerifyResult(false, checkedCount, expectedSeq, false, headSeq, headHashHex);

                var recomputed = AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(row), prev);
                if (!prev.AsSpan().SequenceEqual(row.PrevHash) || !recomputed.AsSpan().SequenceEqual(row.RowHash))
                    return new AuditVerifyResult(false, checkedCount, row.Seq, false, headSeq, headHashHex);

                prev = row.RowHash;
                lastRowHash = row.RowHash;
                checkedCount++;
                expectedSeq = row.Seq + 1;
            }

            if (page.Count < PageSize)
                break;
        }

        // ── Head-consistency checks ───────────────────────────────────────────
        //
        // SECURITY: the next two checks close the "full-wipe" and "missing-head" attack surfaces.
        //
        // (A) Missing head when the chain appears to have content.
        //     If the head row was deleted by an attacker but events still exist, the seed
        //     resolves to Seq=1 (genesis) and checkedCount will be > 0.  We cannot confirm
        //     what the head *should* say, so treat it as a headMismatch.
        //     Edge case: an empty, brand-new DB with no head and no events → ok=true is correct
        //     (nothing to verify).  The guard is: checkedCount > 0 OR startSeq > 1 (explicit fromSeq
        //     or prune-checkpoint seed that implies events existed before the seed).
        if (head is null)
        {
            var emptyChain = checkedCount == 0 && startSeq <= 1;
            if (!emptyChain)
                return new AuditVerifyResult(false, checkedCount, null, true, headSeq, headHashHex);
        }
        else if (lastRowHash is null)
        {
            // (B) Head is present but NO rows were verified (full-wipe attack or the chain was
            //     entirely pruned and a prune-checkpoint seed past the last event was used).
            //     If the head claims LastSeq > 0, then rows should exist from startSeq onward —
            //     their absence (lastRowHash == null) is a headMismatch.
            //     Careful: a legitimate prune-checkpoint seed that lands exactly at the tip
            //     (prunedThroughSeq == head.LastSeq) produces startSeq = head.LastSeq + 1,
            //     so expectedSeq = startSeq and head.LastSeq == startSeq - 1 → that is fine.
            //     The mismatch is when head.LastSeq >= startSeq (i.e. the head claims rows
            //     exist that we should have verified but did not).
            if (head.LastSeq >= startSeq)
                return new AuditVerifyResult(false, checkedCount, null, true, headSeq, headHashHex);
        }
        else
        {
            // (C) Rows were verified — the final row's hash and seq must match the head.
            if (head.LastSeq != expectedSeq - 1 || !lastRowHash.AsSpan().SequenceEqual(head.LastHash))
                return new AuditVerifyResult(false, checkedCount, null, true, headSeq, headHashHex);
        }

        return new AuditVerifyResult(true, checkedCount, null, false, headSeq, headHashHex);
    }

    /// <summary>
    /// Seed resolution:
    /// - explicit fromSeq → seed from that row's own PrevHash (internal-consistency check from there);
    /// - latest audit.prune_checkpoint → {prunedThroughSeq, prunedThroughHash} from its DetailsJson;
    /// - otherwise genesis.
    ///
    /// The third element of the returned tuple is <c>true</c> when seed resolution detects an
    /// impossible checkpoint (cloud-m4: checkpoint.Seq &lt;= prunedThroughSeq) — the caller must
    /// immediately return ok=false without entering the verification loop.
    /// </summary>
    private static async Task<(long StartSeq, byte[] PrevHash, bool Tampered)> ResolveSeedAsync(
        KoratDbContext db, long? fromSeq, CancellationToken ct)
    {
        if (fromSeq is { } f and > 0)
        {
            var row = await db.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Seq == f, ct);
            return row is null
                ? (f, AuditHasher.GenesisHash, false) // nothing at fromSeq → loop exits immediately, Ok=true/0 checked
                : (f, row.PrevHash, false);
        }

        var checkpoint = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditActions.AuditPruneCheckpoint)
            .OrderByDescending(e => e.Seq)
            .FirstOrDefaultAsync(ct);
        if (checkpoint?.DetailsJson is { } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var prunedThroughSeq = doc.RootElement.GetProperty("prunedThroughSeq").GetInt64();
                var prunedThroughHash = Convert.FromHexString(
                    doc.RootElement.GetProperty("prunedThroughHash").GetString()!);

                // SECURITY (cloud-m4): a legitimate checkpoint is always written AFTER
                // the rows it summarises, so its own chain Seq must be strictly greater
                // than the prunedThroughSeq it records.  A checkpoint where Seq <= prunedThroughSeq
                // is physically impossible in a valid chain — the checkpoint row itself must come
                // after all the rows it prunes.  Treat this as tampering and signal the caller to
                // fail rather than silently skipping all verification.
                if (checkpoint.Seq <= prunedThroughSeq)
                    return (0, AuditHasher.GenesisHash, true);

                return (prunedThroughSeq + 1, prunedThroughHash, false);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                // Malformed checkpoint — fall through to genesis; verification will then
                // report the (pruned) gap, which is the correct "investigate me" signal.
            }
        }

        return (1, AuditHasher.GenesisHash, false);
    }
}
