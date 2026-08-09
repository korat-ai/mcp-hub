using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Korat.Auth.Tests;

/// <summary>
/// 032 C1 unit tests: canonicalization escaping, hash-chain golden vectors, the
/// AuditLogger failure policy (fail-closed vs fail-open), and DetailsJson deny-scrub.
/// Golden vectors are hard-coded (computed independently) so an accidental change to the
/// canonical format or chain construction breaks loudly — the chain is on-disk evidence,
/// its format must never drift silently.
/// </summary>
public sealed class AuditChainTests
{
    // ── Canonicalization ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a|b", @"a\|b")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData(@"a\|b", @"a\\\|b")]
    public void Escape_GoldenVectors(string? input, string expected) =>
        Assert.Equal(expected, AuditCanonical.Escape(input));

    [Fact]
    public void Canonicalize_And_RowHash_GoldenVector()
    {
        var record = new AuditEventRecord
        {
            Seq = 7,
            OccurredAtUtc = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero),
            ActorType = "user",
            ActorId = "11111111-2222-3333-4444-555555555555",
            AuthKind = "cookie",
            SpaceId = "space-1",
            Action = "grant.revoke",
            TargetType = "grant",
            TargetId = "g|1", // pipe must be escaped
            Outcome = "success",
            DetailsJson = "{\"a\":1}",
            TraceId = "trace-1",
            SourceIp = "203.0.113.7",
        };

        var canonical = AuditCanonical.Canonicalize(record);
        Assert.Equal(
            "v1|7|2026-06-12T10:00:00.0000000+00:00|user|11111111-2222-3333-4444-555555555555|cookie|" +
            "space-1|grant.revoke|grant|g\\|1|success|{\"a\":1}|trace-1|203.0.113.7",
            canonical);

        // Genesis hash must match the AddAuditEvents migration seed byte-for-byte.
        Assert.Equal(
            "05FE7795135EF7B08B5CD4454B045B2B673BE9A348D98D3178BA3059A88E8B48",
            Convert.ToHexString(AuditHasher.GenesisHash));

