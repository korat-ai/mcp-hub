using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using Korat.Relay.V1;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1, Tasks 4-6): a test-only fake MCP publisher that connects over the
/// REAL gRPC relay (mirrors <c>RelayFrameForwardingTests.ConnectAsync</c> /
/// <c>ConnectAccessRequestTests</c>'s publisher-side helpers) and answers
/// <c>initialize</c>/<c>tools/list</c>/<c>tools/call</c> with canned JSON — so the aggregator
/// integration tests can seed a real granted backend session without spawning a real subprocess
/// MCP server. The aggregator (<c>SpaceMcpAggregatorGrain</c>/<c>SpaceBackendSession</c>) only
/// ever talks to this publisher through the SAME relay frames it would use against a real one —
/// nothing here is a test seam inside the grain itself.
/// </summary>
internal sealed class FakeMcpPublisher : IAsyncDisposable
{
    private readonly AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> _call;
    private readonly Task _pumpTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();
    private readonly ConcurrentQueue<(string SessionId, string Reason)> _closeSessions = new();
    private long _seq;

    /// <summary>
    /// MUST-FIX 1 teardown tests: snapshot of every relay <c>SessionId</c> this publisher has
    /// seen a data Frame for — the aggregator's real backend session id, learned by observation
    /// rather than by reaching into the grain's internals.
    /// </summary>
    public IReadOnlyCollection<string> SeenSessionIds => _buffers.Keys.ToList();

    /// <summary>
    /// MUST-FIX 1 teardown tests: CloseSession control frames pushed to THIS publisher so far
    /// (by <c>SessionTerminator.TerminateSessionAsync</c> via <c>SendToNodeAsync</c>). Proves the
    /// aggregator's real backend-session teardown reaches the publisher, not just the aggregator's
    /// own local grain state.
    /// </summary>
    public IReadOnlyList<(string SessionId, string Reason)> ReceivedCloseSessions => _closeSessions.ToArray();

