// 031-relay-confidentiality: cleartext FrameMetadata stamping for E2E-encrypted relay frames.
// Uses cheap Utf8JsonReader heuristic (same marker scan as McpToolCallInspector) to avoid
// full JSON parse on every frame. Safe for partial / non-JSON payloads (all exceptions caught).
using System.Text.Json;
using Korat.Relay.V1;

namespace Korat.Protocol;

/// <summary>
/// Builds the cleartext <see cref="FrameMetadata"/> proto header that rides alongside an
/// E2E-encrypted relay frame.  The metadata is stamped by the SENDER before encryption and
/// is AAD-bound to the ciphertext, so cloud tampering causes AEAD failure at the peer.
///
/// <para>The cloud reads only these fields for inspection/policy (tool name, category, size);
/// it never touches the encrypted payload.</para>
/// </summary>
public static class FrameMetadataFactory
{
    private static readonly byte[] ToolsCallMarker = "tools/call"u8.ToArray();
    private static readonly byte[] ToolsResultMarker = "tools/result"u8.ToArray();
    private static readonly byte[] MethodMarker = "\"method\""u8.ToArray();

    // ── Public API ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build metadata for an outgoing frame whose MCP plaintext payload is <paramref name="plaintextLine"/>.
    /// Caller sets <paramref name="direction"/> = the RelayFrame.direction proto string value.
    /// <paramref name="plaintextBytes"/> = length of the unencrypted payload (before sealing).
    /// </summary>
    public static FrameMetadata FromPlaintext(
        ReadOnlySpan<byte> plaintextLine,
        string direction,
        ulong plaintextBytes)
    {
        var (kind, category, toolName) = ClassifyLine(plaintextLine);
        return new FrameMetadata
        {
            Kind         = kind,
            Category     = category,
            ToolName     = toolName,
            PayloadBytes = plaintextBytes,
        };
    }

    /// <summary>
    /// Build a minimal metadata stub for a frame carrying a binary or opaque chunk
    /// (e.g. partial stdio line, not parseable as JSON-RPC). Kind = "chunk".
    /// </summary>
    public static FrameMetadata Chunk(ulong payloadBytes)
        => new FrameMetadata
        {
            Kind         = "chunk",
            Category     = "other",
            ToolName     = string.Empty,
            PayloadBytes = payloadBytes,
        };

    // ── Classification ───────────────────────────────────────────────────────────────────────────

    private static (string kind, string category, string toolName) ClassifyLine(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
            return ("chunk", "other", string.Empty);

        // Quick marker scan before paying for JSON parse.
        // We only fast-reject lines that have neither "method" nor "result" nor "error" nor "id"
        // at all — binary chunks, partial reads, etc.
        bool hasMethod = line.IndexOf(MethodMarker) >= 0;
        bool isToolCall = hasMethod && line.IndexOf(ToolsCallMarker) >= 0;
        // result/error/id are common JSON keys: only reject if none of the JSON-RPC keys are present.
        bool looksLikeJsonRpc = hasMethod
            || line.IndexOf("\"result\""u8) >= 0
            || line.IndexOf("\"error\""u8)  >= 0
            || line.IndexOf("\"id\""u8)     >= 0;

        if (!looksLikeJsonRpc)
            return ("chunk", "other", string.Empty);

        // Try a cheap JSON parse to extract kind/method/tool name.
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { AllowTrailingCommas = true });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ("chunk", "other", string.Empty);

            // Determine kind from presence of id + method fields.
            bool hasId     = root.TryGetProperty("id"u8, out _);
            bool hasMethodProp = root.TryGetProperty("method"u8, out var methodProp)
                                 && methodProp.ValueKind == JsonValueKind.String;
            bool hasResult = root.TryGetProperty("result"u8, out _);
            bool hasError  = root.TryGetProperty("error"u8, out _);

            string kind = (hasId, hasMethodProp, hasResult || hasError) switch
            {
                (true,  true,  _)     => "request",
                (true,  false, true)  => "response",
                (false, true,  false) => "notification",
                _                    => "other",
            };

            // Category + tool name.
            if (isToolCall && hasMethodProp && methodProp.GetString() == "tools/call")
            {
                string toolName = string.Empty;
                if (root.TryGetProperty("params"u8, out var prms)
                    && prms.ValueKind == JsonValueKind.Object
                    && prms.TryGetProperty("name"u8, out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    toolName = name.GetString() ?? string.Empty;
                }
                return ("request", "tool_call", toolName);
            }

            // A response (has id, has result or error, no method) is a tool result.
            if (hasId && !hasMethodProp && (hasResult || hasError))
                return (kind, "tool_result", string.Empty);

            if (hasMethodProp)
            {
                var method = methodProp.GetString() ?? string.Empty;
                if (method.StartsWith("initialize", StringComparison.Ordinal)
                    || method.StartsWith("ping", StringComparison.Ordinal)
                    || method.StartsWith("notifications/", StringComparison.Ordinal))
                    return (kind, "lifecycle", string.Empty);
            }

            return (kind, "other", string.Empty);
        }
        catch (JsonException)
        {
            // Partial or non-JSON — treat as a chunk.
            return ("chunk", "other", string.Empty);
        }
    }
}
