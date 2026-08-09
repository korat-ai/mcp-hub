using System.Net.Http.Headers;
using System.Text;
using Korat.Domain.Entities;
using Korat.Mcp;

namespace Korat.Cloud.Mcp.Http;

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): a thin MCP Streamable-HTTP client. Sends a single
/// JSON-RPC request per call, `Accept: application/json, text/event-stream` per the MCP
/// Streamable-HTTP transport spec, and accepts EITHER a direct `application/json` response body
/// OR a `text/event-stream` response (Finding 16, B3 — see below).
///
/// One instance = one consumer's upstream MCP session (Crux Finding 14 — NOT shared across
/// consumers). `IDisposable` because it owns `httpClient`; the owning `HttpMcpProxyGrain`
/// disposes it when that consumer's session closes/evicts.
///
/// Finding 16, B3 — SSE-in-POST-response is part of Streamable HTTP itself, not an edge case.
/// Pinned via Context7 against `/websites/modelcontextprotocol_io_specification_2025-06-18`
/// ("Sending Messages to the Server", "Resumability and Redelivery"): for a JSON-RPC REQUEST,
/// the server MAY reply with either a direct `application/json` body OR
/// `Content-Type: text/event-stream` — on that stream, interim JSON-RPC requests/notifications
/// MAY precede the final response matching the original request's `id`, after which the server
/// closes the stream. This client implements a MINIMAL bounded SSE reader (`ReadSseResponseAsync`
/// below): it reads `data:` lines (optionally preceded by an `id:` line; blank line terminates
/// an event), parses each event's JSON, DROPS any event that is not the final matching-`id`
/// response (interim notifications/requests are out of scope — spec §2's "no new bidirectional
/// design" — this client is a REQUEST/RESPONSE client, not a full duplex one), and returns as
/// soon as the matching response arrives. The standalone `GET` SSE stream (server-initiated
/// messages OUTSIDE a request/response) stays explicitly OUT OF SCOPE (spec §2) — this reader
/// only ever runs against the POST response body for a request THIS client just sent.
///
/// Auth header injection and error mapping mirror OutboundInferenceClient.cs exactly (no
/// upstream body ever surfaces past this class — see HttpMcpProxyGrain's caller).
///
/// Finding 16, S5 — `MCP-Protocol-Version` header. Pinned via the same Context7 source
/// ("Protocol Version Header"): the spec requires this header on every HTTP request after
/// initialization; if absent the server defaults to `2025-03-26` for backward compat and MAY
/// 400 on an unsupported value. Sent on every request once this client has completed its own
/// (or observed the consumer's pass-through — see HttpMcpProxyGrain) `initialize` — never on the
/// `initialize` call itself, since no version is negotiated yet at that point. Fable holistic
/// review FIX 3 [SHOULD-FIX]: the VALUE sent is whatever the upstream's own initialize RESPONSE
/// reports it negotiated (`_negotiatedProtocolVersion`, captured in `InitializeAsync`), not always
/// the pinned `McpProtocolVersion` constant — B2's pass-through forwards the CONSUMER's own
/// "initialize" verbatim, so a consumer that negotiated an older/different revision must have THAT
/// value echoed on every later request, or a strict upstream may 400 them all.
///
/// Timeout is caller-supplied (via the CancellationToken passed to InitializeAsync/SendAsync),
/// NOT hardcoded here (Crux Finding 15): the original draft hardcoded 30 s "mirroring
/// OutboundInferenceClient's bounded timeouts", which was implicitly calibrated to the OLD
/// design's Orleans-grain-call ceiling. Dispatch is one-way now (Crux Finding 13) — nothing
/// bounds this call to 30 s anymore, and this codebase's own hosted-agent tool calls commonly
/// run minutes, so a 30 s HTTP timeout here would silently reintroduce the exact failure this
/// plan was revised to fix. HttpMcpProxyGrain supplies a generous, independently-chosen bound.
///
/// Response body is read with a hard byte cap (Crux Finding 15 / spec §6 "response-path
/// limits"): a hostile or buggy remote returning gigabytes must not be fully buffered into
/// memory before any size check runs. `ReadAsByteArrayAsync()` would do exactly that; this
/// class reads incrementally instead (both the `application/json` path and the SSE path) and
/// aborts once the running total exceeds <paramref name="maxResponseBytes"/> (the caller passes
/// PayloadLimitPolicy.DefaultPerMessageBytes).
/// </summary>
public sealed class HttpMcpClient(HttpClient httpClient, string remoteUrl) : IDisposable
{
    public const string McpProtocolVersion = "2025-06-18"; // matches apps/Korat.Cli/Mcp/Aggregation/BackendSession.cs

