using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Korat.Cloud.Mcp.Http;

namespace Korat.Auth.Tests;

public sealed class HttpMcpClientNotificationTests
{
    private const string Changed = """{"jsonrpc":"2.0","method":"notifications/resources/list_changed"}""";
    private const string Updated = """{"jsonrpc":"2.0","method":"notifications/resources/updated","params":{"uri":"test://item"}}""";
    private const string Result = """{"jsonrpc":"2.0","id":7,"result":{}}""";

    [Fact]
    public async Task PostSse_ForwardsInterimNotificationBeforeMatchingResult()
    {
        var received = new List<string>();
        using var client = Client((_, _) => Task.FromResult(Sse(Changed, Updated, Result)),
            (message, _) => { received.Add(message.Method!); return Task.CompletedTask; });
        var result = await client.SendAsync(HttpMcpMessage.Request(7, "resources/read"), null, 4096, default);
        Assert.Equal(7, result.Id!.GetValue<int>());
        Assert.Equal(new[] { "notifications/resources/list_changed", "notifications/resources/updated" }, received);
    }

    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    public async Task GetSse_UsesSessionNegotiatedVersionAndAuth_AndForwardsOnlySupportedNotifications(string version)
    {
        var received = new List<HttpMcpMessage>();
        var gets = 0;
        using var client = Client((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"{{{version}}}"}}""", Encoding.UTF8, "application/json")
                };
                response.Headers.Add("Mcp-Session-Id", "upstream-session-secret");
                return Task.FromResult(response);
            }
            gets++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal(version, Assert.Single(request.Headers.GetValues("MCP-Protocol-Version")));
            Assert.Equal("upstream-session-secret", Assert.Single(request.Headers.GetValues("Mcp-Session-Id")));
            Assert.Equal("Bearer upstream-token", request.Headers.Authorization!.ToString());
            return Task.FromResult(Sse(Changed, Updated,
                """{"jsonrpc":"2.0","method":"notifications/prompts/list_changed"}""",
                """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""",
                """{"jsonrpc":"2.0","id":9,"method":"notifications/tools/list_changed"}""",
                """{"jsonrpc":"2.0","method":"notifications/message","params":{"data":"upstream-token"}}""",
                """{"jsonrpc":"2.0","method":"notifications/resources/updated","params":{}}""", Result));
        }, (message, _) => { received.Add(message); return Task.CompletedTask; });
        await client.InitializeWithOwnRequestAsync(null, 4096, default);
        await client.ListenForNotificationsAsync(r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "upstream-token"), 1024, 4096, default);
        Assert.Equal(1, gets);
        Assert.Equal(4, received.Count);
        Assert.Equal("test://item", received[1].Root["params"]!["uri"]!.GetValue<string>());
        Assert.DoesNotContain("upstream-token", string.Join("", received));
        Assert.DoesNotContain("upstream-session-secret", string.Join("", received));
    }

    [Fact]
    public async Task Get405_IsNormalAndDoesNotReconnect()
    {
        var gets = 0;
        using var client = Client((_, _) => { gets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)); });
        await client.ListenForNotificationsAsync(null, 128, 1024, default);
        Assert.Equal(1, gets);
    }

    [Fact]
    public async Task GetSse_CanBeReopenedAfterEof_WithoutParallelReaders()
    {
        var gets = 0;
        var notifications = 0;
        using var client = Client((_, _) => { gets++; return Task.FromResult(Sse(Changed)); },
            (_, _) => { notifications++; return Task.CompletedTask; });
        await client.ListenForNotificationsAsync(null, 1024, 4096, default);
        await client.ListenForNotificationsAsync(null, 1024, 4096, default);
        Assert.Equal(2, gets);
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetSse_CancellationOrDisposalCancelsReadAndDisposesResponse(bool dispose)
    {
        var stream = new BlockingStream();
        using var cts = new CancellationTokenSource();
        using var client = Client((_, _) => Task.FromResult(StreamResponse(stream)));
        var listen = client.ListenForNotificationsAsync(null, 1024, 4096, cts.Token);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) client.Dispose(); else cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sse_UnterminatedOversizedLineIsRejectedBeforeBufferingWholeBody(bool get)
    {
        using var client = Client((_, _) => Task.FromResult(StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', 20000))))));
        await Assert.ThrowsAsync<HttpMcpUpstreamException>(async () =>
        {
            if (get) await client.ListenForNotificationsAsync(null, 128, 100000, default);
            else await client.SendAsync(HttpMcpMessage.Request(7, "resources/read"), null, 128, default);
        });
    }

    [Fact]
    public async Task GetSse_TotalStreamBytesRemainBoundedAcrossSmallEvents()
    {
        using var client = Client((_, _) => Task.FromResult(Sse(Enumerable.Repeat(Changed, 30).ToArray())));
        await Assert.ThrowsAsync<HttpMcpUpstreamException>(() => client.ListenForNotificationsAsync(null, 128, 256, default));
    }

    [Fact]
    public async Task Notification_CanonicalJsonExpansionStillObeysMessageByteLimit()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/resources/updated\",\"params\":{\"uri\":\"test://" + new string('é', 40) + "\"}}";
        var rawBytes = Encoding.UTF8.GetByteCount($"data: {json}\n\n");
        var delivered = 0;
        using var client = Client((_, _) => Task.FromResult(Sse(json)), (_, _) => { delivered++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<HttpMcpUpstreamException>(() => client.ListenForNotificationsAsync(null, rawBytes, 4096, default));
        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task GetSse_RejectsConcurrentReader_AndCancellationReleasesIt()
    {
        var stream = new BlockingStream();
        using var cts = new CancellationTokenSource();
        using var client = Client((_, _) => Task.FromResult(StreamResponse(stream)));
        var first = client.ListenForNotificationsAsync(null, 1024, 4096, cts.Token);
        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListenForNotificationsAsync(null, 1024, 4096, default));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task PostSse_AwaitsNotificationDeliveryBeforeReturningResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => Task.FromResult(Sse(Changed, Result)), async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var send = client.SendAsync(HttpMcpMessage.Request(7, "resources/read"), null, 4096, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(send.IsCompleted);
        release.TrySetResult();
        Assert.Equal(7, (await send).Id!.GetValue<int>());
    }

    private static HttpMcpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle,
        Func<HttpMcpMessage, CancellationToken, Task>? notification = null)
        => new(new HttpClient(new Handler(handle)), "https://mcp.example.test/", notification);

    private static HttpResponseMessage Sse(params string[] events)
        => StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(events.Select(e => $"data: {e}\n\n")))));

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
