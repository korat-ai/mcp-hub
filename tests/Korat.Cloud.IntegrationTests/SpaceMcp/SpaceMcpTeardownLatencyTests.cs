using System.Diagnostics;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// S1 (whole-feature adversarial review): verifies EMPIRICALLY whether
/// <c>SpaceMcpAggregatorGrain.TerminateAsync</c>'s (and, by the same code shape,
/// <c>OnDeactivateAsync</c>'s) per-backend inline
/// <c>await terminator.TerminateSessionAsync(...)</c> loop suffers the same self-notify stall
/// <see cref="SpaceMcpAggregatorGrain"/>'s own <c>EvictDeadBackendLocal</c> doc comment documents
/// an EMPIRICAL ~30s-per-call finding for (Orleans' un-overridden default response timeout, hit
/// because every backend this grain opens has its "agent" side bound to THIS SAME activation's
/// synthetic <c>ConnectionId</c>, so terminating it loops back through
/// <c>SendToConnectionAsync</c> → <c>CallbackServerStreamWriter</c> → a nested
/// <c>OnDeliveryAsync</c> call on this same activation).
///
/// <c>TerminateAsync</c> is dispatched from the HTTP responder (a fresh grain call), not from
/// inside a frame delivery already holding <c>SpaceBackendSession</c>'s per-connection lock the
/// way <c>EvictDeadBackendLocal</c>/<c>ReconcileAsync</c> are — so it may not hit the same hazard.
/// Rather than assume either way, this test MEASURES: it seeds 5 granted backends (enough that a
/// real per-backend ~30s stall would be unmistakable — ~150s — against a healthy sub-second
/// teardown) and times the actual wall-clock <c>TerminateAsync</c> call.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpTeardownLatencyTests(KoratIntegrationFixture fixture, ITestOutputHelper output)
    : IClassFixture<KoratIntegrationFixture>
{
    private const string ClientInitializeJson = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private static readonly TimeSpan CloseSessionWait = TimeSpan.FromSeconds(5);

    // A healthy teardown is sub-second; a per-backend ~30s self-notify stall would blow this
    // budget by more than an order of magnitude even for just this many backends.
    private const int BackendCount = 5;

    [Fact]
    public async Task TerminateAsync_MultipleGrantedBackends_CompletesWellUnderNTimesThirtySeconds()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-teardown-latency-{Guid.NewGuid():N}@example.com", "Space MCP Teardown Latency");
        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);

        // N-f (adversarial review precedent): fake publishers connect as real relay nodes — a
        // "full"-scoped token, never the space-mcp-scoped `cliToken` (node Hello rejects that).
        var publisherCliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        // ── Arrange: BackendCount granted backends, each behind its own publisher node ─────────
        var backends = new List<(string PublisherNodeId, McpServer Server)>();
        for (var i = 0; i < BackendCount; i++)
        {
            var publisherNodeId = NodeId.New().Value;
            var server = (await space.PublishMcpServerAsync(
                new NodeId(publisherNodeId), $"latency-srv-{i}-{Guid.NewGuid():N}", "echo", "demo"))!;

            var accessRequest = await space.CreateAccessRequestAsync(consumerIdentity, server.Id, NodeId.New());
            await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

            backends.Add((publisherNodeId, server));
        }

        var publishers = new List<FakeMcpPublisher>();
        try
        {
            foreach (var (publisherNodeId, _) in backends)
            {
                var publisher = await FakeMcpPublisher.ConnectAsync(
                    fixture.Factory, publisherNodeId, publisherCliToken, tools: [("echo", "Echoes input back", null)]);
                publishers.Add(publisher);
            }

            var sessionKey = $"test-session-{Guid.NewGuid():N}";
            var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
            var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);

            // InitializeAsync fans out concurrently to every granted backend (Task.WhenAll) — by
            // the time this returns, all BackendCount backends are open and indexed.
            await grain.InitializeAsync(ctx, ClientInitializeJson);

            var relaySessionIds = publishers.Select(p => Assert.Single(p.SeenSessionIds)).ToList();
            Assert.Equal(BackendCount, relaySessionIds.Distinct().Count());

            // ── Act: time the ACTUAL wall-clock TerminateAsync call ────────────────────────────
            var stopwatch = Stopwatch.StartNew();
            await grain.TerminateAsync();
            stopwatch.Stop();
            output.WriteLine(
                $"S1 MEASURED: TerminateAsync with {BackendCount} granted backends took {stopwatch.Elapsed} wall-clock.");

            // ── Assert (1): no per-backend self-notify stall. A healthy teardown is sub-second;
            // a real ~30s-per-backend stall would measure ~150s for 5 backends — this budget
            // (10s) sits comfortably above real-world noise and comfortably below even a SINGLE
            // backend's ~30s stall, so it unambiguously distinguishes the two.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"Expected TerminateAsync with {BackendCount} granted backends to complete well " +
                $"under {BackendCount}×30s (a per-backend self-notify stall would measure " +
                $"~{BackendCount * 30}s); measured {stopwatch.Elapsed}.");

            // ── Assert (2): teardown correctness is preserved — every backend's relay session
            // actually reached the publisher as a real CloseSession, not just local bookkeeping.
            foreach (var (publisher, relaySessionId) in publishers.Zip(relaySessionIds))
            {
                var received = await publisher.WaitForCloseSessionAsync(relaySessionId, CloseSessionWait);
                Assert.True(received,
                    $"Expected publisher for relay session {relaySessionId} to receive a " +
                    "CloseSession control frame after TerminateAsync — teardown must terminate " +
                    "every backend's relay session, not just flip the aggregator's own local " +
                    "grain state.");
            }

            foreach (var relaySessionId in relaySessionIds)
            {
                var sessionState = await fixture.ClusterClient
                    .GetGrain<ISessionGrain>(relaySessionId).GetAsync();
                Assert.Equal(SessionStatus.Closed, sessionState.Status);
                Assert.Equal(SessionCloseReason.Completed, sessionState.CloseReason);
            }
        }
        finally
        {
            foreach (var publisher in publishers)
                await publisher.DisposeAsync();
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
