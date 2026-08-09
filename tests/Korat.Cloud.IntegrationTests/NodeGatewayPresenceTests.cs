using System.Text;
using System.Text.Json;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Korat.Relay.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

public sealed class NodeGatewayPresenceTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Hello_MarksNodeOnlineInSpaceOverview()
    {
        // Seed a real user+space and issue a CLI token for Bearer auth.
        var seeded = await fixture.SeedUserAsync(
            $"gateway-presence-{Guid.NewGuid():N}@example.com",
            "Gateway Presence Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "gateway-online",
                // No NodeAuthToken needed — Bearer is the auth mechanism.
                // SpaceId intentionally omitted — server resolves it from the CLI token.
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        using var httpClient = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var json = await httpClient.GetStringAsync("/api/space");
        Assert.Contains("gateway-online", json);
        Assert.Contains("Online", json);
    }
}

/// <summary>
/// Node host metadata (additive, node-visibility-doctor design 2026-07-02): hostname/os/arch/
/// cli_version collected in NodeHello (fields 8–10, cli_version was already field 6) and
/// persisted on the Node entity by NodeGrain.ConnectAsync, refreshed on every hello.
/// </summary>
public sealed class NodeHostMetadataTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Hello_WithHostMetadata_NodeExposesIt()
    {
        var seeded = await fixture.SeedUserAsync(
            $"host-metadata-{Guid.NewGuid():N}@example.com",
            "Host Metadata Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "host-metadata-node",
                Hostname = "MacBook-Pro.local",
                Os = "macos",
                Arch = "arm64",
                CliVersion = "0.4.1",
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId).GetAsync();
        Assert.Equal("MacBook-Pro.local", node.Hostname);
        Assert.Equal("macos", node.Os);
        Assert.Equal("arm64", node.Arch);
        Assert.Equal("0.4.1", node.CliVersion);
    }

    [Fact]
    public async Task Hello_WithoutHostMetadata_NodeHasNullMetadata_NoFailure()
    {
        // Legacy CLI (pre-node-visibility-doctor): Hello omits hostname/os/arch/cli_version
        // (proto3 default ""). The cloud must accept the connection and store null, not "".
        var seeded = await fixture.SeedUserAsync(
            $"host-metadata-legacy-{Guid.NewGuid():N}@example.com",
            "Legacy Hello Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "legacy-hello-node",
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId).GetAsync();
        Assert.Null(node.Hostname);
        Assert.Null(node.Os);
        Assert.Null(node.Arch);
        Assert.Null(node.CliVersion);
    }

    [Fact]
    public async Task Hello_WithOversizedHostMetadata_TruncatesTo256()
    {
        // B3-review (low): the four client-controlled metadata strings are capped at 256 chars
        // (varchar(256) in the DB); the gateway truncates rather than rejects so a buggy CLI
        // still connects but cannot persist unbounded strings.
        var seeded = await fixture.SeedUserAsync(
            $"host-metadata-oversize-{Guid.NewGuid():N}@example.com",
            "Oversize Metadata Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        var oversized = new string('h', 300);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = nodeId,
                DisplayName = "oversize-metadata-node",
                Hostname = oversized,
                Os = new string('o', 300),
                Arch = new string('a', 300),
                CliVersion = new string('v', 300),
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);

        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId).GetAsync();
        Assert.Equal(oversized[..256], node.Hostname);
        Assert.Equal(256, node.Os!.Length);
        Assert.Equal(256, node.Arch!.Length);
        Assert.Equal(256, node.CliVersion!.Length);
    }
}

