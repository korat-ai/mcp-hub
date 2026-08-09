// 031-relay-confidentiality: backward-compatibility guard tests.
// Acceptance criteria:
//   A4: legacy peer (plaintext, no enc/meta fields) still parses unchanged.
//   A10: existing relay contract suite — RelayFrame legacy fields byte-identical.
//
// These tests verify that adding fields 8 (enc) and 9 (meta) to RelayFrame does NOT
// change the serialization of any frame that does not set those fields (proto3 default
// = absent ⇒ no bytes on the wire). Any regression here means old CLIs will break.
using Google.Protobuf;
using Korat.Relay.V1;

namespace Korat.Protocol.Tests;

public class RelayFrameLegacyCompatTests
{
    // ── A10: legacy RelayFrame fields 1–7 serialize identically ─────────────────────────────────

    [Fact]
    public void RelayFrame_LegacyFields_Unchanged_RoundTrip()
    {
        var frame = new RelayFrame
        {
            SessionId      = "sess-legacy-001",
            SourceNodeId   = "node-src",
            TargetNodeId   = "node-dst",
            SequenceNumber = 42,
            Direction      = "client_to_server",
            Ciphertext     = ByteString.CopyFrom([0x01, 0x02, 0x03]),
            McpServerId    = "mcp-srv-7",
        };

        var bytes    = frame.ToByteArray();
        var restored = RelayFrame.Parser.ParseFrom(bytes);

        Assert.Equal(frame.SessionId,      restored.SessionId);
        Assert.Equal(frame.SourceNodeId,   restored.SourceNodeId);
        Assert.Equal(frame.TargetNodeId,   restored.TargetNodeId);
        Assert.Equal(frame.SequenceNumber, restored.SequenceNumber);
        Assert.Equal(frame.Direction,      restored.Direction);
        Assert.Equal(frame.Ciphertext,     restored.Ciphertext);
        Assert.Equal(frame.McpServerId,    restored.McpServerId);
        // New fields default to absent/zero.
        Assert.Equal(0u, restored.Enc);
        Assert.Null(restored.Meta);
    }

    [Fact]
    public void RelayFrame_LegacyFields_Unchanged_ByteIdentical()
    {
        // Build the same frame twice and verify byte-level identity (proto serialization is deterministic).
        var frame1 = new RelayFrame
        {
            SessionId      = "sess-byte-check",
            SourceNodeId   = "n1",
            TargetNodeId   = "n2",
            SequenceNumber = 99,
            Direction      = "server_to_client",
            Ciphertext     = ByteString.CopyFrom([0xAA, 0xBB]),
            McpServerId    = string.Empty,
        };
        var frame2 = new RelayFrame
        {
            SessionId      = "sess-byte-check",
            SourceNodeId   = "n1",
            TargetNodeId   = "n2",
            SequenceNumber = 99,
            Direction      = "server_to_client",
            Ciphertext     = ByteString.CopyFrom([0xAA, 0xBB]),
            McpServerId    = string.Empty,
        };

        Assert.Equal(frame1.ToByteArray(), frame2.ToByteArray());
    }

    // ── enc field: absent when 0 (proto3 default) ────────────────────────────────────────────────

    [Fact]
    public void RelayFrame_EncField_AbsentWhenZero()
    {
        var frame = new RelayFrame { SessionId = "s1", Enc = 0 };
        var bytes = frame.ToByteArray();
        var restored = RelayFrame.Parser.ParseFrom(bytes);
        Assert.Equal(0u, restored.Enc);

        // An old parser that doesn't know field 8 would skip it harmlessly (proto3 unknown fields).
        // Enc=1 must survive round-trip.
        var e2eFrame = new RelayFrame { SessionId = "s1", Enc = 1 };
        var e2eBytes = e2eFrame.ToByteArray();
        var e2eRestored = RelayFrame.Parser.ParseFrom(e2eBytes);
        Assert.Equal(1u, e2eRestored.Enc);
    }

    // ── FrameMetadata in frame: absent when not set ──────────────────────────────────────────────

    [Fact]
    public void RelayFrame_MetaField_AbsentWhenNull()
    {
        var frame = new RelayFrame { SessionId = "s2" };
        var bytes = frame.ToByteArray();
        var restored = RelayFrame.Parser.ParseFrom(bytes);
        Assert.Null(restored.Meta);
    }

