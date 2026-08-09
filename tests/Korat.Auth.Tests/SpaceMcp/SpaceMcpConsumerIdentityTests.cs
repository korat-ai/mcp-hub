using Korat.Cloud.Mcp.Space;
using Korat.Domain;

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Task 3 (2026-07-10 Space-MCP increment-1 plan): <see cref="SpaceMcpConsumerIdentity"/> is
/// deterministic, Space-scoped, and rendered in a namespace disjoint from the CLI's own
/// <see cref="ConsumerId.New"/> ids (BLOCKER-2, Global Constraint "Durable consumer
/// identity").
/// </summary>
public class SpaceMcpConsumerIdentityTests
{





    [Fact]
    public void SyntheticConnectionId_EmbedsTheMcpSessionId()
    {
        const string mcpSessionId = "abc123deadbeef";

        var connectionId = SpaceMcpConsumerIdentity.SyntheticConnectionId(mcpSessionId);

        Assert.Equal("cagg-" + mcpSessionId, connectionId.Value);
    }

    [Fact]
    public void SyntheticConnectionId_DiffersByMcpSessionId()
    {
        var a = SpaceMcpConsumerIdentity.SyntheticConnectionId("session-a");
        var b = SpaceMcpConsumerIdentity.SyntheticConnectionId("session-b");

        Assert.NotEqual(a, b);
    }
}
