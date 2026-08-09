// 031-relay-confidentiality: FrameMetadataFactory unit tests.
using System.Text;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Protocol.Tests;

public class FrameMetadataFactoryTests
{
    private static ReadOnlySpan<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ── tools/call → category=tool_call, kind=request ────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_ToolCall_ExtractsToolName()
    {
        var line = Utf8("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file","arguments":{}}}""");
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionClientToServer, (ulong)line.Length);

        Assert.Equal("read_file", meta.ToolName);
        Assert.Equal("tool_call", meta.Category);
        Assert.Equal("request",   meta.Kind);
    }

    [Fact]
    public void FrameMetadataFactory_ToolCall_NoName_EmptyToolName()
    {
        // tools/call but no params.name
        var line = Utf8("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}""");
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionClientToServer, (ulong)line.Length);

        Assert.Equal("tool_call", meta.Category);
        Assert.Equal(string.Empty, meta.ToolName);
    }

    // ── Response → category=tool_result ──────────────────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_Response_CategoryToolResult()
    {
        var line = Utf8("""{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"hello"}]}}""");
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionServerToClient, (ulong)line.Length);

        Assert.Equal("tool_result", meta.Category);
        Assert.Equal(string.Empty,  meta.ToolName);
    }

    // ── Lifecycle → category=lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_Initialize_CategoryLifecycle()
    {
        var line = Utf8("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"1.0"}}""");
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionClientToServer, (ulong)line.Length);

        Assert.Equal("lifecycle", meta.Category);
    }

    // ── Partial / non-JSON → chunk ────────────────────────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_PartialLine_ReturnsChunk()
    {
        var line = Utf8("{\"method\":\"tools"); // truncated
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionClientToServer, (ulong)line.Length);

        Assert.Equal("chunk", meta.Kind);
        Assert.Equal("other", meta.Category);
        Assert.Equal(string.Empty, meta.ToolName);
    }

    [Fact]
    public void FrameMetadataFactory_Empty_ReturnsChunk()
    {
        var meta = FrameMetadataFactory.FromPlaintext(ReadOnlySpan<byte>.Empty, E2eSessionCipher.DirectionClientToServer, 0);

        Assert.Equal("chunk", meta.Kind);
    }

    // ── Chunk factory ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_Chunk_HasCorrectFields()
    {
        var meta = FrameMetadataFactory.Chunk(256);

        Assert.Equal("chunk", meta.Kind);
        Assert.Equal("other", meta.Category);
        Assert.Equal(string.Empty, meta.ToolName);
        Assert.Equal(256uL, meta.PayloadBytes);
    }

    // ── PayloadBytes set correctly ────────────────────────────────────────────────────────────────

    [Fact]
    public void FrameMetadataFactory_PayloadBytes_MatchesArgument()
    {
        var line = Utf8("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"x","arguments":{}}}""");
        const ulong expectedBytes = 999uL;
        var meta = FrameMetadataFactory.FromPlaintext(line, E2eSessionCipher.DirectionClientToServer, expectedBytes);

        Assert.Equal(expectedBytes, meta.PayloadBytes);
    }
}
