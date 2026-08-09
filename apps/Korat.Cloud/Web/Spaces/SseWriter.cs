using System.Text;

namespace Korat.Cloud.Web.Spaces;

/// <summary>
/// Writes Server-Sent Events to an HttpResponse.
/// Enforces the SSE newline-injection guard (SR-T3-8): raw \n or \r bytes in chunk_json
/// must not create extra SSE events. Well-formed JSON never contains raw newlines
/// (they must be \n or \r in JSON); if a malicious node sends raw newlines they are
/// stripped before emission.
/// </summary>
public static class SseWriter
{
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] EventTerminator = "\n\n"u8.ToArray();
    private static readonly byte[] DoneEvent = "data: [DONE]\n\n"u8.ToArray();

    /// <summary>
    /// Sets SSE response headers. Must be called before any body bytes are written.
    /// </summary>
    public static void SetSseHeaders(HttpResponse response)
    {
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");
    }

    /// <summary>
    /// Writes one SSE data frame: <c>data: &lt;sanitizedJson&gt;\n\n</c>
    /// Sanitizes raw newlines from chunkJson bytes (SR-T3-8).
    /// </summary>
    public static async Task WriteChunkAsync(HttpResponse response, byte[] chunkJson, CancellationToken ct)
    {
        var sanitized = SanitizeJsonBytes(chunkJson);
        await response.Body.WriteAsync(DataPrefix, ct);
        await response.Body.WriteAsync(sanitized, ct);
        await response.Body.WriteAsync(EventTerminator, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Writes the final <c>data: [DONE]\n\n</c> event.
    /// </summary>
    public static async Task WriteDoneAsync(HttpResponse response, CancellationToken ct)
    {
        await response.Body.WriteAsync(DoneEvent, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Removes raw CR and LF bytes from JSON bytes (SR-T3-8 frame-injection guard).
    /// Valid JSON uses \n/\r escape sequences (two chars), not raw 0x0A/0x0D bytes.
    /// Stripping them collapses any injected fake SSE frames into a single data: line.
    /// </summary>
    internal static byte[] SanitizeJsonBytes(byte[] input)
    {
        // Fast path: no raw newlines (the common case for valid JSON).
        bool hasNewline = false;
        foreach (var b in input)
        {
            if (b == 0x0A || b == 0x0D) { hasNewline = true; break; }
        }
        if (!hasNewline) return input;

        // Slow path: copy without CR/LF bytes.
        var result = new byte[input.Length];
        int j = 0;
        foreach (var b in input)
        {
            if (b != 0x0A && b != 0x0D)
                result[j++] = b;
        }
        return result[..j];
    }
}
