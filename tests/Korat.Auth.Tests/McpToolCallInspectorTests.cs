using System.Text;
using Korat.Cloud.Observability;
using Korat.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="McpToolCallInspector"/> — 009-nats-relay-backplane.
/// Verifies tool-call extraction from a newline-delimited JSON-RPC byte stream, including
/// frames that split a line, multiple lines per frame, and defensive rejection of junk.
/// </summary>
public class McpToolCallInspectorTests
{
    private static readonly SessionId RelaySession = new("sess-1");
    private static readonly McpServerId Server = new("srv-1");
    private static readonly SpaceId Space = new("space-1");

    private sealed class CapturingSink : IMcpToolCallSink
    {
        public readonly List<ToolCallEvent> Events = [];
        public void Record(in ToolCallEvent toolCall) => Events.Add(toolCall);
    }

    private static McpToolCallInspector New(CapturingSink sink)
        => new(sink, NullLogger<McpToolCallInspector>.Instance);

    private static void Observe(McpToolCallInspector inspector, string direction, string text)
        => inspector.Observe(RelaySession, Server, Space, direction, Encoding.UTF8.GetBytes(text));

    private const string ToolCallLine =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_weather\",\"arguments\":{\"city\":\"SF\"}}}\n";

    [Fact]
    public void ExtractsToolCall_FromCompleteLine()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server", ToolCallLine);

        var evt = Assert.Single(sink.Events);
        Assert.Equal("get_weather", evt.ToolName);
        Assert.Equal("srv-1", evt.McpServerId);
        Assert.Equal("space-1", evt.SpaceId);
        Assert.Equal("client_to_server", evt.Direction);
    }

    [Fact]
    public void DoesNotEmit_UntilNewlineArrives()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        // First half of the line — no terminating newline yet.
        var split = ToolCallLine.Length / 2;
        Observe(inspector, "client_to_server", ToolCallLine[..split]);
        Assert.Empty(sink.Events);

        // Remainder including the newline → now it emits exactly once.
        Observe(inspector, "client_to_server", ToolCallLine[split..]);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void EmitsOncePerLine_WhenMultipleLinesInOneFrame()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server", ToolCallLine + ToolCallLine);

        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void HandlesCrLfLineEndings()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server", ToolCallLine.TrimEnd('\n') + "\r\n");

        Assert.Single(sink.Events);
        Assert.Equal("get_weather", sink.Events[0].ToolName);
    }

    [Fact]
    public void Ignores_NonToolsCallMethod()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}\n");

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Ignores_NonJsonLine()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server", "this is not json at all\n");
        // Contains the marker substring but is not valid JSON → must not throw, must not emit.
        Observe(inspector, "client_to_server", "garbage tools/call garbage\n");

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Ignores_ToolsCallWithoutName()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        Observe(inspector, "client_to_server",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{}}\n");

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ForgetSession_DiscardsPartialBuffer()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        var split = ToolCallLine.Length / 2;
        Observe(inspector, "client_to_server", ToolCallLine[..split]);
        inspector.ForgetSession(RelaySession);

        // The remainder now starts a fresh (incomplete) line; no event should be emitted.
        Observe(inspector, "client_to_server", ToolCallLine[split..]);

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void SeparatesBuffers_PerDirection()
    {
        var sink = new CapturingSink();
        var inspector = New(sink);

        // Interleave halves of two independent lines on different directions; each completes.
        var split = ToolCallLine.Length / 2;
        Observe(inspector, "client_to_server", ToolCallLine[..split]);
        Observe(inspector, "server_to_client", ToolCallLine[..split]);
        Observe(inspector, "client_to_server", ToolCallLine[split..]);
        Observe(inspector, "server_to_client", ToolCallLine[split..]);

        Assert.Equal(2, sink.Events.Count);
    }
}
