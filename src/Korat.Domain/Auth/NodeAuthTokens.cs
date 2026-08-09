using System.Security.Cryptography;
using System.Text;

namespace Korat.Domain.Auth;

/// <summary>
/// Per-node gateway auth tokens.
///
/// Closes the gRPC node-impersonation gap (security review CRITICAL): before this,
/// the Hello handshake on :5192 was trusted blindly and the only mitigation was the
/// loopback bind on the dev cloud. Once Fly.io exposes the gRPC port publicly, anyone
/// who guessed a NodeId could impersonate that node.
///
/// Design: <c>NodeAuthToken = HMAC-SHA256(OwnerToken, NodeId)</c>. No separate storage —
/// the cloud already knows OwnerToken (it is the space-owner auth backbone, see
/// <c>SpaceOwnerAuth</c>) and the CLI already has it cached in <c>LocalIdentityStore</c>.
/// Both sides recompute the token independently; they match iff both sides know the
/// same OwnerToken. A stranger from the internet who does not know OwnerToken cannot
/// produce a valid NodeAuthToken for ANY NodeId in this Space.
///
/// Threat model:
/// • OUT: stranger-from-internet impersonating a node. Mitigated.
/// • IN-SCOPE: holder of OwnerToken can claim any NodeId in their space. Acceptable
///   under the single-owner-per-Space model — the owner already has full control of
///   the Space via /api/space mutations.
/// • FUTURE: multi-owner-per-Space requires per-node tokens (separate storage), out
///   of scope for MVP.
/// </summary>
public static class NodeAuthTokens
{
    /// <summary>
    /// Derives the per-node auth token from the owner token and node id.
    /// Output is base64 (44 chars) — safe for gRPC text fields and protobuf strings.
    /// </summary>
    public static string Compute(string ownerToken, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(ownerToken);
        ArgumentNullException.ThrowIfNull(nodeId);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ownerToken));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(nodeId));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Constant-time verification — returns false on any mismatch (length or content).
    /// Empty or null <paramref name="presentedToken"/> always returns false.
    /// </summary>
    public static bool Verify(string ownerToken, string nodeId, string? presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
            return false;

        var expected = Compute(ownerToken, nodeId);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);

        // FixedTimeEquals requires equal-length inputs.
        if (expectedBytes.Length != presentedBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