    private string? _mcpSessionId;
    private bool _negotiated; // Finding 16, S5: true once initialize has completed — gates the MCP-Protocol-Version header.
    // Fable holistic review FIX 3 [SHOULD-FIX]: the value actually stamped on the header once
    // `_negotiated` is true. Starts at the pinned constant as a fallback for a malformed/absent
    // `protocolVersion` in the initialize response; InitializeAsync below overwrites it with
    // whatever the upstream's OWN initialize response actually reports it negotiated.
    private string _negotiatedProtocolVersion = McpProtocolVersion;

    /// <summary>
    /// Finding 16, B2 — genuine pass-through: sends <paramref name="request"/> AS the upstream
    /// initialize, preserving its id/protocolVersion/capabilities/clientInfo VERBATIM (the caller
    /// — HttpMcpProxyGrain — parsed this straight off the consumer's own incoming frame). This is
    /// what makes it a pass-through rather than a reconstruction: the response this method
    /// returns already carries the CONSUMER's own id (the upstream naturally echoes back
    /// whatever id it was sent), so the caller does not need to re-stamp anything before
    /// forwarding the response on. The caller is responsible for confirming
    /// <c>request.Method == "initialize"</c> before calling this — it is not re-validated here.
    /// </summary>
    public async Task<HttpMcpMessage> InitializeAsync(HttpMcpMessage request, Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
    {
        var response = await SendCoreAsync(request, injectAuth, maxResponseBytes, ct);
        _negotiated = true; // MCP-Protocol-Version is sent on every request FROM HERE ON, never on this one.

        // FIX 3: B2's pass-through forwards the CONSUMER's own "initialize" verbatim upstream — a
        // consumer (Claude Desktop, etc.) may negotiate an older/different revision than this
        // client's own pinned constant (e.g. "2025-03-26"). Echo back whatever the upstream's OWN
        // response actually reports it negotiated on every subsequent request, rather than always
        // stamping the pinned constant — a strict upstream that only agreed to a different
        // revision may 400 every later call otherwise. Falls back to the pinned constant
        // (already the field's default) when the response is malformed or has no protocolVersion.
        try
        {
            var protocolVersionNode = response.Root["result"]?["protocolVersion"];
            if (protocolVersionNode is not null)
            {
                var negotiatedVersion = protocolVersionNode.GetValue<string>();
                if (!string.IsNullOrEmpty(negotiatedVersion))
                    _negotiatedProtocolVersion = negotiatedVersion;
            }
        }
        catch (Exception)
        {
            // Malformed shape (e.g. protocolVersion is not a string) — keep the pinned-constant
            // fallback already in _negotiatedProtocolVersion.
        }

        return response;
    }

    /// <summary>
    /// Finding 16, B2 (own-initialize FALLBACK only — never the normal path): builds and sends a
    /// synthetic initialize when the consumer itself never sent one (e.g. a bare tools/call with
    /// no prior handshake — an unusual but not impossible consumer). See
    /// HttpMcpProxyGrain.BuildResponseAsync's fallback branch, which is the ONLY caller of this
    /// overload; every normal session reaches upstream initialize via the other overload instead.
    /// </summary>
    public Task<HttpMcpMessage> InitializeWithOwnRequestAsync(Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
    {
        var initReq = HttpMcpMessage.Request(1, "initialize", new
        {
            protocolVersion = McpProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "korat-cloud", version = "1" }
        });
        return InitializeAsync(initReq, injectAuth, maxResponseBytes, ct);
    }

    public Task<HttpMcpMessage> SendAsync(HttpMcpMessage request, Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
        => SendCoreAsync(request, injectAuth, maxResponseBytes, ct);

    /// <summary>
    /// Finding 16, B2: sends a JSON-RPC NOTIFICATION (no "id" — e.g. the consumer's own
    /// "notifications/initialized", or any other fire-and-forget message) upstream. Per the
    /// Streamable HTTP spec (Context7, same source as this class's SSE note): the server
    /// responds "202 Accepted" with NO BODY for an accepted notification/response — this method
    /// must NOT attempt `HttpMcpMessage.Parse("")` against that empty body (the pre-review draft
    /// would have crashed here). Returns nothing — a notification has no reply by definition.
    /// </summary>
    public async Task SendNotificationAsync(HttpMcpMessage notification, Action<HttpRequestMessage>? injectAuth, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, remoteUrl);
        httpRequest.Content = new ByteArrayContent(notification.ToUtf8Bytes());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_mcpSessionId is not null)
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
        // FIX 3: stamp whatever revision was actually negotiated (see InitializeAsync), not
        // always the pinned constant — see SendCoreAsync's own comment below for why.
        if (_negotiated)
            httpRequest.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _negotiatedProtocolVersion);
        injectAuth?.Invoke(httpRequest);