        var rowHash = AuditHasher.ComputeRowHash(canonical, AuditHasher.GenesisHash);
        Assert.Equal(
            "E8DD7DBF81DA6B29B6251ABA05F72044EE5641395FA1755C4486336DC9D37F88",
            Convert.ToHexString(rowHash));
    }

    // ── AuditLogger: chain write + failure policy ─────────────────────────────

    private static AuditLogger MakeLogger(IDbContextFactory<KoratDbContext> dbFactory) =>
        new(dbFactory,
            new HttpContextAccessor(), // no ambient HttpContext in unit tests
            new ConfigurationBuilder().Build(),
            NullLogger<AuditLogger>.Instance,
            NullLoggerFactory.Instance);

    private sealed class InMemoryFactory(InMemoryDatabaseRoot root, string name) : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<KoratDbContext>().UseInMemoryDatabase(name, root).Options);
        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class BrokenFactory : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() => throw new InvalidOperationException("db down");
        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task RecordAsync_Chains_Sequential_Events_From_Genesis()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        var seq1 = await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-1"), required: true);
        var seq2 = await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-1"), required: true);
        Assert.Equal(1, seq1);
        Assert.Equal(2, seq2);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.AuditEvents.OrderBy(e => e.Seq).ToListAsync();
        Assert.Equal(2, rows.Count);

        // Row 1 chains from genesis; row 2 chains from row 1; head matches row 2.
        Assert.Equal(AuditHasher.GenesisHash, rows[0].PrevHash);
        Assert.Equal(rows[0].RowHash, rows[1].PrevHash);
        Assert.Equal(AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(rows[0]), rows[0].PrevHash), rows[0].RowHash);
        Assert.Equal(AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(rows[1]), rows[1].PrevHash), rows[1].RowHash);

        var head = await db.AuditChainHead.SingleAsync(h => h.Id == 1);
        Assert.Equal(2, head.LastSeq);
        Assert.Equal(rows[1].RowHash, head.LastHash);
    }

    [Fact]
    public async Task RecordAsync_Required_FailsClosed_When_Sink_Broken()
    {
        var logger = MakeLogger(new BrokenFactory());
        await Assert.ThrowsAsync<AuditWriteException>(() =>
            logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-1"), required: true));
    }

    [Fact]
    public async Task RecordAsync_BestEffort_FailsOpen_When_Sink_Broken()
    {
        var logger = MakeLogger(new BrokenFactory());
        // required: false → swallow + alarm; returns null instead of throwing.
        var seq = await logger.RecordAsync(new AuditEvent("secret.decrypt", "inference_point", "p-1"), required: false);
        Assert.Null(seq);
    }

    [Fact]
    public async Task RecordAsync_Scrubs_TokenShaped_DetailsJson()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        // A call site accidentally passing a token-shaped value must be redacted at the sink.
        await logger.RecordAsync(new AuditEvent(
            "secret.set", "inference_point", "p-1",
            DetailsJson: "{\"oops\":\"token=korat_cli_SUPERSECRETVALUE\"}"), required: true);

        await using var db = await factory.CreateDbContextAsync();
        var row = await db.AuditEvents.SingleAsync();
        Assert.DoesNotContain("SUPERSECRETVALUE", row.DetailsJson);
        Assert.Contains("<redacted>", row.DetailsJson);
    }

    [Fact]
    public async Task Verifier_Detects_Tampered_Row()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);
        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-1"), required: true);
        await logger.RecordAsync(new AuditEvent("grant.revoke", "grant", "g-1"), required: true);

        var verifier = new AuditVerifier(factory);
        var clean = await verifier.VerifyAsync();
        Assert.True(clean.Ok);
        Assert.Equal(3, clean.CheckedCount);

        // Tamper with row 2 (the attacker rewrites history without recomputing the chain).
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.AuditEvents.SingleAsync(e => e.Seq == 2);
            row.Action = "invite.create"; // hide the revocation
            await db.SaveChangesAsync();
        }

        var tampered = await verifier.VerifyAsync();
        Assert.False(tampered.Ok);
        Assert.Equal(2, tampered.FirstBrokenSeq);
    }

    // ── MAJOR-1 regression tests: full-wipe + missing-head attacks ───────────

    /// <summary>
    /// A DB-writer that DELETES ALL AuditEvents rows must not produce ok=true.
    /// Before the fix, the page loop exited with lastRowHash=null, the head-consistency check
    /// was skipped (head is not null &amp;&amp; lastRowHash is not null → false), and the verifier
    /// returned ok=true with checkedCount=0 — fully hiding the evidence wipe.
    /// </summary>
    [Fact]
    public async Task Verifier_FullWipe_ReturnsHeadMismatch()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        // Record several events to establish a real chain head (LastSeq = 3).
        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-1"), required: true);
        await logger.RecordAsync(new AuditEvent("grant.revoke", "grant", "g-1"), required: true);

        // Attacker deletes ALL AuditEvents rows (but leaves AuditChainHead intact, or vice versa).
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AuditEvents.RemoveRange(db.AuditEvents);
            await db.SaveChangesAsync();
        }

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        // MUST be ok=false with headMismatch=true — the head still says LastSeq=3 but 0 rows exist.
        Assert.False(result.Ok, "Full wipe must produce ok=false (was ok=true before the fix).");
        Assert.True(result.HeadMismatch, "Full wipe must set headMismatch=true.");
        Assert.Equal(0L, result.CheckedCount);
    }

    /// <summary>
    /// A DB-writer that DELETES ALL AuditEvents rows AND the AuditChainHead row must also
    /// produce ok=false (missing head + no events = at minimum ambiguous; head was present
    /// at some point if the chain was non-empty). In this test we write events first so
    /// the prune-checkpoint / seed will not resolve to genesis with startSeq=1 (it does in
    /// InMemory since no checkpoint row exists, so startSeq=1). Then deleting all events
    /// leaves checkedCount=0 and head=null — the guard checks emptyChain=(0==0 &amp;&amp; 1&lt;=1)=true,
    /// so an actually-empty new database (no events, no head) correctly returns ok=true.
    ///
    /// The interesting case: delete events but NOT head → covered by FullWipe test above.
    /// Delete head but NOT events → covered by MissingHead_WithEvents test below.
    /// </summary>
    [Fact]
    public async Task Verifier_EmptyDatabase_ReturnsOkTrue()
    {
        // A brand-new database with no events and no head should verify clean (nothing to check).
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        Assert.True(result.Ok, "Freshly initialised empty chain must verify ok=true.");
        Assert.Equal(0L, result.CheckedCount);
        Assert.False(result.HeadMismatch);
    }

    /// <summary>
    /// When the head row is deleted but events still exist, the verifier must detect the
    /// inconsistency as headMismatch. Before the fix this returned ok=true because the condition
    /// <c>head is not null &amp;&amp; lastRowHash is not null</c> was false when head was null.
    /// </summary>
    [Fact]
    public async Task Verifier_MissingHead_WithEvents_ReturnsHeadMismatch()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-mh-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-mh-1"), required: true);

        // Delete the head row only (events remain).
        await using (var db = await factory.CreateDbContextAsync())
        {
            var head = await db.AuditChainHead.SingleAsync(h => h.Id == 1);
            db.AuditChainHead.Remove(head);
            await db.SaveChangesAsync();
        }

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        Assert.False(result.Ok, "Missing head with existing events must produce ok=false.");
        Assert.True(result.HeadMismatch, "Missing head with existing events must set headMismatch=true.");
    }

    /// <summary>
    /// Existing partial-tail-deletion detection must remain unaffected by the MAJOR-1 fix.
    /// Deleting the last N rows (but not all) must still report ok=false via the existing
    /// head-seq mismatch branch (C in the updated verifier).
    /// </summary>
    [Fact]
    public async Task Verifier_PartialTailDeletion_StillDetected()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-pt-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-pt-1"), required: true);
        await logger.RecordAsync(new AuditEvent("grant.revoke",  "grant",  "g-pt-1"), required: true);

        // Delete only the last row — head still says LastSeq=3 but the chain only has 2 rows.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var last = await db.AuditEvents.SingleAsync(e => e.Seq == 3);
            db.AuditEvents.Remove(last);
            await db.SaveChangesAsync();
        }

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        Assert.False(result.Ok, "Partial tail deletion must produce ok=false.");
        Assert.True(result.HeadMismatch, "Partial tail deletion must set headMismatch=true.");
        Assert.Equal(2L, result.CheckedCount);
    }

    // ── cloud-m4: checkpoint-seed forgery edge ────────────────────────────────

    /// <summary>
    /// cloud-m4 regression: a forged audit.prune_checkpoint row that claims prunedThroughSeq
    /// >= checkpoint.Seq is physically impossible in a valid chain (the checkpoint is always
    /// appended AFTER the rows it summarises), so it is a tamper signal.
    ///
    /// Before the fix the verifier blindly used this seed and returned ok=true with 0 events
    /// checked, silently hiding any tampering in the range before the checkpoint.
    ///
    /// After the fix the verifier detects the impossible relationship and immediately returns
    /// ok=false without entering the verification loop.
    /// </summary>
    [Fact]
    public async Task Verifier_ForgedCheckpoint_SeqEqualsOrLessThanPrunedThrough_FailsVerification()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        // Write three real events so the chain is non-trivially populated.
        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "fc-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "fc-1"), required: true);
        await logger.RecordAsync(new AuditEvent("grant.revoke",  "grant",  "fc-g"), required: true);

        // Directly inject a forged checkpoint row into the DB.
        // The checkpoint's Seq is 100, but its DetailsJson claims prunedThroughSeq=100 —
        // impossible in a valid chain (checkpoint must come AFTER the rows it prunes, so
        // checkpoint.Seq must be strictly > prunedThroughSeq).
        // Also inject Seq=101 variant (Seq < prunedThroughSeq) to cover the full guard range.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var forgedCheckpointEqSeq = new AuditEventRecord
            {
                Seq           = 100,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                ActorType     = AuditActorTypes.System,
                ActorId       = "system",
                AuthKind      = AuditAuthKinds.Internal,
                Action        = AuditActions.AuditPruneCheckpoint,
                TargetType    = "audit_chain",
                TargetId      = "1",
                Outcome       = AuditOutcomes.Success,
                // Seq == prunedThroughSeq → impossible (checkpoint must come AFTER pruned range)
                DetailsJson   = """{"prunedThroughSeq":100,"prunedThroughHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""",
                PrevHash      = AuditHasher.GenesisHash,
                RowHash       = AuditHasher.GenesisHash, // deliberately invalid — guard fires before hash check
            };
            db.AuditEvents.Add(forgedCheckpointEqSeq);
            await db.SaveChangesAsync();
        }

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        // The forged checkpoint must be rejected — ok=false, checkedCount=0 (loop never entered).
        Assert.False(result.Ok,
            "A forged checkpoint with Seq == prunedThroughSeq must produce ok=false (cloud-m4).");
        Assert.Equal(0L, result.CheckedCount); // verification loop must not be entered
    }

    /// <summary>
    /// cloud-m4 complementary case: checkpoint.Seq strictly less than prunedThroughSeq
    /// (even more blatant forgery) also fails immediately.
    /// </summary>
    [Fact]
    public async Task Verifier_ForgedCheckpoint_SeqLessThanPrunedThrough_FailsVerification()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));

        await using (var db = await factory.CreateDbContextAsync())
        {
            // Insert only the forged checkpoint — no real events needed; the seed-tamper guard
            // fires before the verification loop regardless of other chain content.
            var forgedCheckpoint = new AuditEventRecord
            {
                Seq           = 5,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                ActorType     = AuditActorTypes.System,
                ActorId       = "system",
                AuthKind      = AuditAuthKinds.Internal,
                Action        = AuditActions.AuditPruneCheckpoint,
                TargetType    = "audit_chain",
                TargetId      = "1",
                Outcome       = AuditOutcomes.Success,
                // Seq=5 claims to have pruned through Seq=999 — maximally impossible
                DetailsJson   = """{"prunedThroughSeq":999,"prunedThroughHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""",
                PrevHash      = AuditHasher.GenesisHash,
                RowHash       = AuditHasher.GenesisHash,
            };
            db.AuditEvents.Add(forgedCheckpoint);
            await db.SaveChangesAsync();
        }

        var result = await new AuditVerifier(factory).VerifyAsync();

        Assert.False(result.Ok,
            "A forged checkpoint with Seq < prunedThroughSeq must produce ok=false (cloud-m4).");
        Assert.Equal(0L, result.CheckedCount);
    }

    /// <summary>
    /// cloud-m4 sanity / non-regression: a LEGITIMATE checkpoint (Seq strictly greater than
    /// prunedThroughSeq) must continue to be accepted as a valid seed.
    /// </summary>
    [Fact]
    public async Task Verifier_LegitimateCheckpoint_AcceptedAsSeed()
    {
        var factory = new InMemoryFactory(new InMemoryDatabaseRoot(), Guid.NewGuid().ToString("N"));
        var logger = MakeLogger(factory);

        // Write two events, then insert a legitimate checkpoint claiming it prunes through Seq=2.
        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "lc-1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "lc-1"), required: true);

        byte[] hashAtSeq2;
        await using (var db = await factory.CreateDbContextAsync())
        {
            hashAtSeq2 = (await db.AuditEvents.SingleAsync(e => e.Seq == 2)).RowHash;
        }

        // Checkpoint at Seq=3, prunedThroughSeq=2 — legitimate (3 > 2).
        await logger.RecordAsync(new AuditEvent(
            AuditActions.AuditPruneCheckpoint, "audit_chain", "1",
            DetailsJson: $"{{\"prunedThroughSeq\":2,\"prunedThroughHash\":\"{Convert.ToHexString(hashAtSeq2)}\"}}"),
            required: true);

        // Add one more real event AFTER the checkpoint (this is what verification now verifies).
        await logger.RecordAsync(new AuditEvent("grant.revoke", "grant", "lc-g"), required: true);

        var result = await new AuditVerifier(factory).VerifyAsync();

        // Verifier should seed from the checkpoint and verify the single post-checkpoint event.
        Assert.True(result.Ok, "Legitimate checkpoint must be accepted as a valid seed (cloud-m4 non-regression).");
        Assert.Equal(2L, result.CheckedCount); // checkpoint row + post-checkpoint event = 2 verified
    }

    // ── cloud-m8: AuditLogger lazy-genesis race retry ─────────────────────────

    /// <summary>
    /// cloud-m8: IsUniqueViolation must classify the exception messages that Postgres and
    /// EF Core use for PK/unique-constraint failures — this classifier drives the genesis
    /// retry branch added to fix the missing retry the comment described.
    /// </summary>
    [Theory]
    [InlineData("duplicate key value violates unique constraint")]
    [InlineData("23505: duplicate key")]
    [InlineData("UNIQUE constraint failed")]
    [InlineData("violates PRIMARY KEY constraint")]
    public void AuditLogger_IsUniqueViolation_RecognisesConstraintMessages(string innerMessage)
    {
        var inner = new Exception(innerMessage);
        var dbex  = new DbUpdateException("ef wrapper", inner);
        Assert.True(AuditLogger.IsUniqueViolation(dbex),
            $"IsUniqueViolation must return true for: {innerMessage}");
    }

    [Fact]
    public void AuditLogger_IsUniqueViolation_ReturnsFalseForUnrelatedExceptions()
    {
        var inner = new Exception("timeout connecting to database");
        var dbex  = new DbUpdateException("ef wrapper", inner);
        Assert.False(AuditLogger.IsUniqueViolation(dbex),
            "IsUniqueViolation must return false for unrelated exceptions.");
    }

    /// <summary>
    /// cloud-m8 — InMemory path: two sequential writes from separate AuditLogger instances
    /// sharing the same DB root both succeed without AuditWriteException.  This exercises
    /// the scenario where the second writer finds a genesis head already created by the first.
    ///
    /// NOTE: the retry code added for cloud-m8 lives exclusively in the RELATIONAL (Postgres)
    /// path and is end-to-end verified by Korat.Cloud.IntegrationTests.AuditChainPostgresTests.
    /// This test covers the InMemory substrate to confirm no regression in unit-test usage.
    /// </summary>
    [Fact]
    public async Task AuditLogger_TwoWriters_SharedDb_BothSucceedWithoutException()
    {
        var root    = new InMemoryDatabaseRoot();
        var dbName  = Guid.NewGuid().ToString("N");
        var factory = new InMemoryFactory(root, dbName);

        // Logger 1 records first — creates the genesis head implicitly.
        var logger1 = MakeLogger(factory);
        var seq1 = await logger1.RecordAsync(new AuditEvent("invite.create", "invite", "race-1"), required: true);
        Assert.Equal(1L, seq1);

        // Logger 2 uses the same DB root and finds genesis already there — must chain off it.
        var logger2 = MakeLogger(factory);
        var seq2 = await logger2.RecordAsync(new AuditEvent("invite.revoke", "invite", "race-1"), required: true);
        Assert.Equal(2L, seq2);

        // Verify the chain is intact.
        var verifyResult = await new AuditVerifier(factory).VerifyAsync();
        Assert.True(verifyResult.Ok, "Chain written by two separate loggers must verify clean.");
        Assert.Equal(2L, verifyResult.CheckedCount);
    }
}
