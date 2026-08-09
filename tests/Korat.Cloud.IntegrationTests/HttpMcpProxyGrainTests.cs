using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 1 Task 4: HttpMcpProxyGrain against a real (in-process, localhost) Streamable-HTTP
/// MCP stub — proves consumer-initialize pass-through → notifications/initialized →
/// tools/call round-trip (Finding 16, B2), rejection of a request sent before
/// notifications/initialized, an SSE-formatted POST response is read correctly (Finding 16, B3),
/// auth header injection, an upstream error never leaks the raw body/secret, an oversized
/// upstream response is cut off (not forwarded — Crux Finding 15), and one-way dispatch does not
/// serialize a fast consumer behind a slow one (Crux Finding 13). Docker not required (the stub
/// is a plain Kestrel app on a random localhost port); SSRF guard's
/// AllowPrivateNetworks(Testing/Development) escape hatch
/// (Korat:Inference:Outbound:AllowPrivateNetworks) is required — see Step 4's silo bridge — for
/// localhost to pass validation from inside the Orleans test silo's own container.
/// </summary>
public sealed class HttpMcpProxyGrainTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Finding 16, B2: the NORMAL path — a real consumer-shaped handshake. The stub's "initialize"
    /// response must be pushed back carrying the CONSUMER's own id (0, not the client's internal
    /// bookkeeping id — proves genuine pass-through, not a reconstruction). No push at all is
    /// expected for "notifications/initialized" (it's a notification). "tools/call" only succeeds
    /// AFTER that notification — proving the handshake barrier was correctly lifted, not just
    /// skipped.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_PassesThroughConsumerInitialize_ThenForwardsToolsCall_WithAuthHeaderInjected()
    {
        string? observedAuthHeader = null;
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            observedAuthHeader = ctx.Request.Headers.Authorization.ToString();
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";

            if (method == "notifications/initialized")
            {
                // Finding 16, B2: a notification gets 202 Accepted with NO body — the client must
                // not attempt to parse an empty response as JSON-RPC.
                ctx.Response.StatusCode = 202;
                return;
            }

            ctx.Response.ContentType = "application/json";
            string responseJson = method switch
            {
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"stub","version":"1"}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"error":{"code":-32601,"message":"method not found"}}"""
            };
            await ctx.Response.WriteAsync(responseJson);
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.Bearer, "s3cr3t-token");
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // 1. The consumer's own "initialize" — this IS the upstream initialize (pass-through).
        var initializeRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"korat-space","version":"0"}}}""");
        await proxy.DispatchFrameAsync(initializeRequest, connectionId, sessionId, default);
        var initPushed = await writer.ReadNextAsync(PushTimeout);
        var initResponseJson = JsonSerializer.Deserialize<JsonElement>(initPushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(0, initResponseJson.GetProperty("id").GetInt32()); // the CONSUMER's own id — proves pass-through, not reconstruction.
        Assert.Equal("Bearer s3cr3t-token", observedAuthHeader);

        // 2. The consumer's "notifications/initialized" — a notification, no push expected.
        var notifiedFrame = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await proxy.DispatchFrameAsync(notifiedFrame, connectionId, sessionId, default);

        // 3. Now tools/call succeeds — the handshake barrier was correctly lifted.
        var toolsCallRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        await proxy.DispatchFrameAsync(toolsCallRequest, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);

        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(7, responseJson.GetProperty("id").GetInt32());
        Assert.Equal("ok", responseJson.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// Finding 16, B2: a request sent BEFORE the consumer's own "notifications/initialized" (but
    /// after "initialize") must be rejected — mirrors the MCP handshake's own ordering
    /// requirement. This is also what proves Finding 16 M6's FIFO ordering fix actually matters:
    /// under the old per-frame-Task.Run design this assertion would be flaky (a race could let
    /// the barrier lift before this request is processed even though it was dispatched first).
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_RejectsRequestBeforeNotificationsInitialized()
    {
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        var initializeRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"korat-space","version":"0"}}}""");
        await proxy.DispatchFrameAsync(initializeRequest, connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout); // drain the initialize response.

        // Skip notifications/initialized — go straight to a request.
        var toolsCallRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(toolsCallRequest, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);

        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(1, responseJson.GetProperty("id").GetInt32());
        Assert.Equal(-32002, responseJson.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Fable-gate FIX 3 (T4 unhappy-path hardening, [MUST-FIX]): `notifications/initialized` must
    /// lift `consumer.PastInitializedBarrier` from the CONSUMER's own handshake state, not from
    /// upstream notification-delivery success. Before this fix, the barrier was set only AFTER
    /// `SendNotificationAsync` returned — so a single transient upstream failure on that ONE
    /// notification (the consumer sends it exactly once) meant the barrier never lifted, and
    /// EVERY subsequent request would be rejected -32002 forever. This stub aborts the connection
    /// specifically for "notifications/initialized" (so `HttpMcpClient.SendNotificationAsync`'s
    /// own `httpClient.SendAsync` throws, wrapped as `HttpMcpUpstreamException`) but serves
    /// "tools/call" normally — proving a transient upstream blip on the notification cannot brick
    /// request admission.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_NotificationUpstreamFailure_StillLiftsInitializedBarrier_SubsequentRequestSucceeds()
    {
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";

            if (method == "notifications/initialized")
            {
                // FIX 3: force a transient upstream failure on the ONE notification the consumer
                // ever sends — abort the connection so SendNotificationAsync's own SendAsync call
                // throws HttpRequestException (wrapped as HttpMcpUpstreamException) rather than
                // completing with a status code.
                ctx.Abort();
                return;
            }

            ctx.Response.ContentType = "application/json";
            string responseJson = method switch
            {
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"error":{"code":-32601,"message":"method not found"}}"""
            };
            await ctx.Response.WriteAsync(responseJson);
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        var initializeRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"korat-space","version":"0"}}}""");
        await proxy.DispatchFrameAsync(initializeRequest, connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout); // drain the initialize response.

        // notifications/initialized — the stub aborts the connection, so the upstream POST fails.
        // No push is expected either way (it's a notification).
        var notifiedFrame = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await proxy.DispatchFrameAsync(notifiedFrame, connectionId, sessionId, default);

        // Pre-fix: PastInitializedBarrier is set AFTER SendNotificationAsync returns, so the
        // thrown HttpMcpUpstreamException means it never lifts — this would come back -32002.
        // Post-fix: the barrier lifts from the CONSUMER's own handshake state, independent of
        // whether the upstream notification delivery succeeded.
        var toolsCallRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        await proxy.DispatchFrameAsync(toolsCallRequest, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);

        var responseJson2 = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(7, responseJson2.GetProperty("id").GetInt32());
        Assert.Equal("ok", responseJson2.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// Finding 16, B3: the upstream MAY reply to a JSON-RPC request with an SSE stream instead of
    /// a direct JSON body. This stub answers "tools/call" with `Content-Type: text/event-stream`
    /// carrying ONE event whose data is the matching JSON-RPC response — proves HttpMcpClient's
    /// minimal SSE reader parses it and the grain pushes the correct result to the consumer.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_SseResponse_ReturnsMatchingIdResult()
    {
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";

            if (method == "initialize")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
                return;
            }

            // Finding 16, B3: SSE response — a single event carrying the matching JSON-RPC result.
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync($"data: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"sse-ok\"}}]}}}}\n\n");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        var request = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""");

        await proxy.DispatchFrameAsync(request, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.Equal(3, responseJson.GetProperty("id").GetInt32());
        Assert.Equal("sse-ok", responseJson.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// Fable-gate FIX 1 (T4 unhappy-path hardening, [BLOCKER]): the SSE reader must bound the
    /// per-message size at the STREAM level, not just after a completed "line" returns from
    /// StreamReader. Before this fix, `ReadSseResponseAsync` only checked `totalBytes &gt; maxBytes`
    /// AFTER `reader.ReadLineAsync` returned a full line — but `StreamReader.ReadLineAsync` has no
    /// bound on how large a single line it will assemble before returning (it just keeps appending
    /// to an internal buffer until it finds '\n' or hits end-of-stream). A hostile upstream that
    /// streams past the cap with NO newline defeats the per-line check entirely until the stream
    /// closes — for a truly hostile NEVER-closing stream this means unbounded buffering / a hang,
    /// which is impractical to assert directly in a fast, deterministic unit test without risking
    /// the test itself hanging. Per the fable gate's own guidance, this test instead uses a FINITE
    /// stream just over the per-message cap (no newline anywhere) and asserts the bounded-size
    /// error is still produced promptly — proving the fix's stream-level decorator (which throws
    /// the moment CUMULATIVE bytes read from the underlying stream exceed the cap, regardless of
    /// newline placement) is what's doing the bounding, not a lucky finite-stream coincidence.
    /// See this test's `notes` entry in the fix-up task for why a true hang-based fail-before
    /// assertion was not attempted here.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_SseResponse_NoNewlineOverPerMessageCap_CutOffWithCleanError_NotForwarded()
    {
        var oversized = new string('x', (int)Korat.Domain.Entities.PayloadLimitPolicy.DefaultPerMessageBytes + 1024);
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            if (method == "initialize")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
                return;
            }

            // FIX 1: an SSE response with a single chunk larger than the per-message cap and NO
            // newline anywhere in the whole body — the pre-fix per-LINE bound only ever fires
            // after StreamReader has already assembled this entire oversized "line" in memory.
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(oversized);
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        var request = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{}}""");

        await proxy.DispatchFrameAsync(request, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseText = pushed.Frame.Ciphertext.ToStringUtf8();

        Assert.True(responseText.Length < 1024, "the oversized SSE body must be cut off, not forwarded");
        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseText);
        Assert.True(responseJson.TryGetProperty("error", out _));
    }

    /// <summary>
    /// These next three tests never send a consumer "initialize" first — they exercise the
    /// OWN-INITIALIZE FALLBACK path (Finding 16, B2's "not the normal path" branch) rather than
    /// pass-through. Both paths are real, both need coverage; the pass-through path has its own
    /// dedicated tests above.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_UpstreamError_PushesGenericJsonRpcError_NeverLeaksBody()
    {
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"internal":"stack trace with secret-looking-value XYZ"}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        var request = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");

        await proxy.DispatchFrameAsync(request, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseText = pushed.Frame.Ciphertext.ToStringUtf8();
        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseText);

        Assert.True(responseJson.TryGetProperty("error", out _));
        Assert.DoesNotContain("secret-looking-value", responseText);
        Assert.DoesNotContain("stack trace", responseText);
    }

    /// <summary>Crux Finding 15 / spec §6: a hostile/buggy remote returning gigabytes must be cut
    /// off with a clean session error, not buffered/forwarded. 17 MB &gt; PayloadLimitPolicy's
    /// 16 MB per-message cap.</summary>
    [Fact]
    public async Task DispatchFrameAsync_OversizedUpstreamResponse_CutOffWithCleanError_NotForwarded()
    {
        var oversized = new string('x', 17 * 1024 * 1024);
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            if (method == "initialize")
            {
                await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
                return;
            }
            await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"{{{{oversized}}}}"}]}}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        var request = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{}}""");

        await proxy.DispatchFrameAsync(request, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseText = pushed.Frame.Ciphertext.ToStringUtf8();

        Assert.True(responseText.Length < 1024, "the oversized body must be cut off, not forwarded");
        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseText);
        Assert.True(responseJson.TryGetProperty("error", out _));
    }

    /// <summary>
    /// Fable-gate FIX 2 (T4 unhappy-path hardening, [BLOCKER], "plain undeliverable" branch):
    /// `RunConsumerWorkerAsync` must NOT treat a plain, transient undeliverable push (e.g. the
    /// consumer's agent-stream registration momentarily missing — a NATS reconnect window, or
    /// cross-silo placement) the same as a session-hard-limit close. Before this fix, ANY
    /// `delivered == false` from `PushResponseWithGrainOwnedCapAsync` made the worker `break` —
    /// but it left the consumer's inbox WRITER OPEN and the `ConsumerUpstream` still in
    /// `_consumers`, so `DispatchFrameAsync`'s `TryWrite` for the NEXT frame silently succeeds
    /// into a channel nobody is left draining (the worker already exited its loop) — a silent
    /// black hole, violating `IHttpMcpProxyGrain`'s "always eventually pushes a response"
    /// contract. This test forces exactly that "plain undeliverable" case (NOT the grain-owned
    /// cap — see the sibling test below) by unregistering the consumer's own agent stream before
    /// its first frame is dispatched: `SessionRoutingTable`'s local-fast-path lookup then misses,
    /// falls to the (Null)RelayBackplane, which always reports undeliverable in this single-silo
    /// test host — critically, this does NOT touch the session route at all, unlike a real close.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_TransientUndeliverablePush_DoesNotWedgeSession_SubsequentFrameStillDelivered()
    {
        var firstToolsCallHandled = new TaskCompletionSource();
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";

            if (method == "notifications/initialized")
            {
                ctx.Response.StatusCode = 202;
                return;
            }

            ctx.Response.ContentType = "application/json";
            var responseJson = method == "initialize"
                ? $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}"""
                : $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"ok"}]}}""";
            await ctx.Response.WriteAsync(responseJson);

            if (method == "tools/call" && id == "1")
                firstToolsCallHandled.TrySetResult();
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var routingTable = fixture.Services.GetRequiredService<Korat.Cloud.Gateways.SessionRoutingTable>();
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // Real consumer handshake first (pass-through path), same as the happy-path tests above.
        var initializeRequest = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"korat-space","version":"0"}}}""");
        await proxy.DispatchFrameAsync(initializeRequest, connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout); // drain the initialize response.
        var notifiedFrame = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await proxy.DispatchFrameAsync(notifiedFrame, connectionId, sessionId, default);

        // Unregister the agent stream BEFORE dispatching the first tools/call — its push is
        // guaranteed to be undeliverable (no local stream, NullRelayBackplane always false),
        // WITHOUT closing the session route (contrast with the grain-owned-cap test below).
        await routingTable.UnregisterAgentStreamAsync(connectionId);

        var firstRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(firstRequest, connectionId, sessionId, default);

        // Wait until the stub has actually answered frame 1 (so its — undeliverable — push has
        // been attempted) before re-registering; a small buffer covers the residual in-process
        // continuation between "stub wrote its response" and "grain attempted the push" (no
        // further I/O happens in that window, so this is not a real race in practice).
        await firstToolsCallHandled.Task.WaitAsync(PushTimeout);
        await Task.Delay(50);
        await routingTable.RegisterAgentStreamAsync(connectionId, writer, default);

        var secondRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(secondRequest, connectionId, sessionId, default);

        // Pre-fix: the worker `break`s after frame 1's failed (undeliverable) push, so it never
        // even dequeues frame 2 — this read would time out. Post-fix: a plain transient
        // undeliverable push must not kill the pipeline.
        var pushed = await writer.ReadNextAsync(PushTimeout);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, pushed.PayloadCase);
        var responseJson2 = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(2, responseJson2.GetProperty("id").GetInt32());
    }

    /// <summary>
    /// Fable-gate FIX 2 (T4 unhappy-path hardening, [BLOCKER], grain-owned-cap branch): unlike the
    /// "plain undeliverable" sibling test above, a GRAIN-OWNED 250 MiB session-hard-limit
    /// violation (Finding 16, M3) really does mean the session is closing —
    /// `CloseForResponsePayloadLimitAsync` already tears down `SessionRoutingTable`'s own route.
    /// But before this fix, `HttpMcpProxyGrain`'s OWN bookkeeping (`_consumers` and the
    /// consumer's inbox `Channel`) was untouched, so a LATER frame for the same
    /// `consumerSessionId` would still silently pile into a channel nobody drains (the worker
    /// already exited). This test drives enough distinct, real (bounded, per-message-legal)
    /// responses to cross the grain-owned cumulative cap, then sends ONE MORE frame and proves
    /// the worker was NOT wedged: the stale `ConsumerUpstream` was removed, so the later frame
    /// starts a genuinely FRESH upstream (observable via a second "initialize" hit — a fresh
    /// `HttpMcpClient` always re-runs its own-initialize fallback) and still gets a real response
    /// pushed back.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_GrainOwnedCapExceeded_RemovesConsumer_LaterFrameStartsFreshUpstream()
    {
        var initializeHits = 0;
        // Just under the 16 MiB per-message cap (so HttpMcpClient's own bounded read never trips)
        // — repeated across enough messages to cross the grain-owned 250 MiB SESSION cap.
        var chunk = new string('y', (int)Korat.Domain.Entities.PayloadLimitPolicy.DefaultPerMessageBytes - 4096);
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            if (method == "initialize")
            {
                Interlocked.Increment(ref initializeHits);
                await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
                return;
            }
            await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"{{{{chunk}}}}"}]}}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // Drive enough big (but individually per-message-legal) responses to cross the
        // grain-owned 250 MiB session cap. Uses the own-initialize fallback (no explicit
        // consumer handshake needed — mirrors the sibling oversized-response test above).
        GatewayToNodeMessage? capViolationPush = null;
        var messagesSent = 0;
        for (var i = 1; i <= 20 && capViolationPush is null; i++)
        {
            messagesSent = i;
            var req = Encoding.UTF8.GetBytes($$$$"""{"jsonrpc":"2.0","id":{{{{i}}}},"method":"tools/call","params":{}}""");
            await proxy.DispatchFrameAsync(req, connectionId, sessionId, default);
            var pushed = await writer.ReadNextAsync(PushTimeout);
            if (pushed.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.PayloadLimitExceeded)
                capViolationPush = pushed;
        }

        Assert.True(capViolationPush is not null, $"grain-owned session cap was never exceeded after {messagesSent} messages");
        Assert.Equal("session_hard_limit", capViolationPush!.PayloadLimitExceeded.LimitName);

        // CloseForResponsePayloadLimitAsync sends a SECOND message (CloseSession) right after —
        // drain it so it doesn't leak into the next ReadNextAsync call below.
        var closePushed = await writer.ReadNextAsync(PushTimeout);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, closePushed.PayloadCase);

        // FIX 2: a LATER frame for the SAME consumerSessionId must NOT silently pile into a
        // channel nobody drains — it must start a FRESH upstream (own-initialize fallback fires
        // again) and actually get a response pushed back, proving the worker was not left wedged.
        var hitsBeforeLaterFrame = initializeHits;
        var laterRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":999,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(laterRequest, connectionId, sessionId, default);
        var laterPushed = await writer.ReadNextAsync(PushTimeout);

        Assert.True(initializeHits > hitsBeforeLaterFrame, "a fresh upstream must re-run its own-initialize fallback");
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, laterPushed.PayloadCase);
        var laterResponseJson = JsonSerializer.Deserialize<JsonElement>(laterPushed.Frame.Ciphertext.ToByteArray());
        Assert.Equal(999, laterResponseJson.GetProperty("id").GetInt32());
    }

    /// <summary>Crux Finding 13: one-way dispatch means a slow consumer's in-flight upstream call
    /// must NOT block a concurrent, different consumer's call to the same grain from completing.</summary>
    [Fact]
    public async Task DispatchFrameAsync_SlowConsumer_DoesNotBlockConcurrentFastConsumer()
    {
        var slowGate = new TaskCompletionSource();
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.GetProperty("method").GetString();
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            var isSlow = reqDoc.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var n) && n.GetString() == "slow";
            if (isSlow && method == "tools/call")
                await slowGate.Task;
            ctx.Response.ContentType = "application/json";
            var responseJson = method == "initialize"
                ? $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}"""
                : $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"{{{{(isSlow ? "slow-ok" : "fast-ok")}}}}"}]}}""";
            await ctx.Response.WriteAsync(responseJson);
        });

        var server = await CreateServerAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // Two DIFFERENT consumer sessions against the SAME grain (same serverId) — per-session
        // upstream (Crux Finding 14) means these run independently, not serialized.
        var (slowSessionId, slowConnId, slowWriter) = await OpenConsumerConnectionAsync(server);
        var (fastSessionId, fastConnId, fastWriter) = await OpenConsumerConnectionAsync(server);

        var slowRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slow"}}""");
        var fastRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"fast"}}""");

        await proxy.DispatchFrameAsync(slowRequest, slowConnId, slowSessionId, default);
        await proxy.DispatchFrameAsync(fastRequest, fastConnId, fastSessionId, default);

        // The FAST call's push must arrive while the SLOW call is still gated open — proves
        // one-way dispatch does not serialize one consumer behind another.
        var fastPushed = await fastWriter.ReadNextAsync(PushTimeout);
        Assert.Contains("fast-ok", fastPushed.Frame.Ciphertext.ToStringUtf8());

        slowGate.SetResult();
        var slowPushed = await slowWriter.ReadNextAsync(PushTimeout);
        Assert.Contains("slow-ok", slowPushed.Frame.Ciphertext.ToStringUtf8());
    }

    /// <summary>
    /// Finding 16, M2: after a successful PATCH, the NEXT dispatched frame must be served by a
    /// grain activation that reloaded the config, not the stale one cached at first activation.
    ///
    /// Reality-over-plan deviation (flagged for lead review): the plan's literal scenario PATCHes
    /// `remoteUrl` to a second in-process stub and asserts the second dispatch reaches the NEW
    /// url. That scenario is untestable through the REAL PATCH endpoint in this repo: PATCH's
    /// `remoteUrl` field unconditionally runs `SsrfGuard.ValidateUrl` (`Endpoints.cs:298`, same
    /// gate as POST at `:216`) — HTTPS-only, port in {443,8443} only, no environment/AllowPrivateNetworks
    /// escape hatch (that escape hatch only exists on the CONNECT-time `SsrfGuardedHttpClientFactory`,
    /// not this registration-time static check). An in-process loopback Kestrel stub is `http://127.0.0.1:<random-port>`
    /// — it can never pass that check, so `client.PatchAsJsonAsync(..., new { remoteUrl = newStub.Url })`
    /// always 400s here regardless of whether Finding 16 M2's eviction fix is correct. Making it
    /// pass would require either weakening SsrfGuard (unacceptable — Global Constraints/Crux
    /// Finding 6 require reusing it verbatim) or standing up a trusted-cert HTTPS listener on a
    /// fixed 443/8443 port purely for this one test (disproportionate, security-adjacent
    /// complexity for a Task-4 grain test). Instead, this test proves the IDENTICAL staleness
    /// invariant via `authMode`/`authHeaderName` — a PATCH field that reaches the same
    /// unconditional `EvictAsync()` call (Endpoints.cs, right after `UpdateHttpCloudConfigAsync`)
    /// without ever touching `SsrfGuard.ValidateUrl` (that check is gated on `body.RemoteUrl is
    /// not null`, and this PATCH omits RemoteUrl entirely). The stub's own URL never changes, so
    /// the ONLY way the second dispatch's observed behavior can differ from the first is if the
    /// grain actually reloaded `AuthMode`/`AuthHeaderName` from Postgres — i.e., if it were served
    /// by a STALE (pre-PATCH) cached activation, dispatch 2 would behave IDENTICALLY to dispatch 1
    /// (AuthMode still "none", stub hit normally, real success response).
    /// </summary>
    [Fact]
    public async Task PatchAuthMode_EvictsGrain_NextDispatchUsesReloadedConfig()
    {
        var hits = 0;
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            Interlocked.Increment(ref hits);
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
        });

        // Reality-over-plan: the plan's Step 12 flagged (its own inline NOTE) that PATCH runs
        // against the CALLER's own default Space, and this test's server is created via
        // SeedUserAsync (a DIFFERENT user/Space than KoratIntegrationFixture.DevSpaceOwnerUserId)
        // — confirmed here: authenticate as the SEEDED owner so the BOLA check
        // (server.SpaceId == spaceResolver.ResolveDefaultSpaceIdAsync(userId)) actually matches;
        // authenticating as DevSpaceOwnerUserId would 404 (correctly — it doesn't own this server).
        var seeded = await fixture.SeedUserAsync($"http-proxy-patch-{Guid.NewGuid():N}@example.com", "HTTP Proxy Patch Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-patch-{Guid.NewGuid():N}", stub.Url, McpServerAuthModes.None, authHeaderName: null, secretHint: null);

        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        var (sessionId, connectionId, writer) = await OpenConsumerConnectionAsync(server);

        // First dispatch — activates the grain against AuthMode="none", establishing
        // OnActivateAsync's cache. Reaches the stub normally: a real success response.
        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}"""),
            connectionId, sessionId, default);
        var firstPushed = await writer.ReadNextAsync(PushTimeout);
        var firstResponseJson = JsonSerializer.Deserialize<JsonElement>(firstPushed.Frame.Ciphertext.ToByteArray());
        Assert.True(firstResponseJson.TryGetProperty("result", out _));
        Assert.Equal(1, hits);

        // PATCH authMode to "header" (no secret provided) via the real endpoint — Finding 16
        // M2's EvictAsync() must fire here. No RemoteUrl in the body, so SsrfGuard.ValidateUrl
        // is never invoked (its check is gated on `body.RemoteUrl is not null`).
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{server.Id.Value}",
            new { authMode = McpServerAuthModes.Header, authHeaderName = "X-Test-Auth" });
        Assert.Equal(System.Net.HttpStatusCode.OK, patchResp.StatusCode);

        // Second dispatch, a NEW consumer session — a freshly-reactivated grain (post-EvictAsync)
        // must have reloaded AuthMode="header" with no secret configured, so BuildResponseAsync's
        // authRequiredButMissing guard fires BEFORE ever calling upstream: a JSON-RPC error, and
        // the stub's hit count must NOT increase. A STALE (un-evicted) activation would still
        // think AuthMode="none" and dispatch normally, incrementing `hits` again — that's exactly
        // the bug this test would catch if Endpoints.cs's EvictAsync() call were missing/removed.
        var (sessionId2, connectionId2, writer2) = await OpenConsumerConnectionAsync(server);
        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}"""),
            connectionId2, sessionId2, default);
        var pushed = await writer2.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("error", out _));
        Assert.Equal(1, hits); // stub got NO additional hits — the reloaded grain never called upstream.
    }

    /// <summary>
    /// Fable holistic review FIX 2 [SHOULD-FIX]: `McpServerGrain.DisableAsync` calls `EvictAsync()`
    /// on this proxy grain, but a LATER frame on the SAME still-open consumer session simply
    /// reactivates it — `OnActivateAsync` reloads `_server` fresh from the repository, landing on
    /// `Status = Disabled`. Before the fix, `BuildResponseAsync` only checked `_server is null`, so
    /// it served the reactivated frame anyway: a real dial to the remote with the owner's
    /// decrypted Bearer secret, AFTER the owner turned the server off. This test proves: (1) the
    /// happy path still works before disable (one real upstream hit, observed auth header), (2) a
    /// frame dispatched on the SAME session after disable gets a clean JSON-RPC error, and (3) the
    /// upstream is NEVER dialed again — no second hit, no second auth-header observation — proving
    /// the secret was never sent post-disable.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_ServerDisabledAfterActivation_StopsServing_NeverDialsUpstreamAgain()
    {
        // Tracks only the "tools/call" leg — a single dispatched frame via the own-initialize
        // fallback path makes TWO upstream calls (a synthetic "initialize" then the real
        // "tools/call"); counting every method here would conflate that fallback's own -internal-
        // initialize hit with "the server was actually served again post-disable".
        var toolsCallHits = 0;
        string? observedAuthHeaderOnToolsCall = null;
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            if (method == "tools/call")
            {
                Interlocked.Increment(ref toolsCallHits);
                observedAuthHeaderOnToolsCall = ctx.Request.Headers.Authorization.ToString();
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.Bearer, "s3cr3t-token");
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // Happy path first — the server IS enabled, own-initialize fallback dials upstream for
        // real, secret included.
        var firstRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(firstRequest, connectionId, sessionId, default);
        var firstPushed = await writer.ReadNextAsync(PushTimeout);
        var firstResponseJson = JsonSerializer.Deserialize<JsonElement>(firstPushed.Frame.Ciphertext.ToByteArray());
        Assert.True(firstResponseJson.TryGetProperty("result", out _));
        Assert.Equal(1, toolsCallHits);
        Assert.Equal("Bearer s3cr3t-token", observedAuthHeaderOnToolsCall);

        // Owner disables the server — DisableAsync evicts THIS proxy grain activation.
        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).DisableAsync(Korat.Domain.Auth.UserId.New());

        // A LATER frame on the SAME still-open session reactivates the proxy grain — must load
        // Status=Disabled and refuse to serve, never dialing upstream with the secret again.
        observedAuthHeaderOnToolsCall = null;
        var secondRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(secondRequest, connectionId, sessionId, default);
        var secondPushed = await writer.ReadNextAsync(PushTimeout);
        var secondResponseJson = JsonSerializer.Deserialize<JsonElement>(secondPushed.Frame.Ciphertext.ToByteArray());

        Assert.True(secondResponseJson.TryGetProperty("error", out var error));
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.Equal(1, toolsCallHits); // stub's tools/call was NEVER dialed again post-disable.
        Assert.Null(observedAuthHeaderOnToolsCall); // the secret was never sent upstream after disable.
    }

    /// <summary>
    /// Fable holistic review FIX 3 [SHOULD-FIX]: `HttpMcpClient` used to stamp the pinned
    /// `2025-06-18` constant on every post-initialize request regardless of what the upstream
    /// actually negotiated. B2's pass-through forwards the CONSUMER's own "initialize" verbatim,
    /// so a consumer that negotiated an OLDER/different revision would still get the pinned
    /// constant sent upstream on every later call — a strict upstream that only agreed to its own
    /// negotiated revision could 400 every one of them. This stub's "initialize" response
    /// advertises "2025-03-26" (not the pinned constant); the very next upstream call (the
    /// "tools/call" triggered by the own-initialize fallback within the SAME dispatched frame) must
    /// carry `MCP-Protocol-Version: 2025-03-26`, not "2025-06-18".
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_UpstreamNegotiatesOlderProtocolVersion_SubsequentRequestEchoesNegotiatedVersion_NotPinnedConstant()
    {
        string? observedProtocolVersionOnToolsCall = null;
        using var stub = await StartStubMcpServerAsync(async ctx =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";

            if (method == "tools/call")
                observedProtocolVersionOnToolsCall = ctx.Request.Headers["MCP-Protocol-Version"].ToString();

            ctx.Response.ContentType = "application/json";
            string responseJson = method switch
            {
                // The upstream negotiates an OLDER revision than this client's own pinned constant.
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-03-26","capabilities":{}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"error":{"code":-32601,"message":"method not found"}}"""
            };
            await ctx.Response.WriteAsync(responseJson);
        });

        var (server, sessionId, connectionId, writer) = await SetUpConsumerSessionAsync(stub.Url, McpServerAuthModes.None, secret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        // Own-initialize fallback path (no consumer handshake needed) — a bare "tools/call"
        // triggers HttpMcpClient.InitializeWithOwnRequestAsync first (within THIS one dispatched
        // frame), which observes the upstream's negotiated "2025-03-26" from the initialize
        // response, then the actual "tools/call" upstream call must carry that negotiated value.
        var toolsCallRequest = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");
        await proxy.DispatchFrameAsync(toolsCallRequest, connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson2 = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
        Assert.True(responseJson2.TryGetProperty("result", out _));

        Assert.Equal("2025-03-26", observedProtocolVersionOnToolsCall);
    }

    private async Task<Domain.Entities.McpServer> CreateServerAsync(string remoteUrl, string authMode, string? secret)
    {
        var seeded = await fixture.SeedUserAsync($"http-proxy-{Guid.NewGuid():N}@example.com", "HTTP Proxy Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-proxy-{Guid.NewGuid():N}", remoteUrl, authMode, authHeaderName: null, secretHint: null);

        if (secret is not null)
        {
            // Reality-over-plan (mirrors WebMcpServerContractTests.CreateKekAwareAuthenticatedClientAsync,
            // Task 3): the shared fixture.Factory has NO envelope KEK configured (fail-closed by
            // design — IEnvelopeCrypto.EncryptAsync throws InvalidOperationException against
            // fixture.Services directly). Use a WithWebHostBuilder factory configured with
            // ThreadGrainTestKek — the SAME KEK the test SILO's own IEnvelopeCrypto uses
            // (SiloConfigurator.Configure, KoratTestHost.cs) — so HttpMcpProxyGrain (which
            // activates in the silo and decrypts this same ciphertext at OnActivateAsync) can
            // actually decrypt what gets written here.
            var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                    ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
                })));
            using var scope = kekFactory.Services.CreateScope();
            var envelopeCrypto = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
            var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
            var ciphertext = await envelopeCrypto.EncryptAsync(
                server.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.Aad(server.Id), secret, default);
            await repository.SetMcpServerSecretAsync(server.Id, ciphertext, "…oken", default);
        }
        return server;
    }

    /// <summary>
    /// Registers a fake agent connection stream directly with SessionRoutingTable and seeds an
    /// http_cloud route for a synthetic session — enough to observe HttpMcpProxyGrain's one-way
    /// push in isolation, without the full RequestSession/gRPC-stream/access-grant machinery
    /// (Task 5's HttpCloudMcpRoutingIntegrationTests already covers that end-to-end).
    /// </summary>
    private async Task<(SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)> OpenConsumerConnectionAsync(Domain.Entities.McpServer server)
    {
        var routingTable = fixture.Services.GetRequiredService<Korat.Cloud.Gateways.SessionRoutingTable>();
        var connectionId = ConnectionId.New();
        var sessionId = SessionId.New();
        var writer = new FakeAgentStreamWriter();

        await routingTable.RegisterAgentStreamAsync(connectionId, writer, default);
        routingTable.OpenSession(sessionId, NodeId.New(), new NodeId(string.Empty), server.Id, server.SpaceId,
            connectionId, payloadPolicy: null, isHttpCloud: true);

        return (sessionId, connectionId, writer);
    }

    private async Task<(Domain.Entities.McpServer Server, SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)> SetUpConsumerSessionAsync(
        string remoteUrl, string authMode, string? secret)
    {
        var server = await CreateServerAsync(remoteUrl, authMode, secret);
        var (sessionId, connectionId, writer) = await OpenConsumerConnectionAsync(server);
        return (server, sessionId, connectionId, writer);
    }

    private static async Task<StubMcpServer> StartStubMcpServerAsync(Func<HttpContext, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var app = builder.Build();
        app.MapPost("/", handler);
        await app.StartAsync();
        var url = app.Urls.First();
        return new StubMcpServer(app, url);
    }

    private sealed class StubMcpServer(WebApplication app, string url) : IDisposable
    {
        public string Url => url;
        public void Dispose() => app.StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>Captures GatewayToNodeMessage writes so a one-way grain push can be awaited/asserted on.</summary>
    private sealed class FakeAgentStreamWriter : IAsyncStreamWriter<GatewayToNodeMessage>
    {
        private readonly Channel<GatewayToNodeMessage> _channel = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(GatewayToNodeMessage message)
        {
            _channel.Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public async Task<GatewayToNodeMessage> ReadNextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _channel.Reader.ReadAsync(cts.Token);
        }
    }
}
