using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Korat.Relay.V1;
using Microsoft.Extensions.DependencyInjection;
using EntitySession = Korat.Domain.Entities.RelaySession;

namespace Korat.Cloud.IntegrationTests;

public sealed class SessionTeardownOnRevokeTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private IMetadataRepository Repo()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
    }

    [Fact]
    public async Task Session_AgentConnectionId_survives_repo_round_trip()
    {
        var repo = Repo();
        var session = new EntitySession
        {
            Id = SessionId.New(),
            SpaceId = SpaceId.New(),
            GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(),
            McpServerId = McpServerId.New(),
            ClientNodeId = NodeId.New(),
            PublisherNodeId = NodeId.New(),
            HomeGatewayId = new GatewayId("gw-1"),
            Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow,
            AgentConnectionId = new ConnectionId("conn-xyz"),
        };

        await repo.UpsertSessionAsync(session);
        var loaded = await repo.GetSessionAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal("conn-xyz", loaded!.AgentConnectionId.Value);
    }

    // ── Task 4: grain enumerate affected sessions ──────────────────────────────

    [Fact]
    public async Task RevokeGrant_returns_affected_active_session_ids()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNode, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNode, "agent");
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNode);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Open a live session against this grant (directly via the SessionGrain).
        var sessionId = SessionId.New();
        await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).OpenAsync(
            grant.Id, agentClientId, server.Id, agentNode, publisherNode,
            new GatewayId("gw"), spaceId, new ConnectionId("conn-1"));

        var affected = await space.RevokeGrantAsync(grant.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        Assert.Contains(sessionId, affected);
        var grants = await space.ListGrantsAsync();
        Assert.Equal(GrantStatus.Revoked, grants.Single(g => g.Id == grant.Id).Status);
    }

    [Fact]
    public async Task RevokeGrant_excludes_non_matching_and_closed_sessions()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNode, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNode, "agent");
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNode);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // RelaySession under a DIFFERENT grant (seed directly via repo).
        var otherGrantId = GrantId.New();
        var unrelatedSessionId = SessionId.New();
        var repo = Repo();
        await repo.UpsertSessionAsync(new EntitySession
        {
            Id = unrelatedSessionId,
            SpaceId = spaceId,
            GrantId = otherGrantId,
            ConsumerId = agentClientId,
            McpServerId = server.Id,
            ClientNodeId = agentNode,
            PublisherNodeId = publisherNode,
            HomeGatewayId = new GatewayId("gw"),
            Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow,
            AgentConnectionId = new ConnectionId(""),
        });

        // Closed session under the grant being revoked — should NOT be returned.
        var closedSessionId = SessionId.New();
        await repo.UpsertSessionAsync(new EntitySession
        {
            Id = closedSessionId,
            SpaceId = spaceId,
            GrantId = grant.Id,
            ConsumerId = agentClientId,
            McpServerId = server.Id,
            ClientNodeId = agentNode,
            PublisherNodeId = publisherNode,
            HomeGatewayId = new GatewayId("gw"),
            Status = SessionStatus.Closed,
            StartedAt = DateTimeOffset.UtcNow,
            AgentConnectionId = new ConnectionId(""),
        });

        var affected = await space.RevokeGrantAsync(grant.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        Assert.DoesNotContain(unrelatedSessionId, affected);
        Assert.DoesNotContain(closedSessionId, affected);
    }

    [Fact]
    public async Task DeleteMcpServer_revokes_grants_and_returns_sessions()
    {
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNode, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNode, "agent");
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNode);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        var sessionId = SessionId.New();
        await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).OpenAsync(
            grant.Id, agentClientId, server.Id, agentNode, publisherNode,
            new GatewayId("gw"), spaceId, new ConnectionId("conn-1"));

        var result = await space.DeleteMcpServerAsync(server.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        Assert.Contains(sessionId, result.AffectedSessionIds);
        Assert.True(result.Deleted);
        // No Active grant rows remain for the deleted server.
        var grants = await space.ListGrantsAsync();
        Assert.DoesNotContain(grants, g => g.McpServerId == server.Id && g.Status == GrantStatus.Active);
    }

    // ── Task 6: delete endpoint wiring — terminator closes the live session ─────

    [Fact]
    public async Task DeleteEndpoint_path_revokes_grants_and_closes_sessions()
    {
        // Arrange — seed space / server / agent / grant / session (mirrors Task 5 test).
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNode, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNode, "agent");
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNode);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        var sessionId = SessionId.New();
        await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).OpenAsync(
            grant.Id, agentClientId, server.Id, agentNode, publisherNode,
            new GatewayId("gw"), spaceId, new ConnectionId("conn-t6"));

        // Resolve the singletons the endpoint uses.
        using var scope = fixture.Factory.Services.CreateScope();
        var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();

        // Act — replicate what the endpoint body does: delete the server (revokes grants +
        // enumerates affected sessions), then terminate each one via SessionTerminator.
        var result = await space.DeleteMcpServerAsync(server.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);
        foreach (var sid in result.AffectedSessionIds)
            await terminator.TerminateSessionAsync(sid, SessionCloseReason.ServerUnavailable, default);

        // Assert — the session grain is now Closed with reason ServerUnavailable.
        var closed = await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).GetAsync();
        Assert.Equal(SessionStatus.Closed, closed.Status);
        Assert.Equal(SessionCloseReason.ServerUnavailable, closed.CloseReason);
        // No Active grant rows remain for the deleted server.
        var grants = await space.ListGrantsAsync();
        Assert.DoesNotContain(grants, g => g.McpServerId == server.Id && g.Status == GrantStatus.Active);
    }

    // ── Task 7: defense-in-depth — re-check grant before OpenSession ─────────

    /// <summary>
    /// Simulates the revoke-during-open race: the grant is Active when the gateway first
    /// resolves it (GetActiveGrantAsync ~line 823), but is revoked before OpenSession. The
    /// re-check added by Task 7 must detect the revocation and respond with AccessDenied
    /// (not SessionOpened).
    ///
    /// In the test-host this is not a true race — we revoke synchronously before the agent
    /// sends RequestSession, which means the single GetActiveGrantAsync the gateway makes
    /// (after revoke) already returns null. That is the degenerate and simplest case of the
    /// race: any revoke that completes before RequestSession arrives is caught by the re-check.
    /// A follow-up stress test (not in Step A scope) would revoke concurrently.
    /// </summary>
    [Fact]
    public async Task OpenSession_after_revoke_is_denied()
    {
        // Seed user + CLI token (mirrors ConnectAccessRequestTests / RelayFrameForwardingTests).
        var seeded = await fixture.SeedUserAsync(
            $"t7-revoke-{Guid.NewGuid():N}@example.com", "T7 Revoke Race Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        // Publish a server and mark the publisher node Online (admission gate requirement).
        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(
            publisherNode, $"t7-srv-{Guid.NewGuid():N}", "echo", "x"))!;

        await fixture.ClusterClient.GetGrain<INodeGrain>(publisherNode.Value)
            .ConnectAsync(spaceId, "t7-publisher", new GatewayId("test-gateway"));

        // Register the agent-client bound to a specific node.
        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNodeId, "t7-agent");

        // Approve the access request so an Active grant now exists.
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // ── Act: revoke the grant BEFORE the agent sends RequestSession ──────────
        await space.RevokeGrantAsync(grant.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        // Open the agent gRPC stream.
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var agentCall = grpcClient.Connect(callOptions);
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = agentNodeId.Value,
                DisplayName = "t7-agent",
                NodeKind = "agent",
            }
        });

        // Drain the Hello ack.
        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await agentCall.ResponseStream.MoveNext(helloCts.Token);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, agentCall.ResponseStream.Current.PayloadCase);

        // Send RequestSession for the server whose grant is now Revoked.
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = "t7-req-1",
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value,
            }
        });

        // ── Assert: the gateway must NOT open a session — expect AccessDenied or AccessPending,
        // NOT SessionOpened.
        using var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await agentCall.ResponseStream.MoveNext(responseCts.Token);
        var response = agentCall.ResponseStream.Current;

        Assert.NotEqual(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, response.PayloadCase);

        await agentCall.RequestStream.CompleteAsync();
    }

    // ── Task 5: revoke endpoint wiring — terminator closes the live session ────

    [Fact]
    public async Task RevokeEndpoint_path_closes_the_live_session()
    {
        // Arrange — seed space / server / agent / grant / session (mirrors Task 4 tests).
        var spaceId = SpaceId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        var publisherNode = NodeId.New();
        var server = (await space.PublishMcpServerAsync(publisherNode, $"srv-{Guid.NewGuid():N}", "echo", "x"))!;
        var agentClientId = ConsumerId.New();
        var agentNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNode, "agent");
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNode);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        var sessionId = SessionId.New();
        await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).OpenAsync(
            grant.Id, agentClientId, server.Id, agentNode, publisherNode,
            new GatewayId("gw"), spaceId, new ConnectionId("conn-t5"));

        // Resolve the singletons the endpoint uses.
        using var scope = fixture.Factory.Services.CreateScope();
        var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();

        // Act — replicate what the endpoint body does: flip the grant + enumerate affected sessions,
        // then terminate each one via the SessionTerminator (exactly what Task 5 wires).
        var affected = await space.RevokeGrantAsync(grant.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);
        foreach (var sid in affected)
            await terminator.TerminateSessionAsync(sid, SessionCloseReason.Revoked, default);

        // Assert — the session grain is now Closed with reason Revoked.
        var closed = await fixture.ClusterClient.GetGrain<ISessionGrain>(sessionId.Value).GetAsync();
        Assert.Equal(SessionStatus.Closed, closed.Status);
        Assert.Equal(SessionCloseReason.Revoked, closed.CloseReason);
    }

    // ── Task 8: end-to-end relay session → revoke → both ends closed + frame rejected ──

    /// <summary>
    /// Capstone e2e proof: a LIVE relay session (real gRPC streams through the in-proc
    /// gateway + NullRelayBackplane) is torn down when the grant is revoked.
    ///
    /// Asserts all three points in the plan:
    ///   (1) CloseSession{Revoked} delivered to the AGENT stream.
    ///   (2) CloseSession{...} delivered to the PUBLISHER stream.
    ///   (3) A frame sent by the agent AFTER revoke is NOT delivered to the publisher
    ///       (route evicted → ForwardFrameAsync returns false).
    /// </summary>
    [Fact]
    public async Task LiveSession_Revoke_ClosesBothEnds_AndRejectsSubsequentFrame()
    {
        // ── Arrange: seed user / space / server / grant ──────────────────────────
        var seeded = await fixture.SeedUserAsync(
            $"t8-revoke-{Guid.NewGuid():N}@example.com", "T8 Live Revoke Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var spaceId = new SpaceId(seeded.SpaceId);
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId.Value);

        // Publisher gRPC stream is opened first; HandleHelloAsync calls NodeGrain.ConnectAsync
        // which sets the publisher node Online — required for the admission gate check before
        // OpenSession (NodeStatus.Offline → AccessDenied without publishing).
        var publisherNodeId = NodeId.New();
        var server = (await space.PublishMcpServerAsync(
            publisherNodeId,
            $"t8-srv-{Guid.NewGuid():N}",
            "echo",
            "demo"))!;

        // Register the agent-client grain so the gateway can validate it against the space.
        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(spaceId, agentNodeId, "t8-agent");

        // Approve the access request so an Active grant exists.
        var ar = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        var grant = await space.ApproveAccessRequestAsync(ar.Id, seeded.UserId);

        // ── Open both gRPC streams (publisher first so the node is Online) ───────
        using var publisherCall = await T8ConnectAsync(publisherNodeId.Value, "t8-publisher", cliToken);
        using var agentCall    = await T8ConnectAsync(agentNodeId.Value,    "t8-agent",     cliToken, nodeKind: "agent");

        // ── Agent requests a session — expect SessionOpened ───────────────────────
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId    = "t8-req-1",
                AgentClientId = agentClientId.Value,
                McpServerId   = server.Id.Value,
            }
        });

        var sessionOpened = await T8ReadUntilAsync(agentCall,
            m => m.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.SessionOpened,
            "SessionOpened");
        var sessionId = sessionOpened.SessionOpened.SessionId;
        Assert.False(string.IsNullOrEmpty(sessionId), "Expected a non-empty session id from SessionOpened");

        // ── Exchange one frame to prove the session is live ───────────────────────
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId      = sessionId,
                SequenceNumber = 1,
                Direction      = "client_to_server",
                Ciphertext     = ByteString.CopyFromUtf8("ping"),
            }
        });
        var pingAtPub = await T8ReadUntilAsync(publisherCall,
            m => m.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.Frame,
            "Frame(ping)");
        Assert.Equal(sessionId, pingAtPub.Frame.SessionId);

        // ── Act: revoke the grant → SessionTerminator closes both ends ────────────
        // Replicate the endpoint body: flip grant + enumerate affected session ids, then
        // terminate each.  This is the same code path that the revoke HTTP endpoint uses.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var terminator = scope.ServiceProvider.GetRequiredService<SessionTerminator>();
            var affected = await space.RevokeGrantAsync(grant.Id, seeded.UserId);
            foreach (var sid in affected)
                await terminator.TerminateSessionAsync(sid, SessionCloseReason.Revoked, default);
        }

        // ── Assert (1): agent stream receives CloseSession{Revoked} ──────────────
        var agentClose = await T8ReadUntilAsync(agentCall,
            m => m.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.CloseSession,
            "CloseSession@agent");
        Assert.Equal(sessionId, agentClose.CloseSession.SessionId);
        Assert.Equal("Revoked", agentClose.CloseSession.Reason);

        // ── Assert (2): publisher stream receives CloseSession ────────────────────
        var pubClose = await T8ReadUntilAsync(publisherCall,
            m => m.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.CloseSession,
            "CloseSession@publisher");
        Assert.Equal(sessionId, pubClose.CloseSession.SessionId);
        Assert.Equal("Revoked", pubClose.CloseSession.Reason);

        // ── Assert (3): a frame sent AFTER revoke is not delivered ────────────────
        // The route was evicted in step 3 of TerminateSessionAsync (CloseSession call),
        // so ForwardFrameAsync will find no route → return false → frame dropped.
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId      = sessionId,
                SequenceNumber = 2,
                Direction      = "client_to_server",
                Ciphertext     = ByteString.CopyFromUtf8("late"),
            }
        });

        // Give the gateway 2 s to forward the late frame — it must NOT arrive.
        var lateFrameArrived = await T8TryReadFrameAsync(publisherCall, sessionId, TimeSpan.FromSeconds(2));
        Assert.False(lateFrameArrived, "Late frame (sent after revoke) must not be delivered to the publisher");

        // ── Assert (4): session grain is Closed{Revoked} ─────────────────────────
        var grainState = await fixture.ClusterClient
            .GetGrain<ISessionGrain>(sessionId).GetAsync();
        Assert.Equal(SessionStatus.Closed, grainState.Status);
        Assert.Equal(SessionCloseReason.Revoked, grainState.CloseReason);

        await agentCall.RequestStream.CompleteAsync();
        await publisherCall.RequestStream.CompleteAsync();
    }

    // ── T8 helpers (scoped to this file) ─────────────────────────────────────────

    private static readonly TimeSpan T8Timeout = TimeSpan.FromSeconds(10);

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>>
        T8ConnectAsync(string nodeId, string displayName, string cliToken, string nodeKind = "")
    {
        var grpcClient  = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call        = grpcClient.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId      = nodeId,
                DisplayName = displayName,
                NodeKind    = nodeKind,
            }
        });

        // Drain the Hello ack.
        using var cts = new CancellationTokenSource(T8Timeout);
        var moved = await call.ResponseStream.MoveNext(cts.Token);
        Assert.True(moved, $"Stream for {displayName} closed before Hello ack");
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello,
            call.ResponseStream.Current.PayloadCase);

        return call;
    }

    /// <summary>
    /// Drain messages from <paramref name="call"/> until one matches <paramref name="predicate"/>
    /// or the timeout expires. Skips messages that don't match so intermediate control messages
    /// (heartbeat acks, etc.) don't stall the assertion.
    /// </summary>
    private static async Task<GatewayToNodeMessage> T8ReadUntilAsync(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call,
        Func<GatewayToNodeMessage, bool> predicate,
        string label)
    {
        using var cts = new CancellationTokenSource(T8Timeout);
        while (true)
        {
            var moved = await call.ResponseStream.MoveNext(cts.Token);
            if (!moved)
                throw new Xunit.Sdk.XunitException($"Stream closed before expected message '{label}' arrived.");
            var msg = call.ResponseStream.Current;
            if (predicate(msg))
                return msg;
            // Skip — e.g. a HeartbeatAck or earlier frame — keep draining.
        }
    }

    /// <summary>
    /// Try to read a Frame for <paramref name="sessionId"/> from the publisher call within
    /// <paramref name="timeout"/>. Returns true if one arrives, false on timeout/cancel.
    /// Used to assert that a late frame is NOT forwarded after revoke.
    /// </summary>
    private static async Task<bool> T8TryReadFrameAsync(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call,
        string sessionId,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var moved = await call.ResponseStream.MoveNext(cts.Token);
                if (!moved)
                    return false; // stream closed — no frame
                var msg = call.ResponseStream.Current;
                if (msg.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.Frame
                    && msg.Frame.SessionId == sessionId)
                    return true; // a frame for this session arrived — unexpected
                // Any other message (e.g. CloseSession duplicate, control) — keep polling.
            }
        }
        catch (OperationCanceledException)
        {
            return false; // timeout — no frame delivered within the window (expected)
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Cancelled)
        {
            // gRPC.Net wraps OperationCanceledException in RpcException(Cancelled) when the
            // CancellationToken passed to MoveNext fires.
            return false;
        }
    }
}
