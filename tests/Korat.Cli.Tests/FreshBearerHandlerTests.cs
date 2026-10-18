using System.Net;
using Korat.Cli.Auth;

namespace Korat.Cli.Tests;

/// <summary>
/// The bridge must refresh its credential without crossing the cloud and issuer boundaries
/// captured when its long-lived HTTP client was created.
///
/// This only became a bug with the move to the sign-in provider. The old hub credential lived
/// 90 days, so taking it once at startup was harmless. A provider token lives hours while the
/// bridge in Claude Desktop lives days: with a snapshot it stops refreshing servers and grants
/// an hour in, silently — sessions keep working, so it reads as "new grants don't arrive"
/// rather than "sign in again".
/// </summary>
public sealed class FreshBearerHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "korat-fresh-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Every_request_carries_the_current_credential()
    {
        var dir = _directory;
        var store = new CredentialStore(dir);

        var first = Sample("token-one");
        await store.SaveAsync(first, CancellationToken.None);

        var seen = new List<string?>();
        var inner = new CapturingHandler(seen);
        using var http = new HttpClient(new FreshBearerHandler(store, first, inner))
        {
            BaseAddress = new Uri("https://example.invalid/"),
        };

        await http.GetAsync("first");

        // The file changes under a long-lived client — exactly what a refresh does.
        await store.SaveAsync(Sample("token-two"), CancellationToken.None);
        await http.GetAsync("second");

        Assert.Equal(["Bearer token-one", "Bearer token-two"], seen);
    }

    [Fact]
    public async Task Credential_for_another_hub_is_not_sent_to_the_bound_hub()
    {
        var dir = _directory;
        var store = new CredentialStore(dir);
        var original = Sample("old-hub-token", cloudUrl: "https://old.example.invalid");
        await store.SaveAsync(
            Sample("new-hub-token", cloudUrl: "https://new.example.invalid"),
            CancellationToken.None);

        var seen = new List<string?>();
        using var http = CreateClient(store, original, seen);

        await http.GetAsync("space");

        Assert.Equal(["Bearer old-hub-token"], seen);
    }

    [Fact]
    public async Task Credential_from_another_issuer_is_not_used_for_the_bound_client()
    {
        var dir = _directory;
        var store = new CredentialStore(dir);
        var original = Sample("original-token", issuer: "https://id.example.invalid/tenant-a/");
        await store.SaveAsync(
            Sample("other-issuer-token", issuer: "https://id.example.invalid/tenant-b/"),
            CancellationToken.None);

        var seen = new List<string?>();
        using var http = CreateClient(store, original, seen);

        await http.GetAsync("space");

        Assert.Equal(["Bearer original-token"], seen);
    }

    [Fact]
    public async Task Canonically_equivalent_hub_and_issuer_accept_a_refreshed_credential()
    {
        var dir = _directory;
        var store = new CredentialStore(dir);
        var original = Sample(
            "original-token",
            cloudUrl: "HTTPS://EXAMPLE.invalid:443/original-path",
            issuer: "HTTPS://ID.KORAT.DEV:443");
        await store.SaveAsync(
            Sample(
                "refreshed-token",
                cloudUrl: "https://example.invalid/another-path/",
                issuer: "https://id.korat.dev/"),
            CancellationToken.None);

        var seen = new List<string?>();
        using var http = CreateClient(store, original, seen);

        await http.GetAsync("space");

        Assert.Equal(["Bearer refreshed-token"], seen);
    }

    [Theory]
    [InlineData("https://other.example.invalid/space")]
    [InlineData("http://example.invalid/space")]
    [InlineData("https://example.invalid:444/space")]
    public async Task Absolute_request_to_another_origin_is_rejected_before_sending_a_credential(
        string requestUrl)
    {
        var dir = _directory;
        var store = new CredentialStore(dir);
        var original = Sample("bound-token");
        await store.SaveAsync(original, CancellationToken.None);

        var seen = new List<string?>();
        using var http = CreateClient(store, original, seen);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => http.GetAsync(requestUrl));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task An_unreadable_store_falls_back_instead_of_failing_the_call()
    {
        var dir = _directory;

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "credentials"), "{not-json");

        // The bridge already holds a working credential, and throwing here would surface as a
        // failure in a place nobody connects to sign-in.
        var seen = new List<string?>();
        using var http = CreateClient(new CredentialStore(dir), Sample("fallback"), seen);

        await http.GetAsync("only");

        Assert.Equal(["Bearer fallback"], seen);
    }

    [Fact]
    public async Task Cancellation_during_store_refresh_is_not_swallowed_or_forwarded()
    {
        var dir = _directory;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CredentialStore(
            dir,
            async (_, ct) =>
            {
                refreshStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            });
        var expired = Sample("expired") with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        await store.SaveAsync(expired, CancellationToken.None);

        var seen = new List<string?>();
        using var http = CreateClient(store, expired, seen);
        using var cts = new CancellationTokenSource();

        var request = http.GetAsync("space", cts.Token);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Empty(seen);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static HttpClient CreateClient(
        CredentialStore store,
        CliCredentials fallback,
        List<string?> seen) => new(new FreshBearerHandler(store, fallback, new CapturingHandler(seen)))
        {
            BaseAddress = new Uri(fallback.CloudUrl.TrimEnd('/') + "/"),
        };

    private static CliCredentials Sample(
        string accessToken,
        string cloudUrl = "https://example.invalid",
        string? issuer = "https://id.korat.dev/") => new(
        AccessToken: accessToken,
        Scope: "full",
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        CloudUrl: cloudUrl,
        RefreshToken: "r",
        Issuer: issuer);

    private sealed class CapturingHandler(List<string?> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
