using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cloud.Maintenance;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 032 (#57 Leg 3 C1/C2/C3): end-to-end audit-trail tests through the real HTTP stack.
///
/// Acceptance matrix (specs/032-leg3-hardening/plan.md §10):
///   A2  audited ops write the catalogued events (nodes.prune, cli_token.issue,
///       inference_point.create, secret.set, dek.create, secret.decrypt)
///   A3  fail-closed: poisoned audit sink → nodes prune returns 500
///   A4  fail-open:   poisoned audit sink → GetDecryptedAsync still returns plaintext
///   A5  tamper: rewritten row → /api/admin/audit/verify reports firstBrokenSeq
///   A6  prune writes a checkpoint and verification stays green afterwards
///   A7  admin ops: 401 anon / 403 non-admin; rewrap re-wraps; shred destroys + confirm guard
///   A11 owner-management endpoints carry the rate-limit policy metadata
/// </summary>
[Trait("Category", "AuditLogging")]
public sealed class AuditLoggingIntegrationTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── KEK plumbing (mirrors EnvelopeEncryptionIntegrationTests) ─────────────

    private static readonly string KekBase64;
    private static readonly string Kek2Base64;
    private const string KekId = "audit-k1";
    private const string KekId2 = "audit-k2";

    static AuditLoggingIntegrationTests()
    {
        var kek = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(kek);
        KekBase64 = Convert.ToBase64String(kek);
        var kek2 = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(kek2);
        Kek2Base64 = Convert.ToBase64String(kek2);
    }

    private WebApplicationFactory<Program> CreateEnvelopeFactory(
        string activeKekId = KekId, bool bothKeks = false, IAuditLog? replaceAudit = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            [$"Korat:Envelope:Keks:{KekId}"] = KekBase64,
            ["Korat:Envelope:ActiveKekId"] = activeKekId,
        };
        if (bothKeks)
            cfg[$"Korat:Envelope:Keks:{KekId2}"] = Kek2Base64;

        return fixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            if (replaceAudit is not null)
                b.ConfigureTestServices(s => s.AddSingleton(replaceAudit));
        });
    }

    /// <summary>Bearer-authenticated client (headless → antiforgery skipped by design).</summary>
    private async Task<HttpClient> CreateBearerClientAsync(
        WebApplicationFactory<Program> factory, Korat.Domain.Auth.UserId userId)
    {
        string raw;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
            raw = (await tokens.IssueAsync(userId.Value, "full", default)).RawToken;
        }
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", raw);
        return client;
    }

    private async Task<List<AuditEventRecord>> EventsAsync(Func<AuditEventRecord, bool> predicate)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        return (await db.AuditEvents.AsNoTracking().OrderBy(e => e.Seq).ToListAsync())
            .Where(predicate).ToList();
    }

    // ── A2: nodes.prune + cli_token.issue audited, chained, actor recorded ──

    [Fact]
    public async Task A2_NodesPrune_And_CliTokenIssue_Write_Chained_Audit_Events()
    {
        var admin = await fixture.SeedUserAsync($"audit-a2-{Guid.NewGuid():N}@example.com", "Audit A2");
        await fixture.MakeAdminAsync(admin.UserId);
        var client = await CreateBearerClientAsync(fixture.Factory, admin.UserId);

        // Prune audits fail-closed even when nothing matches, so no node seeding is needed.
        var resp = await client.PostAsync("/api/nodes/prune",
            JsonContent.Create(new { kind = "agent", olderThanDays = 30 }));
        resp.EnsureSuccessStatusCode();

        // cli_token.issue from CreateBearerClientAsync.
        var issued = await EventsAsync(e =>
            e.Action == AuditActions.CliTokenIssue && e.ActorId == admin.UserId.Value.ToString());
        Assert.NotEmpty(issued);

        // nodes.prune with the real admin actor.
        var pruned = Assert.Single(await EventsAsync(e =>
            e.Action == AuditActions.NodesPrune && e.ActorId == admin.UserId.Value.ToString()));
        Assert.Equal(AuditActorTypes.User, pruned.ActorType);
        Assert.Equal(AuditOutcomes.Success, pruned.Outcome);

        // The row's own hash link must be internally consistent (full-chain verify is
        // exercised separately — other tests intentionally tamper with the shared chain).
        Assert.Equal(
            AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(pruned), pruned.PrevHash),
            pruned.RowHash);
        // And the audit trail NEVER contains a raw token.
        Assert.DoesNotContain("korat_cli_", pruned.DetailsJson ?? "");
    }

    // ── A2: envelope secret surface (secret.set / dek.create / secret.decrypt / point.create) ──


    /// <summary>
    /// An IAuditLog that always fails, so the fail policy itself can be observed:
    /// required → throw (fail-closed); best-effort → null (fail-open). The AuditLogger-side
    /// behaviour (its own swallow/throw split on a dead DB) is unit-tested in AuditChainTests.
    /// </summary>
    private sealed class PoisonedAuditLog : IAuditLog
    {
        public Task<long?> RecordAsync(AuditEvent auditEvent, bool required, CancellationToken ct = default) =>
            required
                ? throw new AuditWriteException("audit sink poisoned (test)", new InvalidOperationException())
                : Task.FromResult<long?>(null);
    }

    [Fact]
    public async Task A3_FailClosed_Poisoned_Sink_Makes_NodesPrune_Return_500()
    {
        var admin = await fixture.SeedUserAsync($"audit-a3-{Guid.NewGuid():N}@example.com", "Audit A3");
        await fixture.MakeAdminAsync(admin.UserId);

        using var poisoned = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IAuditLog>(new PoisonedAuditLog())));
        var client = await CreateBearerClientAsync(poisoned, admin.UserId);

        // Fail-closed: the audit failure must surface as a server error. In production
        // Kestrel turns the unhandled AuditWriteException into a 500; the TestServer
        // rethrows unhandled server exceptions to the client instead — accept both shapes.
        try
        {
            var resp = await client.PostAsync("/api/nodes/prune",
                JsonContent.Create(new { kind = "agent", olderThanDays = 30 }));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Contains("audit", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }


    [Fact]
    public async Task A5_Tampered_Row_Is_Reported_By_Admin_Verify_Endpoint()
    {
        var admin = await fixture.SeedUserAsync($"audit-a5-{Guid.NewGuid():N}@example.com", "Audit A5");
        await fixture.MakeAdminAsync(admin.UserId);
        var client = await CreateBearerClientAsync(fixture.Factory, admin.UserId);

        // Ensure at least one event exists, then tamper with the LAST row and restore it after —
        // the chain is shared fixture state; leaving it broken would poison later verifies.
        var auditLog = fixture.Factory.Services.GetRequiredService<IAuditLog>();
        var seq = await auditLog.RecordAsync(
            new AuditEvent("nodes.prune", "node", $"tamper-{Guid.NewGuid():N}"), required: true);
        Assert.NotNull(seq);

        string originalTargetId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var row = await db.AuditEvents.SingleAsync(e => e.Seq == seq);
            originalTargetId = row.TargetId;
            row.TargetId = "rewritten-by-attacker";
            await db.SaveChangesAsync();
        }

        try
        {
            var resp = await client.GetAsync("/api/admin/audit/verify");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Equal(seq, body.GetProperty("firstBrokenSeq").GetInt64());
        }
        finally
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            var row = await db.AuditEvents.SingleAsync(e => e.Seq == seq);
            row.TargetId = originalTargetId;
            await db.SaveChangesAsync();
        }

        // Restored → verify is green again.
        var clean = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/verify");
        Assert.True(clean.GetProperty("ok").GetBoolean());
    }

    // ── A6: prune writes a checkpoint; verification survives pruning ──────────

    [Fact]
    public async Task A6_Prune_Writes_Checkpoint_And_Verify_Stays_Green()
    {
        var auditLog = fixture.Factory.Services.GetRequiredService<IAuditLog>();
        await auditLog.RecordAsync(new AuditEvent("invite.create", "invite", "prune-1"), required: true);
        await auditLog.RecordAsync(new AuditEvent("invite.revoke", "invite", "prune-1"), required: true);

        var dbFactory = fixture.Factory.Services.GetRequiredService<IDbContextFactory<KoratDbContext>>();
        var prune = new AuditPruneService(auditLog, dbFactory, NullLogger<AuditPruneService>.Instance);

        // Cutoff in the future ⇒ every existing row is "expired" — prunes the whole trail.
        var deleted = await prune.PruneOnceAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.True(deleted >= 2);

        // A chained checkpoint must exist and carry the reseed material.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var checkpoint = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditActions.AuditPruneCheckpoint)
            .OrderByDescending(e => e.Seq).FirstAsync();
        Assert.Contains("prunedThroughSeq", checkpoint.DetailsJson);
        Assert.Contains("prunedThroughHash", checkpoint.DetailsJson);

        // Verification reseeds from the checkpoint — the missing (pruned) prefix is NOT a break.
        var verifier = new AuditVerifier(dbFactory);
        var result = await verifier.VerifyAsync();
        Assert.True(result.Ok, $"verify after prune must stay green (firstBrokenSeq={result.FirstBrokenSeq})");
        Assert.True(result.CheckedCount >= 1); // at least the checkpoint row itself
    }

    // ── A7: admin ops endpoints — auth gates, rewrap, crypto-shred ────────────

    [Fact]
    public async Task A7_Admin_Endpoints_Reject_Anonymous_And_NonAdmin()
    {
        var anon = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/audit/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/admin/envelope/rewrap", content: null)).StatusCode);

        var user = await fixture.SeedUserAsync($"audit-nonadmin-{Guid.NewGuid():N}@example.com", "Non Admin");
        var client = await CreateBearerClientAsync(fixture.Factory, user.UserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/audit/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/envelope/rewrap", content: null)).StatusCode);
    }

    [Fact]
    public async Task A7_Rewrap_Endpoint_Rewraps_Deks_And_Audits()
    {
        var admin = await fixture.SeedUserAsync($"audit-rewrap-{Guid.NewGuid():N}@example.com", "Audit Rewrap");
        await fixture.MakeAdminAsync(admin.UserId);

        // Phase 1: write a secret under k1 (creates a DEK row wrapped by k1).
        using (var k1Factory = CreateEnvelopeFactory())
        {
            var client = await CreateBearerClientAsync(k1Factory, admin.UserId);
            (await client.PostAsync("/api/mcp-servers", JsonContent.Create(new
            {
                displayName = $"rw-{Guid.NewGuid():N}",
                remoteUrl = "https://mcp.example.test/",
                authMode = "bearer",
                secret = "sk-rewrap-test"
            }))).EnsureSuccessStatusCode();
        }

        // Phase 2: rotate — active KEK is k2, k1 still present for unwrap.
        using var k2Factory = CreateEnvelopeFactory(activeKekId: KekId2, bothKeks: true);
        var adminClient = await CreateBearerClientAsync(k2Factory, admin.UserId);
        var resp = await adminClient.PostAsync("/api/admin/envelope/rewrap", content: null);
        resp.EnsureSuccessStatusCode();
        var processed = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("processed").GetInt32();
        Assert.True(processed >= 1);

        using var scope = k2Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        Assert.False(await db.SpaceEncryptionKeys.AnyAsync(r => r.KekId == KekId),
            "after rewrap no DEK row may remain under the old KEK");

        Assert.NotEmpty(await EventsAsync(e =>
            e.Action == AuditActions.KekRewrap && e.ActorId == admin.UserId.Value.ToString()));
    }

    [Fact]
    public async Task A7_CryptoShred_Requires_Confirmation_And_Destroys_Deks()
    {
        var admin = await fixture.SeedUserAsync($"audit-shred-{Guid.NewGuid():N}@example.com", "Audit Shred");
        await fixture.MakeAdminAsync(admin.UserId);

        using var factory = CreateEnvelopeFactory();
        var client = await CreateBearerClientAsync(factory, admin.UserId);

        (await client.PostAsync("/api/mcp-servers", JsonContent.Create(new
        {
            displayName = $"sh-{Guid.NewGuid():N}",
            remoteUrl = "https://mcp.example.test/",
            authMode = "bearer",
            secret = "sk-shred-me"
        }))).EnsureSuccessStatusCode();

        // Confirmation mismatch → 400, nothing destroyed.
        var bad = await client.PostAsync($"/api/admin/spaces/{admin.SpaceId}/crypto-shred",
            JsonContent.Create(new { confirm = "wrong" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            Assert.True(await db.SpaceEncryptionKeys.AnyAsync(r => r.SpaceId == admin.SpaceId));
        }

        // Correct confirmation → DEK rows destroyed + dek.shred audited.
        var ok = await client.PostAsync($"/api/admin/spaces/{admin.SpaceId}/crypto-shred",
            JsonContent.Create(new { confirm = admin.SpaceId }));
        ok.EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
            Assert.False(await db.SpaceEncryptionKeys.AnyAsync(r => r.SpaceId == admin.SpaceId),
                "crypto-shred must delete every DEK row for the space");
        }

        Assert.NotEmpty(await EventsAsync(e =>
            e.Action == AuditActions.DekShred && e.SpaceId == admin.SpaceId));
    }

    // ── A11: rate-limit coverage on the owner-management surface ──────────────

    [Fact]
    public void A11_Owner_Management_Endpoints_Carry_RateLimit_Policy()
    {
        var endpointSource = fixture.Factory.Services.GetRequiredService<EndpointDataSource>();
        string[] mustBeLimited =
        [
            "/api/space",
            "/api/grants/{grantId}/revoke",
            "/api/access-requests/{requestId}/approve",
            "/api/mcp-servers/{serverId}/disable",
            "/api/mcp-servers",
        ];

        foreach (var pattern in mustBeLimited)
        {
            var matches = endpointSource.Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText == pattern)
                .ToList();
            Assert.NotEmpty(matches);
            foreach (var endpoint in matches)
            {
                var meta = endpoint.Metadata
                    .OfType<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
                    .FirstOrDefault();
                Assert.NotNull(meta);
                Assert.Equal(
                    Korat.Cloud.Web.Auth.Security.RateLimiterRegistration.OwnerManagementPolicy,
                    meta!.PolicyName);
            }
        }
    }
}
