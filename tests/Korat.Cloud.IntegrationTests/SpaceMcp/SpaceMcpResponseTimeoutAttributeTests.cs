using System.Reflection;
using Korat.Cloud.Mcp.Space;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// MUST-FIX 1 (adversarial review, Space-MCP increment 1 Tasks 7-8, BLOCKER): Orleans' default
/// grain-call response timeout is 30s (<c>MessagingOptions.ResponseTimeout</c>, never overridden
/// anywhere else in this codebase) — but <see cref="ISpaceMcpAggregatorGrain.DispatchAsync"/>'s
/// own internal <c>tools/call</c> routing can legitimately wait up to
/// <c>SpaceBackendSession.ToolCallTimeout</c> (300s), <see cref="ISpaceMcpAggregatorGrain.InitializeAsync"/>'s
/// bounded-concurrency backend fan-out can take multiple minutes in the worst case, and
/// <see cref="ISpaceMcpAggregatorGrain.TerminateAsync"/> tears down N backends sequentially. A
/// still-legal, in-budget call on any of these three methods would otherwise throw
/// <c>TimeoutException</c> at the GRAIN-CALL boundary (<c>SpaceMcpDispatcher</c>'s own
/// <c>await grain.XxxAsync(...)</c>) well before the method's own internal work finishes.
///
/// This is a pure reflection check — no grain activation, no integration fixture — so a FUTURE
/// accidental removal of the <c>[ResponseTimeout]</c> attribute (e.g. during a refactor) is caught
/// here instantly rather than only by a slow/flaky "wait past 30s" integration test, which the
/// adversarial review itself calls out as impractical to run.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpResponseTimeoutAttributeTests
{
    [Theory]
    [InlineData(nameof(ISpaceMcpAggregatorGrain.DispatchAsync))]
    [InlineData(nameof(ISpaceMcpAggregatorGrain.InitializeAsync))]
    [InlineData(nameof(ISpaceMcpAggregatorGrain.TerminateAsync))]
    public void GrainMethod_HasResponseTimeoutAttribute_ExceedingOrleansDefault(string methodName)
    {
        var method = typeof(ISpaceMcpAggregatorGrain).GetMethod(methodName);
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<global::Orleans.ResponseTimeoutAttribute>();
        Assert.True(attribute is not null,
            $"Expected {nameof(ISpaceMcpAggregatorGrain)}.{methodName} to carry a " +
            $"[ResponseTimeout] override — without one, Orleans' default 30s grain-call response " +
            $"timeout can truncate a still-in-budget call (see this class's own doc comment).");

        // Orleans' own default (MessagingOptions.ResponseTimeout) is 30s — every override here
        // must exceed it, not merely differ from it, or the fix is a no-op.
        Assert.True(attribute!.Timeout > TimeSpan.FromSeconds(30),
            $"Expected {methodName}'s [ResponseTimeout] to exceed Orleans' 30s default, got " +
            $"{attribute.Timeout}.");
    }

    [Fact]
    public void DispatchAsync_ResponseTimeout_ExceedsToolCallTimeout()
    {
        // The specific budget DispatchAsync's own override must clear: SpaceBackendSession's own
        // tools/call timeout (300s) plus lazy backend wake/open budget (40s). A margin that does
        // not clear their combined budget would still truncate a legitimately slow mobile call
        // at the grain-call boundary.
        var method = typeof(ISpaceMcpAggregatorGrain).GetMethod(nameof(ISpaceMcpAggregatorGrain.DispatchAsync));
        var attribute = method!.GetCustomAttribute<global::Orleans.ResponseTimeoutAttribute>();
        Assert.NotNull(attribute);
        Assert.True(attribute!.Timeout > TimeSpan.FromSeconds(340),
            $"Expected DispatchAsync's [ResponseTimeout] to exceed the combined 340s lazy-open " +
            $"plus tools/call timeout, " +
            $"got {attribute.Timeout}.");
    }
}
