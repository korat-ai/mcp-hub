namespace Korat.Domain.Tests;

/// <summary>
/// Unit tests for <see cref="NodeId.IsWellFormed"/>.
///
/// A well-formed NodeId is exactly 32 lowercase hex characters — a <see cref="System.Guid"/>
/// serialised with format "N".  This is the canonical shape produced by <see cref="NodeId.New"/>.
///
/// Locks N-2 (server-side NodeId format validation, defense-in-depth):
///   • Low-entropy / attacker-shaped NodeIds must be rejected at admission.
///   • CLI-minted NodeIds (NodeId.New()) must always pass.
///   • SEC-CRITICAL-1 behavior is preserved (tested in integration layer) — these tests only
///     cover the format predicate.
/// </summary>
public class NodeIdValidationTests
{
    // ── Valid ────────────────────────────────────────────────────────────────

    [Fact]
    public void NodeIdNew_IsAlwaysWellFormed()
    {
        // The canonical factory must always produce IDs that pass the server-side check.
        for (var i = 0; i < 20; i++)
        {
            Assert.True(NodeId.IsWellFormed(NodeId.New().Value),
                "NodeId.New() produced a value that fails IsWellFormed");
        }
    }

    [Theory]
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5d6")] // 32 hex, lowercase
    [InlineData("A3B1C2D4E5F6A7B8C9D0E1F2A3B4C5D6")] // 32 hex, uppercase — Guid.TryParseExact("N") accepts both
    [InlineData("00000000000000000000000000000000")] // all-zeros GUID is technically valid format
    [InlineData("ffffffffffffffffffffffffffffffff")] // all-f
    public void WellFormedGuidN_ReturnsTrue(string value)
    {
        Assert.True(NodeId.IsWellFormed(value));
    }

    // ── Invalid — low-entropy / chosen-by-attacker ───────────────────────────

    [Theory]
    [InlineData("default")]
    [InlineData("a")]
    [InlineData("../x")]
    [InlineData("node-1")]
    [InlineData("not-a-guid")]
    [InlineData("korat.relay.frame.anything")] // subject injection attempt
    [InlineData("wild>card")]
    [InlineData("star*token")]
    [InlineData("has space")]
    [InlineData("")]
    public void MalformedOrLowEntropyNodeId_ReturnsFalse(string value)
    {
        Assert.False(NodeId.IsWellFormed(value));
    }

    [Fact]
    public void Null_ReturnsFalse()
    {
        Assert.False(NodeId.IsWellFormed(null));
    }

    [Theory]
    [InlineData("a3b1c2d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6")] // dashed GUID (format "D") — not canonical
    [InlineData("{a3b1c2d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6}")] // braced GUID (format "B") — not canonical
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5")] // 30 chars — too short
    [InlineData("a3b1c2d4e5f6a7b8c9d0e1f2a3b4c5d6aa")] // 34 chars — too long
    public void NonCanonicalOrWrongLength_ReturnsFalse(string value)
    {
        Assert.False(NodeId.IsWellFormed(value));
    }
}
