using Korat.Domain.Auth;

namespace Korat.Domain.Tests.Auth;

public class NodeAuthTokensTests
{
    private const string OwnerToken = "test-owner-token-abc123";
    private const string NodeId = "node-abc";

    [Fact]
    public void Verify_CorrectToken_ReturnsTrue()
    {
        var token = NodeAuthTokens.Compute(OwnerToken, NodeId);
        Assert.True(NodeAuthTokens.Verify(OwnerToken, NodeId, token));
    }

    [Fact]
    public void Verify_WrongOwnerToken_ReturnsFalse()
    {
        var token = NodeAuthTokens.Compute(OwnerToken, NodeId);
        Assert.False(NodeAuthTokens.Verify("wrong-owner-token", NodeId, token));
    }

    [Fact]
    public void Verify_WrongNodeId_ReturnsFalse()
    {
        var token = NodeAuthTokens.Compute(OwnerToken, NodeId);
        Assert.False(NodeAuthTokens.Verify(OwnerToken, "node-xyz", token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_NullOrEmptyPresented_ReturnsFalse(string? presented)
    {
        Assert.False(NodeAuthTokens.Verify(OwnerToken, NodeId, presented));
    }

    [Fact]
    public void Verify_LengthMismatch_ReturnsFalse()
    {
        var token = NodeAuthTokens.Compute(OwnerToken, NodeId);
        // Truncate the base64 token to create a length mismatch.
        var truncated = token[..^4];
        Assert.False(NodeAuthTokens.Verify(OwnerToken, NodeId, truncated));
    }

    [Fact]
    public void Verify_SingleTamperedByte_ReturnsFalse()
    {
        var token = NodeAuthTokens.Compute(OwnerToken, NodeId);
        // Flip one character in the base64 to tamper the token.
        var chars = token.ToCharArray();
        chars[0] = chars[0] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);
        Assert.False(NodeAuthTokens.Verify(OwnerToken, NodeId, tampered));
    }
}
