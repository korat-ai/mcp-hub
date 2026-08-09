using Korat.Domain.Auth;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="NodeAuthTokens"/> — a pure cryptographic helper that computes
/// and verifies HMAC-SHA256 per-node tokens.
///
/// NOTE: The gRPC gateway no longer accepts NodeAuthToken HMAC credentials — nodes must
/// authenticate via Bearer (CLI token). These tests cover the algorithm correctness only;
/// the gateway rejection tests are in <see cref="NodeGatewayBearerHelloTests"/>.
/// </summary>
public sealed class NodeAuthTokenTests
{
    [Fact]
    public void NodeAuthTokens_Compute_IsDeterministic_AcrossCalls()
    {
        const string secret = "owner-secret-abc";
        const string nodeId = "node-123";

        var first = NodeAuthTokens.Compute(secret, nodeId);
        var second = NodeAuthTokens.Compute(secret, nodeId);

        Assert.Equal(first, second);
        Assert.True(NodeAuthTokens.Verify(secret, nodeId, first));
        // Different NodeId → different token (sanity check that NodeId is part of the input).
        Assert.NotEqual(first, NodeAuthTokens.Compute(secret, "node-different"));
    }

    [Fact]
    public void NodeAuthTokens_Verify_RejectsTokenDerivedFromWrongOwnerSecret()
    {
        const string nodeId = "node-victim";
        const string realSecret = "real-owner-secret";
        const string attackerSecret = "attacker-guess";

        var forged = NodeAuthTokens.Compute(attackerSecret, nodeId);

        Assert.False(NodeAuthTokens.Verify(realSecret, nodeId, forged),
            "A token derived from a different owner-secret must not verify against the real secret.");
        Assert.False(NodeAuthTokens.Verify(realSecret, nodeId, null));
        Assert.False(NodeAuthTokens.Verify(realSecret, nodeId, ""));
        Assert.False(NodeAuthTokens.Verify(realSecret, nodeId, "garbage"));
    }
}
