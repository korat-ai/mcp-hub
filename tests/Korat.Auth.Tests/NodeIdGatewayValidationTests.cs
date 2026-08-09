using Korat.Domain;

namespace Korat.Auth.Tests;

/// <summary>
/// Locks the gateway-layer NodeId admission rule (N-2, defense-in-depth):
/// <c>NodeGatewayService.HandleHelloAsync</c> calls <see cref="NodeId.IsWellFormed"/>
/// to reject Hellos whose NodeId is not a well-formed 32-hex GUID before any grain
/// or persistence interaction.
///
/// These tests cover the predicate from the gateway's perspective — confirming that
/// the exact values a hostile or misbehaving client might send are correctly classified.
/// Full end-to-end "AccessDenied on wire" coverage lives in the integration suite
/// (NodeGatewayBearerHelloTests), which requires a running Postgres/Orleans cluster.
/// </summary>
public class NodeIdGatewayValidationTests
{
    // ── Cases the gateway must REJECT ────────────────────────────────────────

    [Theory]
    [InlineData("default",            "keyword-like low-entropy id")]
    [InlineData("a",                  "single character")]
    [InlineData("../x",               "path traversal attempt")]
    [InlineData("node-1",             "dashed non-GUID")]
    [InlineData("wild>card",          "NATS wildcard char >")]
    [InlineData("star*token",         "NATS wildcard char *")]
    [InlineData("has space",          "whitespace in subject token")]
    [InlineData("korat.relay.frame.x","NATS subject injection attempt")]
    [InlineData("",                   "empty string")]
    [InlineData("a3b1c2d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6", "dashed GUID — not canonical N format")]
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5",        "30-char hex — too short")]
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5d6aa",    "34-char hex — too long")]
    public void MalformedNodeId_GatewayWouldReject(string nodeId, string reason)
    {
        // The gateway calls NodeId.IsWellFormed(hello.NodeId) and returns AccessDenied
        // when it returns false.  We assert the predicate here so that any accidental
        // weakening of the check shows up as a test failure independently of the
        // integration fixture.
        Assert.False(NodeId.IsWellFormed(nodeId),
            $"Expected IsWellFormed to return false for '{nodeId}' ({reason}), " +
            $"but it returned true — the gateway would accept this NodeId.");
    }

    [Fact]
    public void NullNodeId_GatewayWouldReject()
    {
        Assert.False(NodeId.IsWellFormed(null));
    }

    // ── Cases the gateway must ACCEPT ─────────────────────────────────────────

    [Fact]
    public void CliMintedNodeId_GatewayWouldAccept()
    {
        // The CLI calls NodeId.New() which produces a 32-hex GUID.
        // A well-behaved CLI must never be rejected by the format check.
        var id = NodeId.New().Value;
        Assert.True(NodeId.IsWellFormed(id),
            $"NodeId.New() produced '{id}' which failed IsWellFormed — " +
            $"the gateway would incorrectly reject a well-behaved CLI.");
    }

    [Theory]
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5d6")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    [InlineData("00000000000000000000000000000000")]
    public void WellFormedGuidN_GatewayWouldAccept(string nodeId)
    {
        Assert.True(NodeId.IsWellFormed(nodeId));
    }

    // ── SEC-CRITICAL-1 compatibility ──────────────────────────────────────────

    [Fact]
    public void SecCritical1_NodeIdFormat_IsOrthogonalToSpaceCheck()
    {
        // N-2 (format validation) fires BEFORE SEC-CRITICAL-1 (space-mismatch check).
        // A NodeId that passes format validation but belongs to another space is still
        // caught by SEC-CRITICAL-1 further down in HandleHelloAsync.
        // This test documents that a well-formed NodeId does NOT bypass SEC-CRITICAL-1.
        var wellFormedId = NodeId.New().Value;
        Assert.True(NodeId.IsWellFormed(wellFormedId),
            "A well-formed NodeId passes the format gate; SEC-CRITICAL-1 space check remains active.");
        // (The space check itself is covered by ValidBearer_NodeAlreadyOwnedByAnotherSpace_ReturnsAccessDenied
        // in the integration suite.)
    }
}
