using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.Domain.Auth;

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Space-MCP inc-2a, Task 6 (BLOCKER-2 / spec §Identity): the OAuth durable-consumer
/// derivation (client_id × ownerUserId × SpaceId) — stable across calls, distinct per input,
/// in the same reserved cagg_ namespace as inc-1's (cliTokenId × SpaceId) derivation, and
/// DELIBERATELY different from it for the same Space (O1: inc-1 grants are dev-only and
/// orphaned at OAuth cutover — accepted).
/// </summary>
public sealed class SpaceMcpOAuthIdentityTests
{
    private static readonly UserId Owner = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private static readonly SpaceId Space = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public void DeriveOAuth_IsStable()
    {
        var a = SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", Owner, Space);
        var b = SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", Owner, Space);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveOAuth_DiffersByEveryComponent()
    {
        var baseline = SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", Owner, Space);
        Assert.NotEqual(baseline, SpaceMcpConsumerIdentity.DeriveOAuth("other-client", Owner, Space));
        Assert.NotEqual(baseline, SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", new UserId(Guid.NewGuid()), Space));
        Assert.NotEqual(baseline, SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", Owner, new SpaceId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")));
    }

    [Fact]
    public void DeriveOAuth_StaysInReservedNamespace_AndDisjointFromCliShape()
    {
        var id = SpaceMcpConsumerIdentity.DeriveOAuth("korat-mcp", Owner, Space);
        Assert.StartsWith("cagg_", id.Value);
        Assert.Equal(31, id.Value.Length); // same shape SessionAdmission's reserved-namespace guard covers
    }

    [Fact]
    public void DeriveOAuth_SeparatesClients_WithinOneOwnerAndSpace()
    {
        // Replaces a test that asserted the OAuth derivation never collided with the CLI-token
        // derivation. Р25 removed the latter, so that comparison no longer has two sides. What
        // still needs proving is the property Р25 was FOR: two clients belonging to the same owner
        // in the same Space are different consumers. When the CLI branch existed they were not —
        // one machine, one token, one identity — and per-agent grants were a label on a
        // machine-wide permission.
        var cursor = SpaceMcpConsumerIdentity.DeriveOAuth("cursor", Owner, Space);
        var claude = SpaceMcpConsumerIdentity.DeriveOAuth("claude-code", Owner, Space);
        Assert.NotEqual(cursor, claude);
    }
}