        try
        {
            using var httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            // 202/204/any body-less-or-ignored success is expected and fine; a non-2xx is logged
            // by the caller (best-effort — a notification has no reply to fail, so this method
            // does not throw HttpMcpUpstreamException the way SendCoreAsync does for a request).
            if (httpResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
                _mcpSessionId = sessionIds.FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new HttpMcpUpstreamException("Failed to reach the remote MCP server (notification).");
        }
    }

    private async Task<HttpMcpMessage> SendCoreAsync(HttpMcpMessage request, Action<HttpRequestMessage>? injectAuth, long maxResponseBytes, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, remoteUrl);
        httpRequest.Content = new ByteArrayContent(request.ToUtf8Bytes());
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_mcpSessionId is not null)
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
        // Finding 16, S5: never on the initialize call itself (nothing negotiated yet); every
        // request after that carries it. FIX 3 (fable holistic review, [SHOULD-FIX]): the VALUE
        // sent is `_negotiatedProtocolVersion`, not the pinned `McpProtocolVersion` constant — B2's
        // pass-through forwards the CONSUMER's own "initialize" verbatim upstream, so the revision
        // actually agreed in the upstream's initialize RESPONSE may differ from this client's own
        // pinned constant (e.g. a consumer that negotiated "2025-03-26"). Sending the pinned
        // constant unconditionally here would risk a strict upstream 400-ing every request after
        // the first. See InitializeAsync for where `_negotiatedProtocolVersion` is captured.
        if (_negotiated)
            httpRequest.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _negotiatedProtocolVersion);
        injectAuth?.Invoke(httpRequest);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new HttpMcpUpstreamException("Failed to reach the remote MCP server.");
        }

        using (httpResponse)
        {
            if (httpResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
                _mcpSessionId = sessionIds.FirstOrDefault();

            var contentType = httpResponse.Content.Headers.ContentType?.MediaType;

            // Finding 16, B3: the server MAY answer a JSON-RPC request with an SSE stream instead
            // of a direct JSON body — read it with the same byte cap, and return the FIRST event
            // whose "id" matches this request's id (interim notifications/requests are dropped).
            if (contentType == "text/event-stream")
            {
                if (!httpResponse.IsSuccessStatusCode)
                {
                    if (httpResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        throw new HttpMcpUnauthorizedException("Remote MCP server returned HTTP 401.");
                    throw new HttpMcpUpstreamException($"Remote MCP server returned HTTP {(int)httpResponse.StatusCode}.");
                }
                return await ReadSseResponseAsync(
                    await httpResponse.Content.ReadAsStreamAsync(ct), request.Id, maxResponseBytes, ct);
            }

            var bodyBytes = await ReadBoundedAsync(
                await httpResponse.Content.ReadAsStreamAsync(ct), maxResponseBytes, ct);
            if (!httpResponse.IsSuccessStatusCode)
            {
                // Increment 2 (HTTP MCP OAuth), Task 5 grounding fix: the plain (non-SSE) response
                // path never classified a 401 distinctly — only the SSE branch above did. A stale
                // OAuth access token typically comes back as a plain 401 (often with no body at
                // all), which is the overwhelmingly common shape, not SSE — without this branch,
                // HttpMcpProxyGrain's reactive-401 refresh-then-retry (SendWithOAuthRetryAsync)
                // would never trigger for the realistic case, only for a hypothetical SSE-401.
                if (httpResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new HttpMcpUnauthorizedException("Remote MCP server returned HTTP 401.");
                throw new HttpMcpUpstreamException($"Remote MCP server returned HTTP {(int)httpResponse.StatusCode}.");
            }

            try
            {
                return HttpMcpMessage.Parse(bodyBytes);
            }
            catch (Exception)
            {
                throw new HttpMcpUpstreamException("Remote MCP server returned malformed JSON-RPC.");
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="stream"/> incrementally, throwing HttpMcpUpstreamException the
    /// moment the running total exceeds <paramref name="maxBytes"/> — never buffers more than
    /// maxBytes + one chunk into memory, unlike HttpContent.ReadAsByteArrayAsync().
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
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

    /// <summary>
    /// Finding 16, B3: minimal bounded SSE reader for a POST response body. Standard SSE wire
    /// format: an optional `id: &lt;n&gt;` line followed by one or more `data: &lt;chunk&gt;`
    /// lines, terminated by a blank line (event boundary). This reader:
    ///   - wraps the underlying response stream in <see cref="ByteCappedStream"/> BEFORE handing
    ///     it to StreamReader (fable gate FIX 1, T4 unhappy-path hardening — see that type's doc
    ///     comment for why a per-LINE check alone is not a bound at all against a hostile stream);
    ///   - joins multi-line `data:` fields with '\n' per the SSE spec;
    ///   - parses each completed event's data as a HttpMcpMessage;
    ///   - DROPS (does not forward) any event whose `id` does not match <paramref name="requestId"/>
    ///     (interim notifications/requests are explicitly out of scope this increment — see the
    ///     class doc comment);
    ///   - returns the FIRST event whose `id` matches, and does not read further (matches the
    ///     spec's own "the server closes the stream after the final response" expectation —
    ///     this client does not need to observe the close, just stop consuming);
    ///   - throws HttpMcpUpstreamException if the stream ends (or the byte cap is hit) before a
    ///     matching event ever arrives.
    /// </summary>
    private static async Task<HttpMcpMessage> ReadSseResponseAsync(
        Stream stream, System.Text.Json.Nodes.JsonNode? requestId, long maxBytes, CancellationToken ct)
    {
        await using (stream)
        using (var boundedStream = new ByteCappedStream(stream, maxBytes))
        using (var reader = new StreamReader(boundedStream, Encoding.UTF8))
        {
            var dataLines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0)
                {
                    // Blank line: event boundary. Parse-and-check what we've accumulated, if anything.
                    if (dataLines.Count > 0)
                    {
                        var eventJson = string.Join('\n', dataLines);
                        dataLines.Clear();
                        HttpMcpMessage parsed;
                        try
                        {
                            parsed = HttpMcpMessage.Parse(eventJson);
                        }
                        catch (Exception)
                        {
                            continue; // malformed event — skip it, keep reading (best-effort, mirrors the JSON path's own strictness only at the FINAL parse).
                        }
                        if (JsonNodeIdEquals(parsed.Id, requestId))
                            return parsed;
                        // Interim notification/request or a response for a different id — drop it.
                    }
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                    dataLines.Add(line["data:".Length..].TrimStart());
                // "id:", ":" (comment), "event:", "retry:" lines are all ignored — this client
                // does not support resumption (Last-Event-ID) this increment.
            }

            throw new HttpMcpUpstreamException("Upstream SSE stream ended without a matching response.");
        }
    }

    private static bool JsonNodeIdEquals(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
    {
        if (a is null || b is null) return false;
        return a.ToJsonString() == b.ToJsonString();
    }

    public void Dispose() => httpClient.Dispose();

    /// <summary>
    /// Fable gate FIX 1 (T4 unhappy-path hardening, [BLOCKER]): wraps the upstream SSE response
    /// stream so the per-message byte cap is enforced on every underlying READ of the stream
    /// itself, not just after <see cref="StreamReader.ReadLineAsync()"/> hands back a completed
    /// "line". Before this fix, the per-message check ran only once a full line had already been
    /// assembled — but StreamReader.ReadLineAsync has NO bound on how large a single line it will
    /// build before returning (it keeps appending decoded chars to an internal buffer until it
    /// finds '\n' or reaches end-of-stream). A hostile upstream that streams gigabytes with no
    /// newline would therefore be buffered IN FULL by StreamReader before the old check ever had a
    /// chance to run — for a stream that never closes, that is unbounded memory growth (an OOM of
    /// the whole cloud process, every tenant) rather than a bounded read. This decorator throws
    /// <see cref="HttpMcpUpstreamException"/> the moment CUMULATIVE bytes read from the underlying
    /// stream exceed <paramref name="maxBytes"/> — bounding memory to maxBytes + at most one
    /// in-flight read-chunk, regardless of where (or whether) a newline ever appears. Read-only;
    /// does not own/dispose the wrapped stream (the caller's own `await using` handles that).
    /// </summary>
    private sealed class ByteCappedStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Account(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Account(read);
            return read;
        }

        private void Account(int read)
        {
            _totalRead += read;
            if (_totalRead > maxBytes)
                throw new HttpMcpUpstreamException("Upstream SSE response exceeded the per-message size limit.");
        }
    }
}

/// <summary>
/// Increment 1: a sanitized, upstream-detail-free failure signal. The message is always
/// safe to log AND (via HttpMcpProxyGrain) safe to surface as a generic JSON-RPC error to the
/// consumer — it NEVER contains the raw upstream body/headers/secret.
/// </summary>
public class HttpMcpUpstreamException(string message) : Exception(message);

/// <summary>
/// Increment 2 (HTTP MCP OAuth): a distinguished subtype for an upstream 401 specifically — lets
/// HttpMcpProxyGrain's oauth path (Task 5) trigger a single-flight refresh-then-retry-once, which
/// a plain HttpMcpUpstreamException (used for every other non-2xx status) cannot distinguish.
/// </summary>
public sealed class HttpMcpUnauthorizedException(string message) : HttpMcpUpstreamException(message);