/// <summary>
/// B3-review (blocker regression): the owner-editable Note is set only via PATCH /api/nodes/{id}
/// and must SURVIVE node reconnects (daemon restart, network blip, rolling deploy). Before the
/// fix, NodeGrain.ConnectAsync rebuilt _state without copying Note, so every hello wiped it.
/// </summary>
public sealed class NodeNoteSurvivesReconnectTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static NodeToGatewayMessage HelloMessage(string nodeId, bool withHostMetadata)
    {
        var hello = new NodeHello
        {
            NodeId = nodeId,
            DisplayName = "note-reconnect-node",
        };
        if (withHostMetadata)
        {
            hello.Hostname = "Reconnect-Box.local";
            hello.Os = "macos";
            hello.Arch = "arm64";
            hello.CliVersion = "0.4.1";
        }
        return new NodeToGatewayMessage { Hello = hello };
    }

    private static async Task SendHelloAsync(
        NodeGatewayService.NodeGatewayServiceClient grpcClient, string cliToken, string nodeId, bool withHostMetadata)
    {
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(HelloMessage(nodeId, withHostMetadata));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);
    }

    [Fact]
    public async Task Note_SurvivesReconnectHello_WithAndWithoutHostMetadata()
    {
        var seeded = await fixture.SeedUserAsync(
            $"note-reconnect-{Guid.NewGuid():N}@example.com",
            "Note Reconnect Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var nodeId = NodeId.New().Value;
        var grpcClient = GrpcTestClient.Create(fixture.Factory);

        // 1) First hello — node registers in the owner's space.
        await SendHelloAsync(grpcClient, cliToken, nodeId, withHostMetadata: true);

        // 2) Owner sets a note via PATCH /api/nodes/{id}.
        using var httpClient = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var patchResp = await httpClient.PatchAsync(
            $"/api/nodes/{nodeId}",
            new StringContent(JsonSerializer.Serialize(new { note = "work laptop" }), Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, patchResp.StatusCode);

        // 3) Second hello WITH host-metadata fields (normal reconnect of a current CLI).
        await SendHelloAsync(grpcClient, cliToken, nodeId, withHostMetadata: true);
        var node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId).GetAsync();
        Assert.Equal("work laptop", node.Note);

        // 4) Third hello WITHOUT host-metadata fields (legacy CLI reconnect) — metadata is
        //    cleared by design, but the owner-set Note must still survive.
        await SendHelloAsync(grpcClient, cliToken, nodeId, withHostMetadata: false);
        node = await fixture.ClusterClient.GetGrain<INodeGrain>(nodeId).GetAsync();
        Assert.Equal("work laptop", node.Note);
        Assert.Null(node.Hostname);
    }
}

/// <summary>
/// 019: Verifies that /api/space returns the RAW stored status (not the effective presence
/// indicator) plus the serverTime + presenceStaleSeconds fields that let the frontend
/// determine online/offline without server-side pre-application of the stale rule.
/// A node that is stored as Online with a stale LastSeenAt must appear as "Online" in the
/// payload — the frontend flips it to Offline once the lastSeenAt age exceeds presenceStaleSeconds.
/// </summary>
public sealed class NodeStalePresenceTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task NodeWithStaleHeartbeat_AppearsOnlineInRawPayload_WithFreshLastSeenAt()
    {
        // Arrange: seed a node with Online status but stale LastSeenAt (simulates a
        // node that stopped heartbeating without a clean disconnect).
        var spaceId = SpaceId.New();
        var seeded = await fixture.SeedUserAsync(
            $"stale-presence-{Guid.NewGuid():N}@example.com",
            "Stale Presence Test User");
        // Use the seeded user's Space so we can call /api/space as that user.
        var nodeId = NodeId.New();
        var staleAt = DateTimeOffset.UtcNow - NodePresenceRules.StaleThreshold - TimeSpan.FromSeconds(5);

        // Register the node through SpaceGrain + NodeGrain paths so both caches are seeded.
        // NodeGrain.ConnectAsync sets Status=Online + fresh LastSeenAt; we then manually
        // back-date by writing stale state via the NodeGrain's test helper path through
        // the repository so the NodeGrain in-memory state reflects the stale scenario.
        // Simpler: use the repository directly to seed the NodeGrain state, then
        // connect through SpaceGrain so it holds the node in its membership.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
            var staleNode = new Node
            {
                Id = nodeId,
                SpaceId = new SpaceId(seeded.SpaceId),
                DisplayName = "stale-node-019",
                Status = NodeStatus.Online,
                LastSeenAt = staleAt,
                CreatedAt = staleAt,
                UpdatedAt = staleAt
            };
            await repository.UpsertNodeAsync(staleNode);
            // Register in SpaceGrain membership (the grain picks up the node on hydrate).
            var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
            await spaceGrain.RegisterNodeAsync(staleNode);
        }

        // Act: call /api/space as the seeded user (who owns this Space).
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var json = await client.GetStringAsync("/api/space");

        // Assert: node appears in the payload.
        Assert.Contains("stale-node-019", json);
        Assert.Contains(nodeId.Value, json);

        // 019: raw status is "Online" — the server does NOT pre-apply the stale rule.
        // The frontend derives Offline from (serverTime - lastSeenAt) > presenceStaleSeconds.
        // ASP.NET Core serialises anonymous-type properties with camelCase by default.
        Assert.Contains("\"status\":\"Online\"", json, StringComparison.Ordinal);

        // 019: top-level presence metadata must be present so the frontend can compute presence.
        Assert.Contains("\"serverTime\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"presenceStaleSeconds\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("90", json); // StaleThreshold = 90s
    }
}
