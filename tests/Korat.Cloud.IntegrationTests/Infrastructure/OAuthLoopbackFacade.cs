using System.Collections.Concurrent;
using Korat.Domain;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Fable plan-review, Blocker 1: a process-wide (static) registry mapping a fake HTTPS façade
/// host (e.g. "oauth-stub-3f2a91a4.test") to a REAL http://127.0.0.1:{port} loopback base URI.
/// Static/process-wide — NOT per-DI-container — because the silo (Orleans TestCluster) and the
/// web host (WebApplicationFactory) are two SEPARATE DI containers in the same test process
/// (confirmed: KoratTestHost.cs's SiloConfigurator vs ConfigureWebHost) but run in the SAME
/// process, so a static registry is visible to both without any new cross-container wiring.
/// Public (not internal) so both Korat.Cloud.IntegrationTests and Korat.Cloud.ContractTests (which
/// already share KoratIntegrationFixture/ThreadGrainTestKek across the same project boundary) can
/// reference it.
/// </summary>
public static class OAuthFacadeHostRegistry
{
    private static readonly ConcurrentDictionary<string, Uri> Map = new();

    /// <summary>Registers a fresh, collision-free façade host for one stub server instance.
    /// Call once per stub, right after it starts listening; unregister on stub disposal.</summary>
    public static string Register(Uri realLoopbackBaseUri)
    {
        var facadeHost = $"oauth-stub-{Guid.NewGuid():N}.test";
        Map[facadeHost] = realLoopbackBaseUri;
        return facadeHost;
    }

    public static void Unregister(string facadeHost) => Map.TryRemove(facadeHost, out _);

    public static bool TryResolve(string facadeHost, out Uri realLoopbackBaseUri) =>
        Map.TryGetValue(facadeHost, out realLoopbackBaseUri!);
}

/// <summary>
/// Rewrites a request's scheme/host/port from a registered façade to the real loopback target
/// immediately before it would otherwise be dialed, then sends it directly (the whole point of
/// the façade is that these specific requests never need production SSRF/DNS machinery a second
/// time — SsrfGuard.ValidateUrl has ALREADY run, in application code, on the pre-rewrite façade
/// URL string, which is the thing actually under test). Any request whose host is NOT a registered
/// façade is passed through UNCHANGED to the wrapped real factory's client — every pre-existing
/// SSRF/outbound test is completely unaffected.
///
/// IMPLEMENTATION NOTE (bug found + fixed while transcribing the plan's reference code, self-test
/// Step 5): this handler is itself the PRIMARY handler of an outer <see cref="HttpClient"/> (built
/// by <see cref="OAuthFacadeOutboundHttpClientFactory.CreateClient"/>). That outer HttpClient's own
/// SendAsync marks the HttpRequestMessage instance "sent" BEFORE ever reaching this override — so
/// forwarding that SAME instance through a second, inner HttpClient's SendAsync (either
/// DirectLoopbackClient for a façade hit, or the wrapped `passthrough` client otherwise) throws
/// "The request message was already sent" (System.InvalidOperationException) on EVERY call, façade
/// or not. Fixed by cloning the request into a fresh HttpRequestMessage before each inner send —
/// the clone reuses the same Content instance (untouched/unread at this point, safe to share) and
/// copies headers, so no observable request shape changes.
/// </summary>
internal sealed class OAuthFacadeRewritingHandler(HttpClient passthrough) : HttpMessageHandler
{
    private static readonly HttpClient DirectLoopbackClient = new(new HttpClientHandler());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not null && OAuthFacadeHostRegistry.TryResolve(request.RequestUri.Host, out var real))
        {
            var rewrittenUri = new UriBuilder(request.RequestUri) { Scheme = real.Scheme, Host = real.Host, Port = real.Port }.Uri;
            var forward = CloneForResend(request);
            forward.RequestUri = rewrittenUri;
            return await DirectLoopbackClient.SendAsync(forward, ct);
        }
        return await passthrough.SendAsync(CloneForResend(request), ct);
    }

    /// <summary>
    /// A fresh HttpRequestMessage carrying the same method/URI/version/headers/content as
    /// <paramref name="original"/> — needed because HttpClient.SendAsync refuses to send the same
    /// HttpRequestMessage instance twice (see the class doc comment above). Content is reused by
    /// reference (not cloned) — safe here because the outer HttpClient that owns
    /// <paramref name="original"/> has not yet serialized/read it (this handler runs before any
    /// actual network I/O), and the caller's own `using` on <paramref name="original"/> remains the
    /// single owner responsible for eventually disposing that Content — this clone is deliberately
    /// left undisposed so it never double-disposes the shared Content.
    /// </summary>
    private static HttpRequestMessage CloneForResend(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            Content = original.Content,
        };
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}

/// <summary>
/// Test-only IOutboundHttpClientFactory decorator wrapping the real, wrapped factory. Registered
/// ONCE in each container's shared fixture DI (KoratTestHost.cs's SiloConfigurator AND
/// ConfigureWebHost — see the Step below) — not per-test — so no individual OAuth test needs its
/// own WithWebHostBuilder override; it only needs to call OAuthFacadeHostRegistry.Register/
/// Unregister around its own stub server's lifetime.
/// </summary>
public sealed class OAuthFacadeOutboundHttpClientFactory(IOutboundHttpClientFactory realFactory) : IOutboundHttpClientFactory
{
    public HttpClient CreateClient(string purposeLabel) =>
        new(new OAuthFacadeRewritingHandler(realFactory.CreateClient(purposeLabel)));
}
