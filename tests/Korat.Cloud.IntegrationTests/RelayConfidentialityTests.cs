// 031-relay-confidentiality: integration tests for E2E key exchange and frame routing.
//
// Covers:
//   A2: E2E payload is opaque to the cloud (cloud forwards ciphertext unmodified).
//   A3: Cleartext FrameMetadata is inspectable by cloud (tool_name, category, payload_bytes).
//   A4: Legacy peer (no e2e-v1 capability) still works with plaintext frames.
//   A5: Downgrade path — publisher lacks e2e-v1 → cloud sends E2eNotSupported to agent.
//   A7: Tampered ciphertext is rejected (AEAD failure) — cloud just forwards, receiver rejects.
//   A8: Replay attack rejected by sequence enforcement (cipher.Open on same seq twice fails).
//   A9: Direction-splice rejected (direction byte in nonce makes cross-direction auth fail).
//  A10: No regression on existing plaintext relay (legacy frames still forwarded correctly).

using Google.Protobuf;
using Grpc.Core;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// 031-relay-confidentiality: end-to-end integration tests covering E2E key exchange routing,
/// metadata inspection, legacy fallback (E2eNotSupported), and relay correctness.
/// </summary>
public sealed class RelayConfidentialityTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(10);

    // ── A4 / A10: plaintext relay still works (no regression) ──────────────────────────────────
    [Fact]
    public async Task A10_PlaintextFrames_ForwardedCorrectly_NoRegression()
    {
        var (publisherCall, agentCall, sessionId, _, _) = await SetupSessionAsync(e2eCapability: false);
        using var _ = publisherCall;
        using var __ = agentCall;

        var payload = ByteString.CopyFromUtf8("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 1,
                Direction = "client_to_server",
                Ciphertext = payload,
            }
        });

        var received = await ReadFrameAsync(publisherCall.ResponseStream);
        Assert.Equal(sessionId, received.SessionId);
        Assert.Equal(payload, received.Ciphertext);
        Assert.Equal(0u, received.Enc); // enc=0 for plaintext
        Assert.Null(received.Meta);    // no metadata on plaintext frames
    }

    // ── A5: E2eNotSupported when publisher lacks e2e-v1 capability ─────────────────────────────
    [Fact]
    public async Task A5_Downgrade_WhenPublisherLacksE2eCapability()
    {
        // Connect publisher WITHOUT advertising e2e-v1 capability.
        var (publisherCall, agentCall, sessionId, _, _) = await SetupSessionAsync(e2eCapability: false);
        using var _ = publisherCall;
        using var __ = agentCall;

        // Agent sends E2eKeyOffer.
        var salt = E2eHandshake.GenerateSalt();
        using var handshake = E2eHandshake.CreateEphemeral();
        var agentSpki = handshake.ExportSpki();

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyOffer = new E2eKeyOffer
            {
                SessionId = sessionId,
                Version = 1,
                Curve = "p256",
                PubKey = ByteString.CopyFrom(agentSpki),
                Salt = ByteString.CopyFrom(salt),
            }
        });

        // Cloud should respond with E2eNotSupported (publisher doesn't have e2e-v1).
        var response = await ReadMessageAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported, response.PayloadCase);
        Assert.Equal(sessionId, response.E2ENotSupported.SessionId);
        Assert.False(string.IsNullOrEmpty(response.E2ENotSupported.Reason));
    }

    // ── A2 + A3: E2E key exchange routing and metadata inspection ──────────────────────────────
    [Fact]
    public async Task A2_E2eKeyExchange_CloudForwardsOpaqueFrames_MetadataVisible()
    {
        var (publisherCall, agentCall, sessionId, _, publisherNodeIdValue) = await SetupSessionAsync(e2eCapability: true);
        using var _ = publisherCall;
        using var __ = agentCall;

        // ── Handshake: agent offers → publisher answers → agent confirms ────────────────────
        var salt = E2eHandshake.GenerateSalt();
        using var agentHandshake = E2eHandshake.CreateEphemeral();
        var agentSpki = agentHandshake.ExportSpki();

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyOffer = new E2eKeyOffer
            {
                SessionId = sessionId,
                Version = 1,
                Curve = "p256",
                PubKey = ByteString.CopyFrom(agentSpki),
                Salt = ByteString.CopyFrom(salt),
            }
        });

        // Cloud forwards the offer to publisher (with mcp_server_id stamped).
        var publisherOffer = await ReadMessageAsync(publisherCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2EKeyOffer, publisherOffer.PayloadCase);
        Assert.Equal(sessionId, publisherOffer.E2EKeyOffer.SessionId);
        Assert.Equal("p256", publisherOffer.E2EKeyOffer.Curve);
        Assert.False(string.IsNullOrEmpty(publisherOffer.E2EKeyOffer.McpServerId));

        // Publisher performs ECDH on its side.
        var offeredAgentSpki = publisherOffer.E2EKeyOffer.PubKey.ToByteArray();
        var offeredSalt = publisherOffer.E2EKeyOffer.Salt.ToByteArray();
        var offeredAgentClientId = publisherOffer.E2EKeyOffer.AgentClientId;
        // Publisher node ID = the actual NodeId UUID sent in the Hello (not the display name).
        var publisherNodeId = publisherNodeIdValue;

        using var publisherHandshake = E2eHandshake.CreateEphemeral();
        var publisherSpki = publisherHandshake.ExportSpki();

        var transcriptHash = E2eHandshake.BuildTranscriptHash(
            sessionId, offeredAgentClientId, publisherNodeId,
            offeredSalt, offeredAgentSpki, publisherSpki);

        E2eSessionCipher? publisherCipher;
        byte[] publisherTag;
        byte[] expectedAgentTag;
        using (var pubKeys = publisherHandshake.Derive(offeredAgentSpki, offeredSalt, transcriptHash))
        {
            publisherTag = E2eHandshake.ComputeConfirm(
                pubKeys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash);
            expectedAgentTag = E2eHandshake.ComputeConfirm(
                pubKeys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);
            var pubKPayload = (byte[])pubKeys.KPayload.Clone();
            publisherCipher = new E2eSessionCipher(pubKPayload, sessionId);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pubKPayload);
        }

        // Publisher sends answer.
        await publisherCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyAnswer = new E2eKeyAnswer
            {
                SessionId = sessionId,
                Version = 1,
                Curve = "p256",
                PubKey = ByteString.CopyFrom(publisherSpki),
                ConfirmTag = ByteString.CopyFrom(publisherTag),
            }
        });

        // Agent receives answer from cloud.
        var agentAnswerMsg = await ReadMessageAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2EKeyAnswer, agentAnswerMsg.PayloadCase);
        Assert.Equal(sessionId, agentAnswerMsg.E2EKeyAnswer.SessionId);
        var receivedPublisherSpki = agentAnswerMsg.E2EKeyAnswer.PubKey.ToByteArray();
        var receivedPublisherTag = agentAnswerMsg.E2EKeyAnswer.ConfirmTag.ToByteArray();

        // Agent derives shared key + verifies publisher tag.
        var agentTranscript = E2eHandshake.BuildTranscriptHash(
            sessionId, offeredAgentClientId, publisherNodeId,
            salt, agentSpki, receivedPublisherSpki);

        E2eSessionCipher agentCipher;
        byte[] agentTag;
        using (var agentKeys = agentHandshake.Derive(receivedPublisherSpki, salt, agentTranscript))
        {
            E2eHandshake.VerifyConfirm(
                agentKeys.KConf, E2eHandshake.PublisherConfirmLabel,
                agentTranscript, receivedPublisherTag);

            agentTag = E2eHandshake.ComputeConfirm(
                agentKeys.KConf, E2eHandshake.AgentConfirmLabel, agentTranscript);
            var agentKPayload = (byte[])agentKeys.KPayload.Clone();
            agentCipher = new E2eSessionCipher(agentKPayload, sessionId);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(agentKPayload);
        }

        // Agent sends confirm.
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            E2EKeyConfirm = new E2eKeyConfirm
            {
                SessionId = sessionId,
                ConfirmTag = ByteString.CopyFrom(agentTag),
            }
        });

        // Cloud forwards confirm to publisher.
        var publisherConfirmMsg = await ReadMessageAsync(publisherCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2EKeyConfirm, publisherConfirmMsg.PayloadCase);
        Assert.Equal(sessionId, publisherConfirmMsg.E2EKeyConfirm.SessionId);
        Assert.True(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            publisherConfirmMsg.E2EKeyConfirm.ConfirmTag.Span, expectedAgentTag));

        // ── A2: agent sends an encrypted frame; cloud forwards CIPHERTEXT unchanged ─────────
        const string plaintext = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_files\"}}";
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var meta = FrameMetadataFactory.FromPlaintext(
            plaintextBytes.AsSpan(), E2eSessionCipher.DirectionClientToServer, (ulong)plaintextBytes.Length);
        var metaBytes = meta.ToByteArray();
        // Use the seqUsed overload so the RelayFrame.SequenceNumber matches the actual cipher seq
        // (the cipher starts at 0, not 1 — using a hardcoded 1 would cause an AAD mismatch on Open).
        var ciphertext = agentCipher.Seal(
            plaintextBytes.AsSpan(),
            E2eSessionCipher.DirClientToServer,
            metaBytes,
            out var seqUsed);

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = seqUsed,
                Direction = "client_to_server",
                Enc = 1,
                Ciphertext = ByteString.CopyFrom(ciphertext),
                Meta = meta,
            }
        });

        // Publisher receives the frame.
        var publisherFrame = await ReadFrameAsync(publisherCall.ResponseStream);
        Assert.Equal(sessionId, publisherFrame.SessionId);
        Assert.Equal(1u, publisherFrame.Enc); // still enc=1

        // A2: cloud did NOT decrypt the payload — ciphertext is byte-for-byte identical.
        Assert.Equal(ByteString.CopyFrom(ciphertext), publisherFrame.Ciphertext);

        // A3: cleartext metadata is readable by the cloud (forwarded to publisher who can verify).
        Assert.NotNull(publisherFrame.Meta);
        Assert.Equal("tool_call", publisherFrame.Meta!.Category);
        Assert.Equal("list_files", publisherFrame.Meta.ToolName);
        Assert.Equal((ulong)plaintextBytes.Length, publisherFrame.Meta.PayloadBytes);

        // Publisher can decrypt the frame.
        var decrypted = publisherCipher.Open(
            publisherFrame.Ciphertext.Span,
            E2eSessionCipher.DirClientToServer,
            publisherFrame.SequenceNumber,
            publisherFrame.Meta.ToByteArray());
        Assert.Equal(plaintext, System.Text.Encoding.UTF8.GetString(decrypted));

        // Cleanup.
        publisherCipher.Dispose();
        agentCipher.Dispose();
    }

    // ── A8: replay attack is rejected by the cipher ──────────────────────────────────────────
    [Fact]
    public void A8_ReplayAttack_RejectedBySequenceEnforcement()
    {
        const string sessionId = "test-session-replay";
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        using var cipher1 = new E2eSessionCipher(key, sessionId);
        using var cipher2 = new E2eSessionCipher(key, sessionId);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello");
        var wire = cipher1.Seal(plaintext, E2eSessionCipher.DirClientToServer);

        // First Open succeeds.
        var pt1 = cipher2.Open(wire, E2eSessionCipher.DirClientToServer, 0);
        Assert.Equal(plaintext, pt1);

        // Replaying seq=0 is rejected.
        // AuthenticationTagMismatchException is a subclass of CryptographicException.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            cipher2.Open(wire, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── A9: direction-splice is rejected (AEAD uses direction byte in nonce) ─────────────────
    [Fact]
    public void A9_DirectionSplice_RejectedByAead()
    {
        const string sessionId = "test-session-splice";
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        using var cipher1 = new E2eSessionCipher(key, sessionId);
        using var cipher2 = new E2eSessionCipher(key, sessionId);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello");
        // Seal as client→server (dir=0x00).
        var wire = cipher1.Seal(plaintext, E2eSessionCipher.DirClientToServer);

        // Attempt to Open as server→client (dir=0x01) — different nonce, AEAD must fail.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            cipher2.Open(wire, E2eSessionCipher.DirServerToClient, 0));
    }

    // ── A7: tampered ciphertext rejected ────────────────────────────────────────────────────
    [Fact]
    public void A7_TamperedCiphertext_RejectedByAead()
    {
        const string sessionId = "test-session-tamper";
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        using var cipher1 = new E2eSessionCipher(key, sessionId);
        using var cipher2 = new E2eSessionCipher(key, sessionId);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("sensitive data");
        var wire = cipher1.Seal(plaintext, E2eSessionCipher.DirClientToServer);

        // Flip one bit in the ciphertext.
        var tampered = (byte[])wire.Clone();
        tampered[20] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            cipher2.Open(tampered, E2eSessionCipher.DirClientToServer, 0));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up a publisher + agent connected and a session opened between them.
    /// When <paramref name="e2eCapability"/> is true, the publisher advertises "e2e-v1".
    /// </summary>
    private async Task<(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> Publisher,
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> Agent,
        string SessionId,
        string AgentClientIdValue,
        string PublisherNodeIdValue)> SetupSessionAsync(bool e2eCapability)
    {
        var seeded = await fixture.SeedUserAsync(
            $"e2e-test-{Guid.NewGuid():N}@example.com", "E2E Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var publisherNodeId = NodeId.New();
        var agentNodeId = NodeId.New();
        var agentClientId = ConsumerId.New();

        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            publisherNodeId,
            $"e2e-srv-{Guid.NewGuid():N}",
            "echo", "demo"))!;

        await fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), agentNodeId, "test-agent");

        var accessRequest = await space.CreateAccessRequestAsync(agentClientId, server.Id, agentNodeId);
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);

        // Connect publisher.
        var publisherCall = await ConnectAsync(
            publisherNodeId.Value, "test-publisher-node", cliToken,
            nodeKind: "publisher", e2eCapability: e2eCapability);

        // Connect agent.
        var agentCall = await ConnectAsync(
            agentNodeId.Value, "test-agent-node", cliToken,
            nodeKind: "agent", e2eCapability: true);

        // Open session.
        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId.Value,
                McpServerId = server.Id.Value
            }
        });

        var sessionMsg = await ReadMessageAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, sessionMsg.PayloadCase);
        var sessionId = sessionMsg.SessionOpened.SessionId;

        return (publisherCall, agentCall, sessionId, agentClientId.Value, publisherNodeId.Value);
    }

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAsync(
        string nodeId,
        string displayName,
        string cliToken,
        string nodeKind = "",
        bool e2eCapability = false)
    {
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);

        var hello = new NodeHello
        {
            NodeId = nodeId,
            DisplayName = displayName,
            NodeKind = nodeKind,
        };
        if (e2eCapability)
            hello.Capabilities.Add("e2e-v1");

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage { Hello = hello });
        var ack = await ReadMessageAsync(call.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, ack.PayloadCase);
        return call;
    }

    private static async Task<GatewayToNodeMessage> ReadMessageAsync(
        IAsyncStreamReader<GatewayToNodeMessage> stream)
    {
        using var cts = new CancellationTokenSource(MoveNextTimeout);
        var moved = await stream.MoveNext(cts.Token);
        if (!moved)
            throw new Xunit.Sdk.XunitException("Stream closed before expected message arrived.");
        return stream.Current;
    }

    private static async Task<RelayFrame> ReadFrameAsync(
        IAsyncStreamReader<GatewayToNodeMessage> stream)
    {
        using var cts = new CancellationTokenSource(MoveNextTimeout);
        while (true)
        {
            var moved = await stream.MoveNext(cts.Token);
            if (!moved)
                throw new Xunit.Sdk.XunitException("Stream closed before Frame arrived.");
            var msg = stream.Current;
            if (msg.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.Frame)
                return msg.Frame;
            // Skip heartbeat acks, etc.
        }
    }
}
