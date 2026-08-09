using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Korat.Cloud.Mcp.Space;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// MUST-FIX 2 (adversarial review, Space-MCP increment 1 Tasks 7-8): before the fix, a GET-SSE
/// watch stream with nothing to report (a heartbeat/unchanged <c>NextListChangedAsync</c> return)
/// wrote ZERO bytes after the initial response headers. A real Fly edge proxy severs a connection
/// idle for ~60s of true silence, which this behaviour guaranteed on any quiet Space. The fix
/// writes an SSE comment line (<c>": keepalive\n\n"</c>) on every heartbeat iteration instead —
/// RFC-legal (any line starting with <c>:</c> is a comment, ignored by every real SSE parser,
/// never surfaced as a <c>message</c> event) — so bytes keep flowing without ever emitting a
/// semantically-meaningful notification.
///
/// This test opens a GET-SSE stream against a session with an EMPTY catalog (no published
/// servers — nothing will ever generate a real <c>list_changed</c>) and asserts at least one
/// keepalive comment line arrives within a bounded wait, proving the stream is not silent.
/// <see cref="SpaceMcpAggregatorGrain.ListChangedHeartbeat"/> is shrunk for the duration of the
/// test (same test-shrink precedent as <c>SpaceMcpListChangedTests</c>).
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpSseKeepAliveTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QuietSession_GetSse_EmitsKeepAliveComment_NotSilence()
    {
        var originalHeartbeat = SpaceMcpAggregatorGrain.ListChangedHeartbeat;
        SpaceMcpAggregatorGrain.ListChangedHeartbeat = TimeSpan.FromSeconds(1);
        try
        {
            var seeded = await fixture.SeedUserAsync(
                $"space-mcp-keepalive-{Guid.NewGuid():N}@example.com", "Space MCP SSE KeepAlive");
            // Р25: the endpoint accepts OAuth only — bearer from the real flow.
            var (token, _) =
                await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
            var client = fixture.Factory.CreateClient();

            var sessionId = await InitializeSessionAsync(client, seeded.SpaceId, token);

            var firstLine = await WaitForFirstNonEmptyLineAsync(seeded.SpaceId, token, sessionId, WaitTimeout);

            Assert.NotNull(firstLine);
            Assert.StartsWith(":", firstLine);
            Assert.DoesNotContain("notifications/tools/list_changed", firstLine, StringComparison.Ordinal);
        }
        finally
        {
            SpaceMcpAggregatorGrain.ListChangedHeartbeat = originalHeartbeat;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static async Task<string> InitializeSessionAsync(HttpClient client, string spaceSeg, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var values));
        return Assert.Single(values);
    }

    /// <summary>Opens a GET-SSE stream and returns the first non-empty line written to the body
    /// (bounded by <paramref name="timeout"/>), or <c>null</c> on timeout/early close — never
    /// throws.</summary>
    private async Task<string?> WaitForFirstNonEmptyLineAsync(
        string spaceSeg, string token, string sessionId, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/mcp/{spaceSeg}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var client = fixture.Factory.CreateClient();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                    return null; // stream closed before anything arrived.
                if (line.Length > 0)
                    return line;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
