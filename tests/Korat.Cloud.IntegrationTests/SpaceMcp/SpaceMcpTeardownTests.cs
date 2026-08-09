using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// MUST-FIX 1 (adversarial review, Space-MCP increment 1 Tasks 4-6, BLOCKER): before this fix,
/// <c>SpaceMcpAggregatorGrain</c>'s teardown paths (<c>TerminateAsync</c>,
/// <c>OnDeactivateAsync</c>, the <c>OpenBackendAsync</c> handshake-timeout catch) called ONLY
/// <c>SpaceBackendSession.OnClosed(...)</c> — which flips this grain's own LOCAL <c>_isAlive</c>
/// flag and faults its in-flight <c>TaskCompletionSource</c>, but never terminates the
/// PUBLISHER-side relay session. Every DELETE / grain-deactivation / handshake-timeout therefore
/// leaked a live publisher-side MCP subprocess forever. The fix injects <c>SessionTerminator</c>
/// and calls <c>TerminateSessionAsync</c> on every path that drops a backend.
///
/// These tests drive the grain directly against a REAL gRPC-connected <see cref="FakeMcpPublisher"/>
/// (mirrors <c>SpaceMcpInitializeToolsTests</c>) and assert the teardown is REAL, not cosmetic:
/// the publisher receives an actual <c>CloseSession</c> control frame for the backend's relay
/// <c>SessionId</c>, AND the underlying <c>SessionGrain</c> is durably <c>Closed</c>.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpTeardownTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string ClientInitializeJson = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private static readonly TimeSpan CloseSessionWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TerminateAsync_TerminatesBackendRelaySession_PublisherReceivesCloseSession_AndSessionGrainIsClosed()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-teardown-{Guid.NewGuid():N}@example.com", "Space MCP Teardown");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"teardown-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // N-f (adversarial review): the fake publisher connects as a real relay node — a
        // "full"-scoped token, never the space-mcp-scoped `cliToken` (which node Hello now
        // correctly rejects).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
        await grain.InitializeAsync(ctx, ClientInitializeJson);

        // The backend's relay SessionId, learned by observation (the aggregator's own initialize
        // handshake exchanged a Frame for it) — not by reaching into the grain's internals.
        var relaySessionId = Assert.Single(publisher.SeenSessionIds);

        // ── Act: TerminateAsync (mirrors the HTTP responder's DELETE handler, Task 7) ────────
        await grain.TerminateAsync();

        // ── Assert (1): the PUBLISHER actually received a CloseSession control frame ─────────
        var received = await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait);
        Assert.True(received,
            "Expected the publisher to receive a CloseSession control frame for the backend relay " +
            "session after TerminateAsync — teardown must terminate the relay session, not just " +
            "flip the aggregator's own local grain state.");

        // ── Assert (2): the underlying SessionGrain is durably Closed ────────────────────────
        var sessionState = await fixture.ClusterClient
            .GetGrain<ISessionGrain>(relaySessionId).GetAsync();
        Assert.Equal(SessionStatus.Closed, sessionState.Status);
        Assert.Equal(SessionCloseReason.Completed, sessionState.CloseReason);
    }

    /// <summary>
    /// MUST-FIX 1's third teardown path: a granted backend that never answers "tools/list" (Task
    /// 5's HangOnToolsList) is dropped once <c>PerBackendTimeout</c> elapses via the
    /// <c>OpenBackendAsync</c> handshake-timeout catch — that catch must ALSO terminate the
    /// admitted-but-hung backend's relay session, not merely mark it locally dead, otherwise the
    /// orphaned publisher-side subprocess is never cleaned up at all (no DELETE/deactivate ever
    /// follows for a backend that was never successfully cataloged).
    /// </summary>
    [Fact]
    public async Task Initialize_HungBackendHandshakeTimeout_TerminatesTheOrphanedRelaySession()
    {
        var originalTimeout = SpaceMcpAggregatorGrain.PerBackendTimeout;
        SpaceMcpAggregatorGrain.PerBackendTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-teardown-hung-{Guid.NewGuid():N}@example.com", "Space MCP Teardown Hung");
            var spaceId = new SpaceId(seeded.SpaceId);
            var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"hung-teardown-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

            // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
            // authorize→consent→code→token flow, not from a machine-wide CLI token.
            var (cliToken, consumerIdentity) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            // N-f: the fake publisher connects as a real relay node — a "full"-scoped token,
            // never the space-mcp-scoped `cliToken` (which node Hello now correctly rejects).
            var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
            await using var publisher = await FakeMcpPublisher.ConnectAsync(
                fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "hung", null)]);
            publisher.HangOnToolsList = true;

            var sessionKey = $"test-session-{Guid.NewGuid():N}";
            var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
            var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);
            await grain.InitializeAsync(ctx, ClientInitializeJson);

            // The backend progressed far enough to exchange "initialize" (the publisher saw a
            // Frame for it) before hanging on "tools/list" — same observation technique as above.
            var relaySessionId = Assert.Single(publisher.SeenSessionIds);

            var received = await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait);
            Assert.True(received,
                "Expected the hung backend's relay session to be terminated (CloseSession " +
                "delivered to the publisher) once PerBackendTimeout elapsed — the handshake-timeout " +
                "catch must not merely mark the backend locally dead.");

            var sessionState = await fixture.ClusterClient
                .GetGrain<ISessionGrain>(relaySessionId).GetAsync();
            Assert.Equal(SessionStatus.Closed, sessionState.Status);
        }
        finally
        {
            SpaceMcpAggregatorGrain.PerBackendTimeout = originalTimeout;
        }
    }

    /// <summary>
    /// MUST-FIX F1 (adversarial review, second pass, BLOCKER): the exact teardown-vs-fanout race —
    /// <c>TerminateAsync</c>'s <c>_backendsBySessionId</c> snapshot runs to completion WHILE a
    /// granted backend is still "inside" <c>admission.AdmitAsync</c> from the aggregator's own
    /// point of view (production: node-wake can take seconds). <see cref="GatedSessionAdmission"/>
    /// lets this run the REAL admission to completion first (so the underlying relay session — DB
    /// row, routing-table entry — is genuinely opened, exactly like a real node-wake delay would
    /// leave it), then holds the RETURN to the aggregator grain hostage until <c>TerminateAsync</c>
    /// has already run and flipped <c>_tornDown</c>.
    ///
    /// Before the fix: the late backend gets indexed into (by-then torn-down) grain dictionaries,
    /// its handshake succeeds against a still-live relay session nothing ever closes, and it would
    /// leak forever (publisher-side subprocess + an Active session row). With the fix: checkpoint
    /// (a) in <c>OpenBackendAsync</c> observes <c>_tornDown</c> immediately after admission returns
    /// and terminates the just-opened session WITHOUT ever indexing it or attempting a handshake —
    /// asserted here by BOTH the publisher receiving a real CloseSession AND the publisher never
    /// having seen so much as an "initialize" frame for that session.
    /// </summary>
    [Fact]
    public async Task TerminateAsync_RacingLateOpeningBackend_TerminatesItBeforeIndexing_NoLeak()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-teardown-race-{Guid.NewGuid():N}@example.com", "Space MCP Teardown Race");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"race-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);

        // Arm the gate BEFORE triggering admission — the real SessionAdmission will run to
        // completion (minting a real SessionId, opening a real routing-table entry) but its
        // RETURN to the aggregator's OpenBackendAsync is held hostage until Release(...) below.
        GatedSessionAdmission.Arm(server.Id.Value);
        try
        {
            var sessionKey = $"test-session-{Guid.NewGuid():N}";
            var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
            var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);

            // Fire InitializeAsync WITHOUT awaiting — its fan-out will suspend on the armed gate.
            var initTask = grain.InitializeAsync(ctx, ClientInitializeJson);

            // Wait for the gate to observe the REAL minted SessionId — proves the underlying
            // relay session already exists (admission has already decided Opened) before we race
            // TerminateAsync against the still-held gate.
            var relaySessionId = await GatedSessionAdmission.WaitForObservedSessionIdAsync(
                server.Id.Value, TimeSpan.FromSeconds(10));

            // ── Act: TerminateAsync runs and completes WHILE the late backend is still held ────
            // The grain is [Reentrant] — this call interleaves with InitializeAsync's suspended
            // fan-out turn. Its _backendsBySessionId snapshot is empty (the late backend was never
            // indexed — admission hasn't returned to OpenBackendAsync yet), so its teardown loop
            // has nothing to terminate; it still flips _tornDown, unregisters the delivery leg,
            // and clears its (already-empty) dictionaries.
            await grain.TerminateAsync();

            // NOW let the held admission result flow back to OpenBackendAsync.
            GatedSessionAdmission.Release(server.Id.Value);

            // InitializeAsync's fan-out completes once checkpoint (a) terminates the late backend.
            await initTask;

            // ── Assert (1): the publisher received a REAL CloseSession for the late session ────
            var received = await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait);
            Assert.True(received,
                "Expected the late-opening backend's relay session to be terminated (CloseSession " +
                "delivered to the publisher) even though admission only returned AFTER TerminateAsync " +
                "had already run — the pre-fix code would index/handshake/catalog it into an already " +
                "torn-down activation instead, leaking the relay session forever.");

            // ── Assert (2): checkpoint (a) fired BEFORE any handshake — the publisher never even
            // saw an "initialize" frame for this session (proves we caught the BEFORE-INDEXING
            // race, not merely the after-handshake one N-b/checkpoint (b) already covered).
            Assert.DoesNotContain(relaySessionId, publisher.SeenSessionIds);

            // ── Assert (3): the underlying SessionGrain is durably Closed — no leaked Active row.
            var sessionState = await fixture.ClusterClient
                .GetGrain<ISessionGrain>(relaySessionId).GetAsync();
            Assert.Equal(SessionStatus.Closed, sessionState.Status);
        }
        finally
        {
            GatedSessionAdmission.Release(server.Id.Value); // safety net if an assertion throws first.
        }
    }

    private async Task<Guid> GetTokenIdAsync(string rawToken)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var id = await cliTokens.GetTokenIdAsync(rawToken, default);
        Assert.NotNull(id);
        return id!.Value;
    }
}