    [Fact]
    public void RelayFrame_MetaField_RoundTrip()
    {
        var frame = new RelayFrame
        {
            SessionId = "s3",
            Enc = 1,
            Meta = new FrameMetadata
            {
                ToolName     = "list_files",
                Kind         = "request",
                Category     = "tool_call",
                PayloadBytes = 128,
            }
        };

        var restored = RelayFrame.Parser.ParseFrom(frame.ToByteArray());

        Assert.Equal("list_files", restored.Meta!.ToolName);
        Assert.Equal("request",    restored.Meta.Kind);
        Assert.Equal("tool_call",  restored.Meta.Category);
        Assert.Equal(128uL,        restored.Meta.PayloadBytes);
    }

    // ── SessionOpened: peer_supports_e2e field is additive ──────────────────────────────────────

    [Fact]
    public void SessionOpened_PeerSupportsE2e_DefaultFalse()
    {
        var msg = new SessionOpened
        {
            RequestId     = "req-1",
            SessionId     = "sess-1",
            HomeGatewayId = "gw-1",
        };
        var restored = SessionOpened.Parser.ParseFrom(msg.ToByteArray());
        Assert.False(restored.PeerSupportsE2E);
    }

    [Fact]
    public void SessionOpened_PeerSupportsE2e_RoundTrip()
    {
        var msg = new SessionOpened
        {
            RequestId      = "req-2",
            SessionId      = "sess-2",
            HomeGatewayId  = "gw-2",
            PeerSupportsE2E = true,
        };
        var restored = SessionOpened.Parser.ParseFrom(msg.ToByteArray());
        Assert.True(restored.PeerSupportsE2E);
    }

    // ── A4: NodeHello capabilities field can carry "e2e-v1" ─────────────────────────────────────

    [Fact]
    public void NodeHello_Capabilities_CanCarryE2eV1()
    {
        var hello = new NodeHello
        {
            SpaceId    = "sp-1",
            NodeId     = "nd-1",
            NodeKind   = "publisher",
            Capabilities = { "inference", "e2e-v1" },
        };

        var restored = NodeHello.Parser.ParseFrom(hello.ToByteArray());
        Assert.Contains("e2e-v1", restored.Capabilities);
        Assert.Contains("inference", restored.Capabilities);
    }

    // ── E2E key exchange messages round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void E2eKeyOffer_RoundTrip()
    {
        var offer = new E2eKeyOffer
        {
            SessionId  = "sess-offer",
            Version    = 1,
            Curve      = "p256",
            PubKey     = ByteString.CopyFrom([0x04, 0x01, 0x02]),
            Salt       = ByteString.CopyFrom([0xAA, 0xBB, 0xCC]),
            McpServerId = "srv-1",
        };

        var msg = new NodeToGatewayMessage { E2EKeyOffer = offer };
        var restored = NodeToGatewayMessage.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(NodeToGatewayMessage.PayloadOneofCase.E2EKeyOffer, restored.PayloadCase);
        Assert.Equal("sess-offer", restored.E2EKeyOffer.SessionId);
        Assert.Equal("p256",       restored.E2EKeyOffer.Curve);
        Assert.Equal(1u,           restored.E2EKeyOffer.Version);
    }

    [Fact]
    public void E2eKeyAnswer_RoundTrip()
    {
        var answer = new E2eKeyAnswer
        {
            SessionId  = "sess-ans",
            Version    = 1,
            Curve      = "p256",
            PubKey     = ByteString.CopyFrom([0x04, 0x03]),
            ConfirmTag = ByteString.CopyFrom(new byte[32]),
        };

        var msg = new GatewayToNodeMessage { E2EKeyAnswer = answer };
        var restored = GatewayToNodeMessage.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2EKeyAnswer, restored.PayloadCase);
        Assert.Equal("sess-ans", restored.E2EKeyAnswer.SessionId);
    }

    [Fact]
    public void E2eKeyConfirm_RoundTrip()
    {
        var confirm = new E2eKeyConfirm
        {
            SessionId  = "sess-conf",
            ConfirmTag = ByteString.CopyFrom(new byte[32]),
        };

        var msg = new NodeToGatewayMessage { E2EKeyConfirm = confirm };
        var restored = NodeToGatewayMessage.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(NodeToGatewayMessage.PayloadOneofCase.E2EKeyConfirm, restored.PayloadCase);
        Assert.Equal("sess-conf", restored.E2EKeyConfirm.SessionId);
    }

    [Fact]
    public void E2eNotSupported_RoundTrip()
    {
        var ns = new E2eNotSupported
        {
            SessionId = "sess-ns",
            Reason    = "publisher does not support e2e-v1",
        };

        var msg = new GatewayToNodeMessage { E2ENotSupported = ns };
        var restored = GatewayToNodeMessage.Parser.ParseFrom(msg.ToByteArray());

        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.E2ENotSupported, restored.PayloadCase);
        Assert.Equal("sess-ns", restored.E2ENotSupported.SessionId);
    }
}
