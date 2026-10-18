using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Korat.Cloud.Mcp.Http;

/// <summary>
/// One consumer's MCP Streamable-HTTP session (2025-06-18 / 2025-03-26).
/// POST accepts JSON or bounded SSE; supported notifications are delivered with backpressure
/// through the same callback as standalone GET SSE. No transport headers or diagnostic bodies
/// are forwarded. The owner starts GET after notifications/initialized and cancels on close.
/// There is at most one active GET reader; its owner controls bounded reconnects.
/// </summary>
public sealed class HttpMcpClient(HttpClient httpClient, string remoteUrl,
    Func<HttpMcpMessage, CancellationToken, Task>? onNotification = null) : IDisposable
{
    public const string McpProtocolVersion = "2025-06-18";
    private string? _mcpSessionId;
    private bool _negotiated;
    private string _negotiatedProtocolVersion = McpProtocolVersion;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifetimeLock = new();
    private bool _disposed;
    private int _getStarted;
    public bool SupportsNotifications { get; private set; }

    public async Task<HttpMcpMessage> InitializeAsync(HttpMcpMessage request,
        Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
    {
        var response = await SendCoreAsync(request, injectAuth, maxResponseBytes, ct);
        if (!response.HasError)
        {
            _negotiated = true;
            if (response.Root["result"] is JsonObject initResult && initResult["capabilities"] is JsonObject capabilities)
                SupportsNotifications = Flag(capabilities, "resources", "subscribe")
                    || Flag(capabilities, "resources", "listChanged")
                    || Flag(capabilities, "prompts", "listChanged") || Flag(capabilities, "tools", "listChanged");
            if (response.Root["result"] is JsonObject result
                && result["protocolVersion"] is JsonValue version
                && version.TryGetValue<string>(out var value) && !string.IsNullOrEmpty(value))
                _negotiatedProtocolVersion = value;
        }
        return response;
    }

    private static bool Flag(JsonObject capabilities, string name, string flag)
        => capabilities[name] is JsonObject capability && capability[flag] is JsonValue value
            && value.TryGetValue<bool>(out var enabled) && enabled;

    public Task<HttpMcpMessage> InitializeWithOwnRequestAsync(Action<HttpRequestMessage>? injectAuth,
        long maxResponseBytes, CancellationToken ct)
        => InitializeAsync(HttpMcpMessage.Request(1, "initialize", new
        {
            protocolVersion = McpProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "korat-cloud", version = "1" }
        }), injectAuth, maxResponseBytes, ct);

    public Task<HttpMcpMessage> SendAsync(HttpMcpMessage request, Action<HttpRequestMessage>? injectAuth,
        long maxResponseBytes, CancellationToken ct)
        => SendCoreAsync(request, injectAuth, maxResponseBytes, ct);

    public async Task SendNotificationAsync(HttpMcpMessage notification,
        Action<HttpRequestMessage>? injectAuth, CancellationToken ct)
    {
        using var linked = LinkLifetime(ct);
        using var request = CreateRequest(HttpMethod.Post, notification, injectAuth);
        using var response = await SendHttpAsync(request, linked.Token);
        EnsureSuccess(response);
    }

    /// <summary>
    /// Reads one standalone stream, with per-event AND cumulative wire-byte limits.
    /// Returns false for normal POST-only 405 fallback, true at SSE EOF. The owner controls
    /// bounded retries and supplies a session lifetime/deadline. Concurrent readers are refused.
    /// </summary>
    public async Task<bool> ListenForNotificationsAsync(Action<HttpRequestMessage>? injectAuth,
        long maxMessageBytes, long maxStreamBytes, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _getStarted, 1, 0) != 0)
            throw new InvalidOperationException("A notification reader is already active.");
        try
        {
            using var linked = LinkLifetime(ct);
            using var request = CreateRequest(HttpMethod.Get, null, injectAuth);
            using var response = await SendHttpAsync(request, linked.Token);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return false;
            EnsureSuccess(response);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new HttpMcpUpstreamException("Remote MCP server did not return an SSE stream.");
            await ReadSseAsync(await response.Content.ReadAsStreamAsync(linked.Token), null,
                maxMessageBytes, maxStreamBytes, linked.Token);
            return true;
        }
        finally { Volatile.Write(ref _getStarted, 0); }
    }

    private async Task<HttpMcpMessage> SendCoreAsync(HttpMcpMessage message,
        Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
    {
        using var linked = LinkLifetime(ct);
        ct = linked.Token;
        using var request = CreateRequest(HttpMethod.Post, message, injectAuth);
        using var response = await SendHttpAsync(request, ct);
        EnsureSuccess(response);
        // Only initialize establishes session identity, never concurrent GET/POST responses.
        if (message.Method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            _mcpSessionId = ids.FirstOrDefault();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            return await ReadSseAsync(await response.Content.ReadAsStreamAsync(ct), message.Id,
                maxResponseBytes, maxResponseBytes, ct)
                ?? throw new HttpMcpUpstreamException("Upstream SSE stream ended without a matching response.");

        var body = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), maxResponseBytes, ct);
        try { return HttpMcpMessage.Parse(body); }
        catch (Exception) { throw new HttpMcpUpstreamException("Remote MCP server returned malformed JSON-RPC."); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, HttpMcpMessage? message,
        Action<HttpRequestMessage>? injectAuth)
    {
        var request = new HttpRequestMessage(method, remoteUrl);
        try
        {
            if (message is not null)
            {
                request.Content = new ByteArrayContent(message.ToUtf8Bytes());
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_mcpSessionId is not null)
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
            if (_negotiated)
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _negotiatedProtocolVersion);
            injectAuth?.Invoke(request);
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new HttpMcpUpstreamException("Failed to reach the remote MCP server."); }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpMcpUnauthorizedException("Remote MCP server returned HTTP 401.");
        if (!response.IsSuccessStatusCode)
            throw new HttpMcpUpstreamException($"Remote MCP server returned HTTP {(int)response.StatusCode}.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new HttpMcpUpstreamException("Upstream response exceeded the per-message size limit.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    private async Task<HttpMcpMessage?> ReadSseAsync(Stream stream, JsonNode? requestId,
        long maxMessageBytes, long maxStreamBytes, CancellationToken ct)
    {
        await using (stream)
        using (var bounded = new ByteCappedStream(stream, maxMessageBytes, maxStreamBytes))
        using (var reader = new StreamReader(bounded, Encoding.UTF8))
        {
            var data = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0)
                {
                    if (data.Count == 0) continue;
                    var json = string.Join('\n', data);
                    data.Clear();
                    HttpMcpMessage parsed;
                    try { parsed = HttpMcpMessage.Parse(json); }
                    catch (Exception) { continue; }
                    if (requestId is not null && !parsed.Root.ContainsKey("method")
                        && (parsed.Root.ContainsKey("result") || parsed.HasError)
                        && JsonNode.DeepEquals(parsed.Id, requestId))
                        return parsed;
                    var notification = SupportedNotification(parsed);
                    if (notification is not null && onNotification is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        // JsonNode's serializer can expand non-ASCII characters into escapes.
                        // The forwarded message must obey the cap as well as its SSE wire form.
                        if (notification.ToUtf8Bytes().LongLength + 1 > maxMessageBytes)
                            throw new HttpMcpUpstreamException("Upstream notification exceeded the per-message size limit.");
                        await onNotification(notification, ct);
                    }
                    continue;
                }
                if (line == "data") data.Add(string.Empty);
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = line[5..];
                    data.Add(value.StartsWith(' ') ? value[1..] : value);
                }
                // IDs/retry/comments are ignored; resumption is intentionally unsupported.
            }
            return null;
        }
    }

    private static HttpMcpMessage? SupportedNotification(HttpMcpMessage message)
    {
        var root = message.Root;
        if (root.ContainsKey("id") || root.ContainsKey("result") || root.ContainsKey("error")
            || root["jsonrpc"] is not JsonValue rpc || !rpc.TryGetValue<string>(out var version) || version != "2.0"
            || root["method"] is not JsonValue method || !method.TryGetValue<string>(out var name))
            return null;
        if (name is not ("notifications/tools/list_changed" or "notifications/resources/list_changed"
            or "notifications/prompts/list_changed" or "notifications/resources/updated"))
            return null;
        // Copy only protocol-defined content, never arbitrary diagnostics/extension fields.
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = name };
        if (name == "notifications/resources/updated")
        {
            if (root["params"] is not JsonObject parameters || parameters["uri"] is not JsonValue uri
                || !uri.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value))
                return null;
            notification["params"] = new JsonObject { ["uri"] = value };
        }
        return new HttpMcpMessage(notification);
    }

    private CancellationTokenSource LinkLifetime(CancellationToken ct)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        httpClient.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Caps raw reads before StreamReader can accumulate an unterminated line. Event accounting
    /// recognizes LF, CRLF and CR blank lines across reads, including ignored SSE fields.
    /// Cumulative accounting never resets, even for comment/empty-event floods.
    /// </summary>
    private sealed class ByteCappedStream(Stream inner, long maxMessageBytes, long maxStreamBytes) : Stream
    {
        private long _total, _eventBytes, _lineBytes;
        private bool _previousCr;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(buffer.AsSpan(offset, read));
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            Account(buffer.AsSpan(offset, read));
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            Account(buffer.Span[..read]);
            return read;
        }
        private void Account(ReadOnlySpan<byte> bytes)
        {
            _total += bytes.Length;
            if (_total > maxStreamBytes)
                throw new HttpMcpUpstreamException("Upstream SSE stream exceeded the size limit.");
            foreach (var b in bytes)
            {
                if (++_eventBytes > maxMessageBytes)
                    throw new HttpMcpUpstreamException("Upstream SSE event exceeded the per-message size limit.");
                if (b == '\n' && _previousCr) { _previousCr = false; continue; }
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (_lineBytes == 0) _eventBytes = 0;
                    _lineBytes = 0;
                }
                else _lineBytes++;
                _previousCr = b == '\r';
            }
        }
    }
}

/// <summary>A sanitized error; never includes upstream bodies, headers or credentials.</summary>
public class HttpMcpUpstreamException(string message) : Exception(message);

/// <summary>Allows the proxy's OAuth path to refresh and retry once.</summary>
public sealed class HttpMcpUnauthorizedException(string message) : HttpMcpUpstreamException(message);
