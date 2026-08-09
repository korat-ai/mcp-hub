using System.Collections.Concurrent;
using System.Text.Json;
using Korat.Domain;

namespace Korat.Cloud.Observability;

/// <summary>
/// 009-nats-relay-backplane: best-effort tap that reads MCP <c>tools/call</c> metadata out
/// of relayed frames and reports it via <see cref="IMcpToolCallSink"/>.
///
/// MCP stdio transport is newline-delimited JSON-RPC (messages MUST NOT contain embedded
/// newlines), but a single RelayFrame may carry a partial line or several lines. We
/// reassemble per (session, direction), split on '\n', and parse each complete line.
///
/// 031-relay-confidentiality: when the frame is E2E-encrypted (<c>enc==1</c>), the cloud
/// MUST NOT attempt to parse the payload. Instead the cloud calls <see cref="ObserveMetadata"/>
/// with the cleartext <see cref="FrameMetadata"/> header that the sender stamped. The legacy
/// <see cref="Observe"/> path is preserved for old CLIs sending plaintext frames.
/// </summary>
public sealed class McpToolCallInspector(IMcpToolCallSink sink, ILogger<McpToolCallInspector> logger)
{
    /// <summary>Drop a half-stream buffer that grows past this without a newline (runaway / binary).</summary>
    private const int MaxBufferBytes = 1 * 1024 * 1024;

    private readonly ConcurrentDictionary<(string Session, string Direction), StreamBuffer> _buffers = new();

    // ── 031: metadata-only path (E2E frames) ───────────────────────────────────────────────────

    /// <summary>
    /// 031-relay-confidentiality: observe a <see cref="Korat.Relay.V1.FrameMetadata"/> header from
    /// an E2E-encrypted frame. The cloud reads ONLY the cleartext metadata — never the payload.
    /// Emits a <see cref="ToolCallEvent"/> when <paramref name="toolName"/> is a <c>tool_call</c>.
    /// </summary>
    public void ObserveMetadata(
        string toolName,
        string category,
        McpServerId mcpServerId,
        SpaceId spaceId,
        string direction)
    {
        if (category != "tool_call" || string.IsNullOrEmpty(toolName))
            return;

        try
        {
            sink.Record(new ToolCallEvent(spaceId.Value, mcpServerId.Value, toolName, direction));
        }
        catch (Exception ex)
        {
            logger.LogDebug("Tool-call metadata observe failed errorType={ErrorType}", ex.GetType().Name);
        }
    }

    // ── Legacy plaintext path (enc==0, for old CLIs) ────────────────────────────────────────────

    public void Observe(SessionId sessionId, McpServerId mcpServerId, SpaceId spaceId, string direction, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return;

        var buffer = _buffers.GetOrAdd((sessionId.Value, direction ?? string.Empty), static _ => new StreamBuffer());

        lock (buffer.Sync)
        {
            buffer.Append(payload);

            while (buffer.TryConsumeLine(out var line))
            {
                TryEmit(line, mcpServerId, spaceId, direction ?? string.Empty);
            }

            // No newline yet but the buffer is huge → not line-delimited JSON we can use. Reset.
            if (buffer.Length > MaxBufferBytes)
                buffer.Reset();
        }
    }

    /// <summary>Drop all buffers for a session (call on session close / node teardown).</summary>
    public void ForgetSession(SessionId sessionId)
    {
        foreach (var key in _buffers.Keys)
        {
            if (key.Session == sessionId.Value)
                _buffers.TryRemove(key, out _);
        }
    }

    private void TryEmit(ReadOnlySpan<byte> line, McpServerId mcpServerId, SpaceId spaceId, string direction)
    {
        // Quick reject: must contain the method marker before we pay for a JSON parse.
        if (line.IsEmpty || !ContainsToolsCall(line))
            return;

        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                return;
            if (method.GetString() != "tools/call")
                return;
            if (!root.TryGetProperty("params", out var prms) || prms.ValueKind != JsonValueKind.Object)
                return;
            if (!prms.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                return;

            var toolName = name.GetString();
            if (string.IsNullOrEmpty(toolName))
                return;

            sink.Record(new ToolCallEvent(spaceId.Value, mcpServerId.Value, toolName, direction));
        }
        catch (JsonException)
        {
            // Partial or non-JSON line — expected on a byte stream; ignore.
        }
        catch (Exception ex)
        {
            logger.LogDebug("Tool-call inspect failed errorType={ErrorType}", ex.GetType().Name);
        }
    }

    /// <summary>Cheap substring scan for the tools/call marker to avoid parsing every line.</summary>
    private static bool ContainsToolsCall(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> marker = "tools/call"u8;
        return line.IndexOf(marker) >= 0;
    }

    /// <summary>
    /// Growable byte accumulator that yields newline-delimited lines. Uses a [_start,_end)
    /// window so consuming a line only advances <c>_start</c> — it never moves memory, so a
    /// line span stays valid until the next <see cref="Append"/>. Not thread-safe; guard
    /// with <see cref="Sync"/>.
    /// </summary>
    private sealed class StreamBuffer
    {
        public readonly object Sync = new();
        private byte[] _buffer = [];
        private int _start;
        private int _end;

        public int Length => _end - _start;

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_start == _end)
            {
                // Fully consumed — reuse from the front.
                _start = 0;
                _end = 0;
            }
            else if (_start > 0 && _buffer.Length - _end < data.Length)
            {
                // Need room and there is a consumed prefix to reclaim — compact to front.
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer.AsSpan(0));
                _end -= _start;
                _start = 0;
            }

            EnsureCapacity(_end + data.Length);
            data.CopyTo(_buffer.AsSpan(_end));
            _end += data.Length;
        }

        /// <summary>
        /// If a complete line (up to '\n') is buffered, yields it (CR trimmed) and advances
        /// past it. The returned span is valid until the next <see cref="Append"/> — the
        /// caller consumes it synchronously within the read loop.
        /// </summary>
        public bool TryConsumeLine(out ReadOnlySpan<byte> line)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var newlineIndex = span.IndexOf((byte)'\n');
            if (newlineIndex < 0)
            {
                line = default;
                return false;
            }

            var lineLength = newlineIndex;
            if (lineLength > 0 && span[lineLength - 1] == (byte)'\r')
                lineLength--;

            line = span.Slice(0, lineLength);
            _start += newlineIndex + 1;
            return true;
        }

        public void Reset()
        {
            _start = 0;
            _end = 0;
        }

        private void EnsureCapacity(int required)
        {
            if (_buffer.Length >= required)
                return;
            var newSize = Math.Max(required, Math.Max(256, _buffer.Length * 2));
            Array.Resize(ref _buffer, newSize);
        }
    }
}
