using System.Net;
using Korat.Cli.Auth;

namespace Korat.Cli.Tests;

/// <summary>
/// The bridge must not carry a snapshot of the credential.
///
/// This only became a bug with the move to the sign-in provider. The old hub credential lived
/// 90 days, so taking it once at startup was harmless. A provider token lives hours while the
/// bridge in Claude Desktop lives days: with a snapshot it stops refreshing servers and grants
/// an hour in, silently — sessions keep working, so it reads as "new grants don't arrive"
/// rather than "sign in again".
/// </summary>
public sealed class FreshBearerHandlerTests
{
    [Fact]
    public async Task Every_request_carries_the_current_credential()
    {
        var dir = Path.Combine(Path.GetTempPath(), "korat-fresh-" + Guid.NewGuid().ToString("N"));
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
    public async Task An_unreadable_store_falls_back_instead_of_failing_the_call()
    {
        var dir = Path.Combine(Path.GetTempPath(), "korat-fresh-" + Guid.NewGuid().ToString("N"));

        // Nothing saved: LoadAsync returns null. The bridge already holds a working credential,
        // and throwing here would surface as a failure in a place nobody connects to sign-in.
        var seen = new List<string?>();
        using var http = new HttpClient(
            new FreshBearerHandler(new CredentialStore(dir), Sample("fallback"), new CapturingHandler(seen)))
        {
            BaseAddress = new Uri("https://example.invalid/"),
        };

        await http.GetAsync("only");

        Assert.Equal(["Bearer fallback"], seen);
    }

    private static CliCredentials Sample(string accessToken) => new(
        AccessToken: accessToken,
        Scope: "full",
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        CloudUrl: "https://example.invalid",
        RefreshToken: "r",
        Issuer: "https://id.korat.dev/");

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
