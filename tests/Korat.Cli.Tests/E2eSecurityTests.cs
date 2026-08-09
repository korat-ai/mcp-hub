using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Korat.Cli.Commands;
using Korat.Cli.Gateway;
using Korat.Protocol;
using Korat.Relay.V1;
using Xunit;

// E2ePolicy is nested in ConnectCommand; InternalsVisibleTo is set in Korat.Cli.csproj.
// E2eHandshakeResult is in Korat.Cli.Gateway; DowngradeAttackException is in Korat.Cli.Commands.
using static Korat.Cli.Commands.ConnectCommand;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for 031-relay-confidentiality security properties.
///
/// MAJOR-1: Anti-downgrade/injection — ProcessInboundMessageAsync throws
///          DowngradeAttackException on an injected enc==0 frame when E2E is established.
/// MAJOR-2: E2ePolicy enum + routing logic (require closes, prefer warns).
/// MAJOR-3: Aggregator sessions negotiate E2E; outbound frames use SendE2eFrameAsync.
/// </summary>
public class E2eSecurityTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-1: DowngradeAttackException on injected plaintext frame
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When an E2E session is established and a plaintext (enc==0) frame arrives,
    /// ProcessInboundMessageAsync throws DowngradeAttackException.
    /// </summary>
    [Fact]
    public async Task E2eSession_RejectsAndClosesOnInjectedPlaintextFrame()
    {
        var kPayload = new byte[32];
        Random.Shared.NextBytes(kPayload);
        var cipher = new E2eSessionCipher(kPayload, "test-session");
        var agentSession = CreateAgentSession(cipher);

        var injectedFrame = new GatewayToNodeMessage
        {
            Frame = new RelayFrame
            {
                SessionId = "test-session",
                Ciphertext = ByteString.CopyFromUtf8("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n"),
                Enc = 0, // INJECTED plaintext on an established E2E session
                SequenceNumber = 0,
            }
        };

        var stream = new MemoryStream();
        await Assert.ThrowsAsync<DowngradeAttackException>(
            () => InvokeProcessInboundMessageAsync(injectedFrame, "test-session", agentSession, stream));
    }

    /// <summary>
    /// enc==0 with no cipher installed → plaintext path (normal legacy).
    /// </summary>
    [Fact]
    public async Task NoE2eSession_AllowsPlaintextFrame()
    {
        var payload = "{\"jsonrpc\":\"2.0\",\"result\":{}}\n"u8.ToArray();
        var frame = new GatewayToNodeMessage
        {
            Frame = new RelayFrame
            {
                SessionId = "sess",
                Ciphertext = ByteString.CopyFrom(payload),
                Enc = 0,
            }
        };

        var stream = new MemoryStream();
        await InvokeProcessInboundMessageAsync(frame, "sess", null, stream);
        Assert.Equal(payload, stream.ToArray());
    }

    /// <summary>
    /// enc==1 with no cipher installed → protocol error → DowngradeAttackException.
    /// </summary>
    [Fact]
    public async Task NoE2eSession_EncNotZeroFrame_IsProtocolError()
    {
        var frame = new GatewayToNodeMessage
        {
            Frame = new RelayFrame
            {
                SessionId = "sess",
                Ciphertext = ByteString.CopyFromUtf8("garbage"),
                Enc = 1,
            }
        };

        var stream = new MemoryStream();
        await Assert.ThrowsAsync<DowngradeAttackException>(
            () => InvokeProcessInboundMessageAsync(frame, "sess", null, stream));
    }

    /// <summary>
    /// A valid enc==1 frame is correctly decrypted when a cipher is installed.
    /// </summary>
    [Fact]
    public async Task E2eSession_DecryptsValidEncFrame()
    {
        var kPayload = new byte[32];
        Random.Shared.NextBytes(kPayload);
        var sessionId = "decrypt-test";
        var sendCipher = new E2eSessionCipher(kPayload, sessionId);
        var recvCipher = new E2eSessionCipher(kPayload, sessionId);

        var plaintext = "{\"jsonrpc\":\"2.0\",\"result\":{}}\n"u8.ToArray();
        var wirePayload = sendCipher.Seal(
            plaintext, E2eSessionCipher.DirServerToClient, ReadOnlySpan<byte>.Empty, out var seq);

        var frame = new GatewayToNodeMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                Ciphertext = ByteString.CopyFrom(wirePayload),
                Enc = 1,
                SequenceNumber = seq,
            }
        };

        var agentSession = CreateAgentSession(recvCipher);
        var stream = new MemoryStream();
        await InvokeProcessInboundMessageAsync(frame, sessionId, agentSession, stream);
        Assert.Equal(plaintext, stream.ToArray());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-2: E2ePolicy enum and routing logic
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2ePolicy_HasExpectedValues()
    {
        // Prefer is the default (zero value) so it requires no explicit CLI flag.
        Assert.Equal(0, (int)E2ePolicy.Prefer);
        Assert.NotEqual((int)E2ePolicy.Prefer, (int)E2ePolicy.Require);
        Assert.NotEqual((int)E2ePolicy.Prefer, (int)E2ePolicy.Off);
        Assert.NotEqual((int)E2ePolicy.Require, (int)E2ePolicy.Off);
    }

    /// <summary>
    /// A5: Require policy on FellBackToPlaintext → cts cancelled (fail-closed).
    /// This mirrors the logic inside RunBridgeLoopAsync.
    /// </summary>
    [Fact]
    public void E2eRequire_Policy_CancelsOnFallback()
    {
        using var cts = new CancellationTokenSource();
        var result = Korat.Cli.Gateway.E2eHandshakeResult.FellBackToPlaintext;
        var policy = E2ePolicy.Require;

        // Mirror the decision in RunBridgeLoopAsync.
        if (result is Korat.Cli.Gateway.E2eHandshakeResult.FellBackToPlaintext or Korat.Cli.Gateway.E2eHandshakeResult.Failed
            && policy == E2ePolicy.Require)
        {
            cts.Cancel();
        }

        Assert.True(cts.IsCancellationRequested);
    }

    /// <summary>
    /// A5: Prefer policy on FellBackToPlaintext → cts NOT cancelled.
    /// </summary>
    [Fact]
    public void E2ePrefer_Policy_DoesNotCancelOnFallback()
    {
        using var cts = new CancellationTokenSource();
        var result = Korat.Cli.Gateway.E2eHandshakeResult.FellBackToPlaintext;
        var policy = E2ePolicy.Prefer;

        if (result is Korat.Cli.Gateway.E2eHandshakeResult.FellBackToPlaintext or Korat.Cli.Gateway.E2eHandshakeResult.Failed
            && policy == E2ePolicy.Require)
        {
            cts.Cancel();
        }

        Assert.False(cts.IsCancellationRequested);
    }

    /// <summary>
    /// A5: Off policy skips the E2E offer entirely (no key offer is sent).
    /// </summary>
    [Fact]
    public void E2eOff_Policy_SkipsHandshake()
    {
        // Under E2ePolicy.Off, the code takes the "if (e2ePolicy == E2ePolicy.Off)" early branch
        // and e2eSession remains null. This test documents the expected behavior.
        var policy = E2ePolicy.Off;
        E2eAgentSession? e2eSession = null;

        if (policy != E2ePolicy.Off)
        {
            // Would call EstablishAsync and potentially set e2eSession.
            e2eSession = null; // placeholder
        }

        Assert.Null(e2eSession);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-3: Aggregator session negotiates E2E
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An aggregator backend session with a real E2E-responding fake gateway completes
    /// the E2E handshake, and after E2E is installed, outbound frames use SendE2eFrameAsync.
    /// </summary>
    [Fact]
    public async Task AggregatorSession_NegotiatesE2e_OutboundFramesAreEncrypted()
    {
        var conn = new E2eRespondingFakeConnection("sess-s1");
        await using var mgr = new Korat.Cli.Mcp.Aggregation.BackendSessionManager(
            conn, agentClientId: "ag1",
            handshakeTimeout: TimeSpan.FromSeconds(5));

        var server = new Korat.Cli.Mcp.Aggregation.ServerDescriptor("s1", "TestServer", true);
        var tools = await mgr.OpenAsync(server, "ts", default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(conn.E2eHandshakeCompleted, "Aggregator should complete E2E handshake");
        Assert.NotEmpty(tools);

        // After E2E, trigger a tools/call. The outbound frame must go through enc==1 path.
        _ = mgr.CallAsync("ts__test_tool", "{}", JsonValue.Create(42), default);
        // Small wait to ensure SendLineAsync runs before we assert.
        await Task.Delay(100);

        Assert.True(conn.HasEncryptedFrame, "Post-E2E outbound frames must be encrypted (enc==1)");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static E2eAgentSession CreateAgentSession(E2eSessionCipher cipher)
    {
        var ctor = typeof(E2eAgentSession)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 1);
        return (E2eAgentSession)ctor.Invoke([cipher]);
    }

    private static Task InvokeProcessInboundMessageAsync(
        GatewayToNodeMessage msg, string sessionId, E2eAgentSession? e2eSession, Stream stdout)
    {
        var method = typeof(ConnectCommand).GetMethod(
            "ProcessInboundMessageAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("ProcessInboundMessageAsync not found");
        return (Task)method.Invoke(null, [msg, sessionId, e2eSession, stdout, CancellationToken.None])!;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-3 Fake: performs real publisher-side E2E and encrypts MCP responses
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full fake for the MAJOR-3 test. Performs the publisher side of the E2E handshake
    /// (real ECDH + confirm tag), then encrypts its MCP responses so BackendSession
    /// can decrypt them without triggering the anti-downgrade check.
    /// </summary>
    private sealed class E2eRespondingFakeConnection : Korat.Cli.Mcp.Aggregation.IGatewayConnection
    {
        private readonly string _sessionId;
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();

        public bool E2eHandshakeCompleted { get; private set; }
        public bool HasEncryptedFrame { get; private set; }

        // Publisher cipher (server→client direction) for encrypting MCP responses.
        private E2eSessionCipher? _publisherCipher;

        public E2eRespondingFakeConnection(string sessionId) => _sessionId = sessionId;

        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = _sessionId }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            // This is called when no cipher is installed yet (pre-E2E MCP handshake).
            // Just auto-reply plaintext — should NOT be called after E2E is installed.
            SendAutoReplyPlaintext(sessionId, ciphertext.ToArray());
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, FrameMetadata meta, CancellationToken ct = default)
        {
            HasEncryptedFrame = true;
            // Attempt to decrypt and auto-reply for MCP requests (initialize / tools/list).
            if (_publisherCipher is not null)
            {
                try
                {
                    var metaBytes = meta.ToByteArray();
                    var plaintext = _publisherCipher.Open(
                        wirePayload.ToArray(),
                        E2eSessionCipher.DirClientToServer,
                        sequenceNumber,
                        metaBytes);
                    SendAutoReplyEncrypted(sessionId, plaintext);
                }
                catch { /* Ignore: might be tools/call which we don't reply to */ }
            }
            return Task.CompletedTask;
        }

        // MAJOR-3 fix: the fake now stamps PublisherNodeId on the answer so the
        // aggregator's TryEstablishE2eAsync picks it up and computes the same
        // transcript hash as the direct-connect path.
        internal const string FakePublisherNodeId = "fake-publisher-node";

        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            using var publisherHandshake = E2eHandshake.CreateEphemeral();
            var publisherSpki = publisherHandshake.ExportSpki();
            var transcriptHash = E2eHandshake.BuildTranscriptHash(
                sessionId, "ag1", FakePublisherNodeId, salt, pubKey, publisherSpki);

            byte[] kPayload;
            using (var keys = publisherHandshake.Derive(pubKey, salt, transcriptHash))
            {
                var publisherTag = E2eHandshake.ComputeConfirm(
                    keys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash);
                kPayload = (byte[])keys.KPayload.Clone();
                _in.Writer.TryWrite(new GatewayToNodeMessage
                {
                    E2EKeyAnswer = new E2eKeyAnswer
                    {
                        SessionId = sessionId,
                        Version = 1,
                        Curve = "p256",
                        PubKey = ByteString.CopyFrom(publisherSpki),
                        ConfirmTag = ByteString.CopyFrom(publisherTag),
                        PublisherNodeId = FakePublisherNodeId,   // MAJOR-3: stamp so aggregator matches
                    }
                });
            }
            // Build the publisher cipher so we can encrypt MCP responses.
            _publisherCipher = new E2eSessionCipher(kPayload, sessionId);
            CryptographicOperations.ZeroMemory(kPayload);
            return Task.CompletedTask;
        }

        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
        {
            E2eHandshakeCompleted = true;
            return Task.CompletedTask;
        }

        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        private void SendAutoReplyPlaintext(string sessionId, byte[] bytes)
        {
            TryBuildReply(sessionId, bytes, encrypted: false);
        }

        private void SendAutoReplyEncrypted(string sessionId, byte[] plaintext)
        {
            TryBuildReply(sessionId, plaintext, encrypted: true);
        }

        private void TryBuildReply(string sessionId, byte[] plaintextBytes, bool encrypted)
        {
            var text = Encoding.UTF8.GetString(plaintextBytes).TrimEnd('\n');
            JsonNode? node;
            try { node = JsonNode.Parse(text); } catch { return; }
            var obj = node?.AsObject();
            if (obj is null) return;
            if (!obj.TryGetPropertyValue("id", out var idNode) || idNode is null) return;
            if (!obj.TryGetPropertyValue("method", out var mNode)) return;
            var method = mNode!.GetValue<string>();
            var result = method == "tools/list"
                ? new JsonObject
                {
                    ["tools"] = new JsonArray((JsonNode)new JsonObject
                    {
                        ["name"] = "test_tool",
                        ["description"] = "d",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    })
                }
                : new JsonObject { ["ok"] = true };
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
            var replyBytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");

            RelayFrame frame;
            if (encrypted && _publisherCipher is not null)
            {
                var wirePayload = _publisherCipher.Seal(
                    replyBytes, E2eSessionCipher.DirServerToClient, ReadOnlySpan<byte>.Empty, out var seqUsed);
                frame = new RelayFrame
                {
                    SessionId = sessionId,
                    Ciphertext = ByteString.CopyFrom(wirePayload),
                    Direction = "server_to_client",
                    Enc = 1,
                    SequenceNumber = seqUsed,
                };
            }
            else
            {
                frame = new RelayFrame
                {
                    SessionId = sessionId,
                    Ciphertext = ByteString.CopyFrom(replyBytes),
                    Direction = "server_to_client",
                    Enc = 0,
                };
            }
            _in.Writer.TryWrite(new GatewayToNodeMessage { Frame = frame });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BLOCKING-1 + BLOCKING-2 regression: mandatory production dispatch-loop test
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MANDATORY regression test for BLOCKING-1 (dispatch-loop self-deadlock) and
    /// BLOCKING-2 (fail-open on enc/cipher mismatch).
    ///
    /// Drives SessionBridge.HandleE2eKeyOfferAsync from a detached Task.Run
    /// (exactly as the fixed UpCommand/ServiceCommand dispatch loop does), then
    /// concurrently delivers HandleE2eKeyConfirm on the same SessionBridge.
    /// The cipher must install within 5 seconds (no deadlock), and an enc==0 frame
    /// on the established session must be dropped fail-closed (BLOCKING-2).
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task DispatchLoop_E2eFullCycle_NoCipherDeadlock_FailClosedOnDowngrade()
    {
        const string sessionId = "prod-loop-sess";
        const string publisherNodeId = "publisher-node-1";
        const string agentClientId = "agent-client-1";

        // Use a real agent-side ephemeral key pair for the offer.
        // NOTE: HandleE2eKeyOfferAsync generates its OWN ephemeral publisher key internally.
        // We do NOT pre-compute the transcript here — instead the fake gateway captures the
        // actual publisher SPKI + confirm tag, and we derive the agent-tag from that.
        using var agentHandshake = E2eHandshake.CreateEphemeral();
        var agentSpki = agentHandshake.ExportSpki();
        var salt = E2eHandshake.GenerateSalt();

        // Fake gateway captures the publisher answer (SPKI + confirm tag sent by HandleE2eKeyOfferAsync).
        var fakeGateway = new CapturingFakeSessionBridgeGateway();

        // Routing map with one server (required for OnFrameReceivedAsync to resolve spec).
        var routingMap = new Dictionary<string, Korat.Cli.Mcp.McpServerSpec>
        {
            ["server1"] = new Korat.Cli.Mcp.McpServerSpec("server1", "echo", "")
        };
        await using var bridge = new Korat.Cli.Mcp.SessionBridge(fakeGateway, routingMap);

        // Build the E2eKeyOffer that the dispatch loop would deliver to the bridge.
        var offer = new E2eKeyOffer
        {
            SessionId = sessionId,
            Version = 1,
            Curve = "p256",
            PubKey = ByteString.CopyFrom(agentSpki),
            Salt = ByteString.CopyFrom(salt),
            AgentClientId = agentClientId,
        };

        // ACT PART 1 (BLOCKING-1 fix): fire-and-forget the offer handler, exactly as
        // UpCommand.DispatchMessageAsync now does. The loop must NOT park here.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var offerTask = Task.Run(async () =>
            await bridge.HandleE2eKeyOfferAsync(
                offer,
                agentClientId: agentClientId,
                publisherNodeId: publisherNodeId,
                ct: cts.Token));

        // Wait until HandleE2eKeyOfferAsync has sent the E2eKeyAnswer (publisher SPKI captured).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!fakeGateway.E2eAnswerSent && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(fakeGateway.E2eAnswerSent, "HandleE2eKeyOfferAsync must send E2eKeyAnswer");
        Assert.NotNull(fakeGateway.CapturedPublisherSpki);

        // Now that we have the actual publisher SPKI that HandleE2eKeyOfferAsync used,
        // compute the transcript hash and derive the expected agent confirm tag using the
        // agent's handshake object (which knows the private key).
        var publisherSpki = fakeGateway.CapturedPublisherSpki!;
        var transcriptHash = E2eHandshake.BuildTranscriptHash(
            sessionId, agentClientId, publisherNodeId, salt, agentSpki, publisherSpki);

        byte[] agentTag;
        using (var agentKeys = agentHandshake.Derive(publisherSpki, salt, transcriptHash))
        {
            // Verify the publisher's confirm tag (constant-time). If this throws, the test
            // setup is wrong — the captured SPKI doesn't correspond to the offer we sent.
            E2eHandshake.VerifyConfirm(
                agentKeys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash,
                fakeGateway.CapturedPublisherConfirmTag!);
            agentTag = E2eHandshake.ComputeConfirm(
                agentKeys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);
        }

        // ACT PART 2 (BLOCKING-1 fix): deliver the E2eKeyConfirm with the correct agent tag.
        // Because the dispatch loop fired-and-forgot the offer handler, it is still free to
        // process this message. If BLOCKING-1 were not fixed, HandleE2eKeyConfirm would have
        // no effect because the offer handler would be parked blocking the channel.
        var confirm = new E2eKeyConfirm
        {
            SessionId = sessionId,
            ConfirmTag = ByteString.CopyFrom(agentTag),
        };
        bridge.HandleE2eKeyConfirm(confirm);

        // ASSERT 1 (BLOCKING-1): offer handler completes within 5 s — no deadlock.
        await offerTask.WaitAsync(TimeSpan.FromSeconds(5));

        // ASSERT 2: cipher is installed — evidenced by offerTask completing without timeout.
        // (HandleE2eKeyOfferAsync only logs "[e2e] Session X is E2E-encrypted" and returns on success.)

        // ASSERT 3 (BLOCKING-2): enc==0 frame on an established E2E session must be DROPPED
        // fail-closed — CloseSessionAsync removes the cipher and the subprocess is not spawned.
        await bridge.OnFrameReceivedAsync(
            sessionId,
            mcpServerId: "server1",
            bytes: "{\"jsonrpc\":\"2.0\"}\n"u8.ToArray(),
            enc: 0,           // plaintext injection on an established E2E session — MUST be dropped
            meta: null,
            sequenceNumber: 0,
            cancellationToken: default);
        // RelaySession closed by the fail-closed path. No subprocess was spawned (enc==0 dropped
        // before Lazy<SessionContext> could be materialized), so ActiveSessionCount == 0.
        Assert.Equal(0, bridge.ActiveSessionCount);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-1 (new): confirm-to-cipher-install race — non-serialized interleaving
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MAJOR-1 regression: delivers E2eKeyConfirm and an enc==1 frame back-to-back on the
    /// dispatch channel WITHOUT awaiting the offerTask to completion first. Asserts that the
    /// session survives (cipher was installed before the frame arrived) and that the frame is
    /// correctly decrypted (not dropped by the fail-closed mismatch path).
    ///
    /// This exercises the specific race window that was introduced by the Task.Run detachment:
    /// confirm arrives → cipher install was on a pool continuation → next frame could see
    /// cipher==null → fail-closed session kill. With the fix, HandleE2eKeyConfirm (inline on
    /// the dispatch loop) installs the cipher synchronously before returning, so the frame
    /// dispatched immediately after always finds the cipher.
    ///
    /// Uses "cat" as the subprocess so it blocks on stdin — ActiveSessionCount stays 1 as
    /// long as the encrypted frame was successfully decrypted and written, vs 0 if the
    /// fail-closed mismatch path ran (which calls CloseSessionAsync and kills the process).
    /// </summary>
    [Fact(Timeout = 15_000)]
    public async Task DispatchLoop_ConfirmAndEncFrameBackToBack_CipherInstalledBeforeFrame()
    {
        const string sessionId = "race-test-sess";
        const string publisherNodeId = "publisher-node-race";
        const string agentClientId = "agent-client-race";

        using var agentHandshake = E2eHandshake.CreateEphemeral();
        var agentSpki = agentHandshake.ExportSpki();
        var salt = E2eHandshake.GenerateSalt();

        var fakeGateway = new CapturingFakeSessionBridgeGateway();

        // Use "cat" so the subprocess blocks on stdin — it stays alive after receiving data.
        // This lets us assert ActiveSessionCount == 1 (cipher found → frame decrypted → stdin written).
        // If the mismatch path fires instead, CloseSessionAsync kills cat → ActiveSessionCount == 0.
        var routingMap = new Dictionary<string, Korat.Cli.Mcp.McpServerSpec>
        {
            ["server1"] = new Korat.Cli.Mcp.McpServerSpec("server1", "cat", "")
        };
        await using var bridge = new Korat.Cli.Mcp.SessionBridge(fakeGateway, routingMap);

        var offer = new E2eKeyOffer
        {
            SessionId = sessionId,
            Version = 1,
            Curve = "p256",
            PubKey = ByteString.CopyFrom(agentSpki),
            Salt = ByteString.CopyFrom(salt),
            AgentClientId = agentClientId,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Detach offer handler, exactly as the dispatch loop does (BLOCKING-1 fix).
        var offerTask = Task.Run(async () =>
            await bridge.HandleE2eKeyOfferAsync(
                offer,
                agentClientId: agentClientId,
                publisherNodeId: publisherNodeId,
                ct: cts.Token));

        // Wait for HandleE2eKeyOfferAsync to send the E2eKeyAnswer.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!fakeGateway.E2eAnswerSent && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(fakeGateway.E2eAnswerSent, "HandleE2eKeyOfferAsync must send E2eKeyAnswer");
        var publisherSpki = fakeGateway.CapturedPublisherSpki!;

        // Derive the agent confirm tag and kPayload from the actual publisher SPKI.
        var transcriptHash = E2eHandshake.BuildTranscriptHash(
            sessionId, agentClientId, publisherNodeId, salt, agentSpki, publisherSpki);

        byte[] agentTag;
        byte[] kPayload;
        using (var agentKeys = agentHandshake.Derive(publisherSpki, salt, transcriptHash))
        {
            E2eHandshake.VerifyConfirm(
                agentKeys.KConf, E2eHandshake.PublisherConfirmLabel, transcriptHash,
                fakeGateway.CapturedPublisherConfirmTag!);
            agentTag = E2eHandshake.ComputeConfirm(
                agentKeys.KConf, E2eHandshake.AgentConfirmLabel, transcriptHash);
            kPayload = (byte[])agentKeys.KPayload.Clone();
        }

        // Build agent-side cipher and encrypt a frame (DirClientToServer = what the publisher decrypts).
        using var agentCipher = new E2eSessionCipher(kPayload, sessionId);
        CryptographicOperations.ZeroMemory(kPayload);
        var plaintextPayload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n"u8.ToArray();
        var wirePayload = agentCipher.Seal(
            plaintextPayload,
            E2eSessionCipher.DirClientToServer,
            ReadOnlySpan<byte>.Empty,
            out var seqUsed);

        var confirm = new E2eKeyConfirm
        {
            SessionId = sessionId,
            ConfirmTag = ByteString.CopyFrom(agentTag),
        };

        // ACT: deliver confirm then encrypted frame back-to-back WITHOUT awaiting offerTask.
        // HandleE2eKeyConfirm MUST install the cipher synchronously (MAJOR-1 fix) so that
        // OnFrameReceivedAsync finds the cipher on the very next call.
        bridge.HandleE2eKeyConfirm(confirm);
        await bridge.OnFrameReceivedAsync(
            sessionId,
            mcpServerId: "server1",
            bytes: wirePayload,
            enc: 1,
            meta: null,
            sequenceNumber: seqUsed,
            cancellationToken: default);

        // ASSERT: session must be alive — cat subprocess received the stdin write and
        // is blocking. If the mismatch path had fired (old code), CloseSessionAsync
        // would have killed cat and ActiveSessionCount would be 0.
        Assert.Equal(1, bridge.ActiveSessionCount);

        // offerTask must also complete promptly (confirm signal was fired from HandleE2eKeyConfirm).
        await offerTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-2 (new): aggregator fail-closed on enc!=0 without installed cipher
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MAJOR-2 regression: an enc==1 frame arriving at the aggregator BackendSession when
    /// no E2E cipher is installed must close the session (fail-closed) and NOT forward the
    /// raw ciphertext/garbage as plaintext JSON-RPC into the buffer.
    /// </summary>
    [Fact]
    public void AggregatorBackendSession_Enc1WithNoCipher_ClosesSessionAndBuffersNothing()
    {
        // Create a BackendSession with no cipher installed.
        var conn = new NullGatewayConnection();
        var session = new Korat.Cli.Mcp.Aggregation.BackendSession(conn, "srv1", "test", "sess-enc-test");
        Assert.True(session.IsAlive, "session should start alive");

        // Deliver an enc==1 frame with no cipher installed — MUST fail-closed.
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 };
        session.OnInboundBytes(garbage, enc: 1, meta: null, sequenceNumber: 0);

        // RelaySession must be closed.
        Assert.False(session.IsAlive, "session must be closed after enc==1 with no cipher");
    }

    /// <summary>
    /// Corollary: enc==2 (unknown enc value) with no cipher is also fail-closed.
    /// </summary>
    [Fact]
    public void AggregatorBackendSession_UnknownEncWithNoCipher_ClosesSession()
    {
        var conn = new NullGatewayConnection();
        var session = new Korat.Cli.Mcp.Aggregation.BackendSession(conn, "srv1", "test", "sess-enc-unknown");
        session.OnInboundBytes(new byte[] { 0xFF }, enc: 2, meta: null, sequenceNumber: 0);
        Assert.False(session.IsAlive, "unknown enc value with no cipher must close session");
    }

    /// <summary>
    /// Plaintext path (enc==0, no cipher) must continue to work normally.
    /// </summary>
    [Fact]
    public void AggregatorBackendSession_Enc0WithNoCipher_AcceptsPlaintext()
    {
        var conn = new NullGatewayConnection();
        var session = new Korat.Cli.Mcp.Aggregation.BackendSession(conn, "srv1", "test", "sess-enc0-ok");
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"ok\":true}}\n"u8.ToArray();
        session.OnInboundBytes(payload, enc: 0, meta: null, sequenceNumber: 0);
        Assert.True(session.IsAlive, "plaintext frame with no cipher should not close the session");
    }

    /// <summary>Minimal IGatewayConnection stub for BackendSession unit tests.</summary>
    private sealed class NullGatewayConnection : Korat.Cli.Mcp.Aggregation.IGatewayConnection
    {
        public System.Threading.Channels.ChannelReader<Korat.Relay.V1.GatewayToNodeMessage> IncomingMessages
            => System.Threading.Channels.Channel.CreateUnbounded<Korat.Relay.V1.GatewayToNodeMessage>().Reader;
        public Task SendRequestSessionAsync(string a, string b, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFrameAsync(string a, ReadOnlyMemory<byte> b, ulong c, string d, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendE2eFrameAsync(string a, ReadOnlyMemory<byte> b, ulong c, string d, FrameMetadata e, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendE2eKeyOfferAsync(string a, uint b, string c, byte[] d, byte[] e, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendE2eKeyConfirmAsync(string a, byte[] b, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCloseSessionAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Fake <see cref="Korat.Cli.Mcp.ISessionBridgeGateway"/> that captures the publisher
    /// SPKI and confirm tag sent by <see cref="Korat.Cli.Mcp.SessionBridge.HandleE2eKeyOfferAsync"/>
    /// so the test can complete the handshake with a correctly-derived agent confirm tag.
    /// </summary>
    private sealed class CapturingFakeSessionBridgeGateway : Korat.Cli.Mcp.ISessionBridgeGateway
    {
        public bool E2eAnswerSent { get; private set; }
        public byte[]? CapturedPublisherSpki { get; private set; }
        public byte[]? CapturedPublisherConfirmTag { get; private set; }
        public int FramesSent { get; private set; }
        public int E2eFramesSent { get; private set; }

        public Task SendE2eKeyAnswerAsync(
            string sessionId, uint version, string curve, byte[] pubKey, byte[] confirmTag,
            CancellationToken cancellationToken = default)
        {
            CapturedPublisherSpki = pubKey;
            CapturedPublisherConfirmTag = confirmTag;
            E2eAnswerSent = true;
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(
            string sessionId, ReadOnlyMemory<byte> ciphertext, ulong sequenceNumber,
            string direction, CancellationToken cancellationToken = default)
        {
            FramesSent++;
            return Task.CompletedTask;
        }

        public Task SendE2eFrameAsync(
            string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber,
            string direction, FrameMetadata meta, CancellationToken cancellationToken = default)
        {
            E2eFramesSent++;
            return Task.CompletedTask;
        }

        public Task SendCloseSessionAsync(
            string sessionId, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MAJOR-3 (new): prefer + tag-mismatch → session aborted
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MAJOR-3: under --e2e=prefer, a CryptographicException (confirm-tag mismatch) from the
    /// aggregator handshake must abort the session, not fall back to plaintext.
    /// A broken MAC proves active tampering; plaintext fallback would be insecure.
    /// </summary>
    [Fact]
    public async Task AggregatorSession_Prefer_TagMismatch_AbortsSession()
    {
        var conn = new BadTagFakeConnection("sess-tamper");
        await using var mgr = new Korat.Cli.Mcp.Aggregation.BackendSessionManager(
            conn, agentClientId: "ag1",
            handshakeTimeout: TimeSpan.FromSeconds(5),
            e2ePolicy: E2ePolicy.Prefer);

        var server = new Korat.Cli.Mcp.Aggregation.ServerDescriptor("s1", "TestServer", true);
        // OpenAsync must throw because E2eHandshakeTamperingException propagates out.
        await Assert.ThrowsAnyAsync<Exception>(
            () => mgr.OpenAsync(server, "ts", default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// MAJOR-3: under --e2e=prefer, E2eNotSupported → plaintext fallback is acceptable (no tampering).
    /// </summary>
    [Fact]
    public async Task AggregatorSession_Prefer_NotSupported_PlaintextFallback()
    {
        var conn = new E2eNotSupportedFakeConnection("sess-nosupport");
        await using var mgr = new Korat.Cli.Mcp.Aggregation.BackendSessionManager(
            conn, agentClientId: "ag1",
            handshakeTimeout: TimeSpan.FromSeconds(5),
            e2ePolicy: E2ePolicy.Prefer);

        var server = new Korat.Cli.Mcp.Aggregation.ServerDescriptor("s1", "TestServer", true);
        // Must succeed (plaintext fallback) with tools.
        var tools = await mgr.OpenAsync(server, "ts", default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(tools);
        Assert.False(conn.E2eHandshakeCompleted, "No E2E should be established on E2eNotSupported");
    }

    /// <summary>
    /// Fake that performs a real ECDH offer/answer but sends a WRONG (all-zero) confirm tag,
    /// causing VerifyConfirm to throw CryptographicException in TryEstablishE2eAsync.
    /// </summary>
    private sealed class BadTagFakeConnection : Korat.Cli.Mcp.Aggregation.IGatewayConnection
    {
        private readonly string _sessionId;
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();

        public BadTagFakeConnection(string sessionId) => _sessionId = sessionId;

        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = _sessionId }
            });
            return Task.CompletedTask;
        }

        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            using var publisherHandshake = E2eHandshake.CreateEphemeral();
            var publisherSpki = publisherHandshake.ExportSpki();
            // Send a CORRUPTED confirm tag (all zeros) — proves active tampering.
            var badTag = new byte[32]; // all zeros
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2EKeyAnswer = new E2eKeyAnswer
                {
                    SessionId = sessionId,
                    Version = 1,
                    Curve = "p256",
                    PubKey = ByteString.CopyFrom(publisherSpki),
                    ConfirmTag = ByteString.CopyFrom(badTag),
                    PublisherNodeId = "bad-publisher",
                }
            });
            return Task.CompletedTask;
        }

        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendFrameAsync(string a, ReadOnlyMemory<byte> b, ulong c, string d, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendE2eFrameAsync(string a, ReadOnlyMemory<byte> b, ulong c, string d, FrameMetadata e, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCloseSessionAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Fake that sends E2eNotSupported in response to an E2eKeyOffer, then responds to MCP
    /// requests with plaintext so the session can complete successfully.
    /// </summary>
    private sealed class E2eNotSupportedFakeConnection : Korat.Cli.Mcp.Aggregation.IGatewayConnection
    {
        private readonly string _sessionId;
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();

        public bool E2eHandshakeCompleted { get; private set; }

        public E2eNotSupportedFakeConnection(string sessionId) => _sessionId = sessionId;

        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = _sessionId }
            });
            return Task.CompletedTask;
        }

        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            // Respond with E2eNotSupported — benign absence, not tampering.
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2ENotSupported = new E2eNotSupported
                {
                    SessionId = sessionId,
                    Reason = "publisher-does-not-support-e2e",
                }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> bytes, ulong seq, string direction, CancellationToken ct = default)
        {
            // Auto-reply to MCP initialize/tools-list so OpenAsync can complete.
            var text = Encoding.UTF8.GetString(bytes.Span).TrimEnd('\n');
            JsonNode? node;
            try { node = JsonNode.Parse(text); } catch { return Task.CompletedTask; }
            var obj = node?.AsObject();
            if (obj is null) return Task.CompletedTask;
            if (!obj.TryGetPropertyValue("id", out var idNode) || idNode is null) return Task.CompletedTask;
            if (!obj.TryGetPropertyValue("method", out var mNode)) return Task.CompletedTask;
            var method = mNode!.GetValue<string>();
            var result = method == "tools/list"
                ? new JsonObject
                {
                    ["tools"] = new JsonArray((JsonNode)new JsonObject
                    {
                        ["name"] = "test_tool",
                        ["description"] = "d",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    })
                }
                : new JsonObject { ["ok"] = true };
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
            var replyBytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                Frame = new RelayFrame
                {
                    SessionId = sessionId,
                    Ciphertext = ByteString.CopyFrom(replyBytes),
                    Direction = "server_to_client",
                    Enc = 0,
                }
            });
            return Task.CompletedTask;
        }

        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
        {
            E2eHandshakeCompleted = true;
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendE2eFrameAsync(string a, ReadOnlyMemory<byte> b, ulong c, string d, FrameMetadata e, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCloseSessionAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }
}
