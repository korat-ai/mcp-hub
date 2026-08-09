using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Р24: `korat connect` no longer acts as an MCP consumer.
///
/// <para>Every consumer mode authenticated with <c>~/.korat/credentials</c> — one token per
/// machine — so the cloud saw one consumer identity regardless of which agent was calling.
/// Permissions issued "to an agent" were permissions to the machine. Р25 closed the matching
/// entrance on the server; leaving this side working would only move the failure later and make
/// it less legible.</para>
///
/// <para>The pair of tests matters more than either alone. A gate that refuses everything would
/// satisfy the first test and silently break the publisher path, which is the CLI's actual job.
/// </para>
/// </summary>
public sealed class ConnectConsumerModeDisabledTests
{
    [Theory]
    // --bridge: long-lived stdio transport for an MCP client.
    [InlineData(true, false, null, false)]
    // --space: the local aggregator.
    [InlineData(false, true, null, false)]
    // --send: one-shot tool call.
    [InlineData(false, false, "hello", false)]
    // --wait-response on its own.
    [InlineData(false, false, null, true)]
    public void ConsumerFlags_AreRecognisedAsConsumerMode(bool bridge, bool space, string? send, bool waitResponse)
    {
        Assert.True(ConnectCommand.IsConsumerMode(bridge, space, send, waitResponse));
    }

    [Fact]
    public void PublisherInvocation_IsNotCaughtByTheGate()
    {
        // No consumer flag at all — this is the shape the publisher/one-shot-session path uses,
        // and it must keep working.
        Assert.False(ConnectCommand.IsConsumerMode(bridge: false, space: false, send: null, waitResponse: false));
    }
}