    /// <summary>Polls until a CloseSession for <paramref name="sessionId"/> arrives or times out.</summary>
    public async Task<bool> WaitForCloseSessionAsync(string sessionId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            if (_closeSessions.Any(c => c.SessionId == sessionId)) return true;
            if (cts.IsCancellationRequested) return false;
            try { await Task.Delay(25, cts.Token); }
            catch (OperationCanceledException) { return false; }
        }
    }

    public IReadOnlyList<(string Name, string? Description, string? InputSchemaJson)> Tools { get; }

    /// <summary>Task 5: when true, "tools/list" is never answered — simulates a hung backend so
    /// the per-backend timeout can be proven not to stall the other, well-behaved backends.</summary>
    public bool HangOnToolsList { get; set; }

    /// <summary>Task 6: optional "tools/call" responder — (toolName, argumentsJson) -> resultJson.
    /// Defaults to a fixed echo-style success result when unset.</summary>
    public Func<string, JsonObject?, string>? ToolCallHandler { get; set; }

    /// <summary>MUST-FIX 1 test support (adversarial review, third pass): artificial delay applied
    /// before answering "tools/call" — simulates a legitimately slow backend operation (build/
    /// test/shell — the product's own headline `tools/call` use case) so a test can prove such a
    /// call still round-trips through <c>SpaceMcpDispatcher</c> rather than the grain-call
    /// boundary throwing at Orleans' un-overridden 30s default response timeout. Deliberately a
    /// real <c>Task.Delay</c> on this publisher's own async pump (not a synchronous
    /// <c>Thread.Sleep</c> inside <see cref="ToolCallHandler"/>), so it delays only the REPLY,
    /// never blocks anything else this fake publisher is doing concurrently.</summary>
    public TimeSpan ToolCallDelay { get; set; } = TimeSpan.Zero;

    private FakeMcpPublisher(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call,
        IReadOnlyList<(string Name, string? Description, string? InputSchemaJson)> tools)
    {
        _call = call;
        Tools = tools;
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public static async Task<FakeMcpPublisher> ConnectAsync(
        KoratTestHost factory,
        string publisherNodeId,
        string cliToken,
        IReadOnlyList<(string Name, string? Description, string? InputSchemaJson)> tools)
    {
        var grpcClient = GrpcTestClient.Create(factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = publisherNodeId,
                DisplayName = "fake-mcp-publisher",
                // NodeKind intentionally omitted (empty) — a publisher, not an agent bridge.
            }
        });

        using var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var moved = await call.ResponseStream.MoveNext(ackCts.Token);
        if (!moved || call.ResponseStream.Current.PayloadCase != GatewayToNodeMessage.PayloadOneofCase.Hello)
            throw new InvalidOperationException("FakeMcpPublisher: expected a Hello ack from the gateway.");

        return new FakeMcpPublisher(call, tools);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _call.ResponseStream.MoveNext(ct))
            {
                var msg = _call.ResponseStream.Current;
                switch (msg.PayloadCase)
                {
                    case GatewayToNodeMessage.PayloadOneofCase.Frame:
                        await HandleFrameAsync(msg.Frame, ct);
                        break;
                    case GatewayToNodeMessage.PayloadOneofCase.CloseSession:
                        // MUST-FIX 1 test support: record the CloseSession control frame the
                        // gateway pushes to the PUBLISHER end (SessionTerminator.BuildClose) so
                        // tests can prove the aggregator's real backend-session teardown reaches
                        // this publisher, not just flips the aggregator's own local grain state.
                        _closeSessions.Enqueue((msg.CloseSession.SessionId, msg.CloseSession.Reason));
                        break;
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync.
        }
        catch (RpcException)
        {
            // Stream torn down (session/grain teardown) — expected at test cleanup.
        }
    }

    private async Task HandleFrameAsync(RelayFrame frame, CancellationToken ct)
    {
        var sb = _buffers.GetOrAdd(frame.SessionId, _ => new StringBuilder());
        var lines = new List<string>();
        lock (sb)
        {
            sb.Append(frame.Ciphertext.ToStringUtf8());
            var text = sb.ToString();
            int start = 0, nl;
            while ((nl = text.IndexOf('\n', start)) >= 0)
            {
                lines.Add(text[start..nl]);
                start = nl + 1;
            }
            sb.Clear();
            if (start < text.Length) sb.Append(text, start, text.Length - start);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            await HandleLineAsync(frame.SessionId, line, ct);
        }
    }

    private async Task HandleLineAsync(string sessionId, string line, CancellationToken ct)
    {
        var json = JsonNode.Parse(line)!.AsObject();
        var method = json["method"]?.GetValue<string>();
        var id = json["id"];

        switch (method)
        {
            case "initialize":
                await ReplyAsync(sessionId, id,
                    """{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"fake-publisher","version":"1"}}""",
                    ct);
                break;

            case "notifications/initialized":
                break; // notification — no reply.

            case "tools/list":
                if (HangOnToolsList) return; // simulate a hung backend (Task 5).
                await ReplyAsync(sessionId, id, BuildToolsListResult(), ct);
                break;

            case "tools/call":
                if (ToolCallDelay > TimeSpan.Zero)
                    await Task.Delay(ToolCallDelay, ct);
                var name = json["params"]?["name"]?.GetValue<string>() ?? "";
                var args = json["params"]?["arguments"] as JsonObject;
                var resultJson = ToolCallHandler?.Invoke(name, args)
                    ?? """{"content":[{"type":"text","text":"ok"}]}""";
                await ReplyAsync(sessionId, id, resultJson, ct);
                break;

            default:
                // Unknown method from the aggregator side — ignore (mirrors a real MCP server
                // that would reply -32601, but no test currently drives this path).
                break;
        }
    }

    private string BuildToolsListResult()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchemaJson is not null
                    ? JsonNode.Parse(t.InputSchemaJson)
                    : new JsonObject { ["type"] = "object" },
            });
        }
        return new JsonObject { ["tools"] = arr }.ToJsonString();
    }

    /// <summary>MUST-FIX 3 test support (adversarial review, third pass): sends a raw data frame
    /// with an arbitrary <c>Enc</c> value directly to the given relay <c>SessionId</c> — lets a
    /// test simulate <c>SpaceBackendSession.OnInboundBytesAsync</c>'s own <c>enc != 0</c>
    /// fail-closed guard (N3) without needing a real cipher/ciphertext, which Space-MCP backend
    /// sessions never use (forced <c>peer_supports_e2e=false</c> at admission).</summary>
    public async Task SendRawFrameAsync(string sessionId, byte[] payload, uint enc)
    {
        await _call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = (ulong)Interlocked.Increment(ref _seq),
                Direction = "server_to_client",
                Ciphertext = ByteString.CopyFrom(payload),
                Enc = enc,
            }
        });
    }

    private async Task ReplyAsync(string sessionId, JsonNode? id, string resultJson, CancellationToken ct)
    {
        var line = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = JsonNode.Parse(resultJson),
        }.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = (ulong)Interlocked.Increment(ref _seq),
                Direction = "server_to_client",
                Ciphertext = ByteString.CopyFrom(bytes),
                Enc = 0,
            }
        }).WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _call.RequestStream.CompleteAsync(); }
        catch { /* best-effort */ }
        try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort */ }
        _call.Dispose();
    }
}
