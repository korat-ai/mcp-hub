using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Grpc.Core;
using Korat.Cloud.Mcp.Oauth;
using Korat.Cloud.Security.Envelope;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 2, Task 5: HttpMcpProxyGrain's oauth path against a real in-process Kestrel stub
/// serving BOTH the MCP resource (/mcp) and the token endpoint (/token) — nothing requires a
/// test double to physically separate them (Grounding Note 9). Covers Bearer injection from the
/// stored token, proactive refresh near expiry, reactive refresh on a 401, single-flight refresh
/// under N concurrent dispatches, rotation persisted before use, invalid_grant → NeedsReauth,
/// 5xx → status UNCHANGED, oauth-missing-token fail-closed, and the egress concurrency cap.
///
/// Reality-over-plan deviation #1 (concurrency tests): the plan's literal
/// ConcurrentDispatches_NearExpiry_TriggerExactlyOneRefreshCall and
/// EgressCap_ExcessConcurrentConsumerCalls_AreBoundedNotUnbounded both dispatch N frames on the
/// SAME (connectionId, sessionId) pair. HttpMcpProxyGrain's Increment-1 per-consumer FIFO
/// (Finding 16, M6 — one Channel + exactly one drain-worker Task.Run PER CONSUMER SESSION) means
/// N frames on ONE session are processed strictly SEQUENTIALLY by that one worker — never
/// concurrently — so a single-session version of either test would pass trivially regardless of
/// whether the single-flight lock or the egress limiter actually work (there is nothing to race
/// or bound). The plan's own Step 8 note independently reaches this exact conclusion for the
/// egress test ("this test as written exercises only ONE consumer... proving nothing... open
/// EgressConcurrencyCeiling + 4 SEPARATE consumer sessions"). Applying the SAME fix uniformly to
/// BOTH concurrency tests below: each opens N SEPARATE consumer sessions against the SAME
/// server/grain activation, so N independent per-consumer worker Task.Run threads (real OS
/// threads, not serialized by Orleans' single-threaded turn — see HttpMcpProxyGrain's own class
/// doc comment) race for real. A request counter on the stub's /token route (StubServer.
/// TokenCallCount) gives a direct, deterministic assertion instead of the plan's indirect
/// "everyone observed the same new token" inference.
/// </summary>
public sealed class HttpMcpProxyGrainOAuthTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(10);

    // Must match HttpMcpProxyGrain's own private EgressConcurrencyCeiling constant.
    private const int EgressConcurrencyCeiling = 8;

    [Fact]
    public async Task DispatchFrameAsync_InjectsStoredAccessToken_AsBearerHeader()
    {
        string? observedAuth = null;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (ctx.Request.Path == "/mcp")
                observedAuth = ctx.Request.Headers.Authorization.ToString();
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[]}}""");
        });

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-live", refreshToken: "rt-live", accessExpiry: DateTimeOffset.UtcNow.AddHours(1),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("result", out _));
        Assert.Equal("Bearer at-live", observedAuth);
    }

    [Fact]
    public async Task DispatchFrameAsync_ProactiveRefresh_NearExpiry_UsesNewAccessToken()
    {
        var authHeadersSeen = new List<string>();
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (ctx.Request.Path == "/mcp")
                authHeadersSeen.Add(ctx.Request.Headers.Authorization.ToString());
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""");
        }, refreshedAccessToken: "at-refreshed");

        // Access token expires in 10s — well within the grain's proactive-refresh window.
        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-stale", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(10),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout);

        Assert.Contains("Bearer at-refreshed", authHeadersSeen);
        Assert.DoesNotContain("Bearer at-stale", authHeadersSeen);
    }

    /// <summary>
    /// Reality-over-plan deviation #2: `HttpMcpProxyGrain.BuildResponseAsync` wraps ALL THREE
    /// upstream request/response call sites (initialize pass-through, own-initialize fallback,
    /// AND the final send) in `SendWithOAuthRetryAsync`, not just the final send — a stale token
    /// is just as likely to surface on the FIRST upstream call a freshly-activated consumer ever
    /// makes (the own-initialize fallback, Finding 16 B2's "not the normal path" branch) as on a
    /// later one, and the wrapping is a zero-cost passthrough for non-oauth servers (the retry's
    /// own `when (McpServerAuthModes.IsOAuth(...))` guard). This test isolates the assertion to
    /// the "tools/call" leg specifically (`toolsCallHits`, mirroring the SAME handshake-vs-call
    /// separation `HttpMcpProxyGrainTests.DispatchFrameAsync_ServerDisabledAfterActivation_
    /// StopsServing_NeverDialsUpstreamAgain` already uses) — the stub responds to ANY non-
    /// "tools/call" method (i.e. the own-initialize-fallback's synthetic "initialize") 200
    /// unconditionally, so the own-initialize fallback always succeeds on its first attempt
    /// regardless of the stale token, and the 401-then-refresh-then-retry is exercised exactly
    /// once, purely on the "tools/call" leg — the scenario the plan's own test intends.
    /// </summary>
    [Fact]
    public async Task DispatchFrameAsync_Reactive401_RefreshesOnceThenRetries_Succeeds()
    {
        var toolsCallHits = 0;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (method != "tools/call")
                return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""); // handshake traffic (own-initialize fallback) — always succeeds.
            toolsCallHits++;
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth == "Bearer at-expired")
                return (401, null);
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[]}}""");
        }, refreshedAccessToken: "at-fresh");

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-expired", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddHours(1), // NOT near expiry — proactive refresh must NOT fire; only the 401 does
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("result", out _)); // succeeded after ONE retry
        Assert.Equal(2, toolsCallHits); // the 401 attempt + the retry — not more
    }

    [Fact]
    public async Task RefreshFailure_InvalidGrant_FlipsToNeedsReauth()
    {
        using var stub = await StartStubAsync((ctx, method, id) => (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""),
            tokenEndpointBehavior: TokenEndpointBehavior.InvalidGrant);

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-stale", refreshToken: "rt-dead", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(5),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout); // refresh failed (invalid_grant) — Status is what this test actually cares about.

        var afterwards = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        Assert.Equal(McpServerStatus.NeedsReauth, afterwards.Status);
    }

    /// <summary>
    /// Final whole-feature fable gate, Finding 1 (composition defect T1&lt;-&gt;T5): builds on
    /// <see cref="RefreshFailure_InvalidGrant_FlipsToNeedsReauth"/>'s exact setup, then goes one
    /// step further than that test's Status-only assertion. `MarkNeedsReauthAsync` flips Status,
    /// but `McpServerGrain.EnableAsync` decides Published-vs-NeedsReauth on re-enable from
    /// `hasUsableOAuthToken`, which it computes as "the OAuth ciphertext row is non-null"
    /// (<c>repository.GetMcpServerOAuthTokenCiphertextAsync(...) is not null</c>) — NOT from
    /// Status. Before the fix, the dead ciphertext survived an invalid_grant refresh failure, so a
    /// later disable-&gt;enable saw it, concluded the grant was still usable, and re-Published a
    /// server whose refresh token the AS had just revoked — a lying catalog and a broken first
    /// session. This test confirms both halves: (a) the ciphertext is actually cleared, and (b) the
    /// observable consequence — disable then enable must leave the server at NeedsReauth, not
    /// silently resurrect it to Published.
    /// </summary>
    [Fact]
    public async Task RefreshFailure_InvalidGrant_ClearsDeadCiphertext_SoDisableThenEnableStaysNeedsReauth()
    {
        using var stub = await StartStubAsync((ctx, method, id) => (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""),
            tokenEndpointBehavior: TokenEndpointBehavior.InvalidGrant);

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-stale", refreshToken: "rt-dead", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(5),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout); // refresh failed (invalid_grant)

        var serverGrain = fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value);
        var afterRefresh = await serverGrain.GetAsync();
        Assert.Equal(McpServerStatus.NeedsReauth, afterRefresh.Status); // sanity, mirrors the existing test above

        // (a) the dead ciphertext must be gone, not merely the Status flipped.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
            var ciphertext = await repository.GetMcpServerOAuthTokenCiphertextAsync(server.Id, default);
            Assert.Null(ciphertext);
        }

        // (b) disable then re-enable — with the dead ciphertext cleared, EnableAsync's
        // hasUsableOAuthToken check must see "no usable token" and refuse to re-Published. The
        // userId param is unwired/unused by both DisableAsync and EnableAsync today (no audit
        // column yet — see McpServerGrain's own comment), so `default` is safe here.
        await serverGrain.DisableAsync(default);
        await serverGrain.EnableAsync(default);

        var afterEnable = await serverGrain.GetAsync();
        Assert.Equal(McpServerStatus.NeedsReauth, afterEnable.Status); // must NOT silently re-publish a dead grant
    }

    [Fact]
    public async Task RefreshFailure_TransientServerError_StatusUnchanged()
    {
        using var stub = await StartStubAsync((ctx, method, id) => (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""),
            tokenEndpointBehavior: TokenEndpointBehavior.Transient5xx);

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-stale", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(5),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout);

        var afterwards = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        // A transient AS outage must NOT brick the server into re-consent (spec §"Failure classification").
        Assert.Equal(McpServerStatus.Published, afterwards.Status);
    }

    [Fact]
    public async Task RefreshFailure_NoRefreshToken_ExpiryFlipsToNeedsReauth()
    {
        using var stub = await StartStubAsync((ctx, method, id) => (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""));

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-stale", refreshToken: null, accessExpiry: DateTimeOffset.UtcNow.AddSeconds(-5), // already expired
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout);

        var afterwards = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        Assert.Equal(McpServerStatus.NeedsReauth, afterwards.Status);
    }

    [Fact]
    public async Task OAuthServerWithNoStoredToken_FailsClosed_NeverDialsUpstream()
    {
        var mcpDialed = false;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (ctx.Request.Path == "/mcp") mcpDialed = true;
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""");
        });

        var seeded = await fixture.SeedUserAsync($"oauth-missing-{Guid.NewGuid():N}@example.com", "Missing Token");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-missing-{Guid.NewGuid():N}", $"{stub.Url}/mcp", McpServerAuthModes.Oauth, null, null);
        // NO token ever stored — the row stays NeedsReauth (create-time default).
        var (sessionId, connectionId, writer) = await OpenConsumerConnectionAsync(server);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("error", out var error));
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.False(mcpDialed); // fail-closed BEFORE ever reaching the resource server, per Status != Published guard
    }

    /// <summary>
    /// Task 5 hardening: property 4 (persist-before-use) previously had NO store assertion — the
    /// existing refresh tests only ever checked the auth header observed by the SAME (still-live)
    /// activation that performed the refresh, which would pass even if `_oauthToken = updated`
    /// happened BEFORE the `SetMcpServerOAuthTokenAsync` write (the opposite of what
    /// `DoRefreshOAuthTokenAsync`'s own doc comment promises). This test forces a real reload: a
    /// proactive near-expiry refresh runs and rotates the token, then `EvictAsync()` discards the
    /// activation entirely (and with it, the in-memory `_oauthToken` field), then a BRAND-NEW
    /// consumer session dispatches on a freshly reactivated grain — `OnActivateAsync` can only ever
    /// observe the rotated token by decrypting what actually landed in the repository.
    /// </summary>
    [Fact]
    public async Task RefreshedToken_PersistedBeforeUse_SurvivesEvictAndReactivation()
    {
        var authHeadersSeen = new List<string>();
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (ctx.Request.Path == "/mcp")
                authHeadersSeen.Add(ctx.Request.Headers.Authorization.ToString());
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""");
        }, refreshedAccessToken: "at-persisted-rotated");

        // Access token expires in 10s — well within the grain's proactive-refresh window, so the
        // very FIRST upstream call this activation ever makes already carries the rotated token
        // (proactive refresh runs before dialing upstream at all — the stale "at-old" is never sent).
        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-old", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(10),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        await writer.ReadNextAsync(PushTimeout);
        Assert.Contains("Bearer at-persisted-rotated", authHeadersSeen); // sanity: the refresh actually happened.
        Assert.DoesNotContain("Bearer at-old", authHeadersSeen); // proactive refresh fires BEFORE any upstream call.

        // Discard this activation entirely — the ONLY way a fresh activation can still observe the
        // rotated token is if it was durably persisted, not merely held in the now-gone in-memory field.
        await proxy.EvictAsync();

        var (sessionId2, connectionId2, writer2) = await OpenConsumerConnectionAsync(server);
        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}"""),
            connectionId2, sessionId2, default);
        var pushed2 = await writer2.ReadNextAsync(PushTimeout);
        var responseJson2 = JsonSerializer.Deserialize<JsonElement>(pushed2.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson2.TryGetProperty("result", out _));
        Assert.Equal("Bearer at-persisted-rotated", authHeadersSeen[^1]); // reactivated grain reloaded the ROTATED token from storage.
        Assert.All(authHeadersSeen, h => Assert.Equal("Bearer at-persisted-rotated", h)); // never anything else, on either activation.
        // The reloaded document's AccessExpiry (now+3600s from the stub's own /token response) is
        // NOT near expiry, so the reactivated grain does not refresh again — exactly ONE refresh
        // POST across the whole persist-then-reload scenario.
        Assert.Equal(1, stub.TokenCallCount);
    }

    /// <summary>
    /// Task 5 hardening: the ACTUAL oauth `authRequiredButMissing` guard
    /// (`BuildResponseAsync`'s `McpServerAuthModes.IsOAuth(_server.AuthMode) &amp;&amp; _oauthToken is
    /// null` branch) — distinct from `OAuthServerWithNoStoredToken_FailsClosed_NeverDialsUpstream`
    /// above, which only ever exercises the earlier `Status != Published` gate (that server never
    /// leaves `NeedsReauth`). This test drives the server all the way to `Status = Published` via
    /// the REAL `MarkOAuthConnectedAsync` transition (which requires seeding a token first — the
    /// grain call itself has no token precondition, but seeding one first mirrors the real
    /// consent-then-connect lifecycle), then clears the stored token out from under it
    /// (`ClearMcpServerOAuthTokenAsync`) and forces a reactivation (`EvictAsync`) so the next
    /// dispatch loads `Status = Published` with NO decryptable token — the actual gap this guard
    /// exists to fail closed on (e.g. an out-of-band token loss / encrypt-key rotation gap), not the
    /// simpler "never connected at all" case the existing test covers.
    /// </summary>
    [Fact]
    public async Task OAuthServerPublishedButTokenCleared_FailsClosed_NeverDialsUpstream()
    {
        var mcpDialed = false;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (ctx.Request.Path == "/mcp") mcpDialed = true;
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""");
        });

        var seeded = await fixture.SeedUserAsync($"oauth-published-no-token-{Guid.NewGuid():N}@example.com", "Published No Token");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-published-no-token-{Guid.NewGuid():N}", $"{stub.Url}/mcp", McpServerAuthModes.Oauth, null, null);

        var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));
        using (var scope = kekFactory.Services.CreateScope())
        {
            var envelopeCrypto = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
            var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();

            // Seed a real token first — MarkOAuthConnectedAsync itself has no token precondition,
            // but this mirrors the real lifecycle (consent completes, a token is stored, THEN the
            // server flips Published) rather than skipping straight to an artificial state.
            var doc = new McpOAuthTokenDocument("at-temp", "rt-temp", DateTimeOffset.UtcNow.AddHours(1),
                $"{stub.Url}/token", "https://as.example.test", "client-1", null);
            var ciphertext = await envelopeCrypto.EncryptAsync(server.SpaceId, McpServerSecretCrypto.OAuthAad(server.Id), McpOAuthTokenDocument.Serialize(doc), default);
            await repository.SetMcpServerOAuthTokenAsync(server.Id, ciphertext, default);
            await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).MarkOAuthConnectedAsync();

            // Now remove the stored token WITHOUT flipping Status back — simulates an out-of-band
            // token loss (bad migration / manual DB fix / encrypt-key rotation gap) that leaves
            // Status=Published with no decryptable token: the actual scenario the
            // authRequiredButMissing guard exists for.
            await repository.ClearMcpServerOAuthTokenAsync(server.Id, default);
        }

        // MarkOAuthConnectedAsync already evicted once above, but that happened BEFORE the token
        // was cleared — evict again so the NEXT activation reloads with Status=Published and no token.
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);
        await proxy.EvictAsync();

        var confirmState = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        Assert.Equal(McpServerStatus.Published, confirmState.Status); // sanity: really the Published-but-no-token gap, not the Status!=Published gate.

        var (sessionId, connectionId, writer) = await OpenConsumerConnectionAsync(server);
        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("error", out var error));
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.Equal("Server configuration error.", error.GetProperty("message").GetString());
        Assert.False(mcpDialed); // authRequiredButMissing guard fires BEFORE ever dialing upstream.
    }

    /// <summary>
    /// Task 5 hardening: property 7 (post-refresh-401 does not loop) was untested. The stub 401s
    /// UNCONDITIONALLY on every /mcp call — even the freshly-refreshed token still fails — so
    /// `SendWithOAuthRetryAsync`'s "retry ONCE, then propagate" shape is exercised for real: the
    /// first attempt 401s, the single-flight refresh runs exactly once, the ONE retry 401s again,
    /// and that second failure is NOT retried again (no loop) — it propagates to
    /// `BuildResponseAsync`'s outer `catch (HttpMcpUnauthorizedException)`, which returns the same
    /// generic "Upstream MCP server error." shape as any other upstream failure (never the raw
    /// 401/body).
    /// </summary>
    [Fact]
    public async Task PersistentUpstream401_AfterRefreshRetry_ReturnsGenericError_NoInfiniteLoop()
    {
        var mcpHits = 0;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            Interlocked.Increment(ref mcpHits);
            return (401, null); // unconditional — even the refreshed token still fails upstream.
        }, refreshedAccessToken: "at-still-bad");

        var (server, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-expired", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddHours(1), // NOT near expiry — only the reactive 401 path fires
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        await proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            connectionId, sessionId, default);
        var pushed = await writer.ReadNextAsync(PushTimeout);
        var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());

        Assert.True(responseJson.TryGetProperty("error", out var error));
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.Equal("Upstream MCP server error.", error.GetProperty("message").GetString()); // generic — never the raw 401/body
        Assert.Equal(2, mcpHits); // original attempt + exactly ONE retry — never a loop.
        Assert.Equal(1, stub.TokenCallCount); // exactly one refresh POST — the retry-once shape does not re-refresh on the second 401.
    }

    /// <summary>
    /// Reality-over-plan deviation #1 (see class doc comment): N SEPARATE consumer sessions
    /// against the SAME server/grain activation — NOT N frames on one session — so N real,
    /// independent per-consumer worker threads race to call
    /// `RefreshOAuthTokenSingleFlightAsync` concurrently for real (the scenario its own doc
    /// comment describes). Asserts the single-flight gate directly via a real request counter on
    /// the stub's /token route, not indirectly.
    /// </summary>
    [Fact]
    public async Task ConcurrentDispatches_NearExpiry_TriggerExactlyOneRefreshCall()
    {
        using var stub = await StartStubAsync(
            (ctx, method, id) => (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""),
            refreshedAccessToken: "at-refreshed-once");

        const int concurrentSessions = 5;
        var sessions = new List<(SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)>();
        McpServer? server = null;
        for (var i = 0; i < concurrentSessions; i++)
        {
            if (server is null)
            {
                var (createdServer, sessionId, connectionId, writer) = await SetUpOAuthConsumerSessionAsync(
                    stub.Url, accessToken: "at-old", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddSeconds(10),
                    tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
                server = createdServer;
                sessions.Add((sessionId, connectionId, writer));
            }
            else
            {
                sessions.Add(await OpenConsumerConnectionAsync(server));
            }
        }

        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server!.Id.Value);
        var dispatches = sessions.Select((s, i) => proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes($$$$"""{"jsonrpc":"2.0","id":{{{{i}}}},"method":"tools/call","params":{}}"""),
            s.ConnectionId, s.SessionId, default));
        await Task.WhenAll(dispatches);

        foreach (var (_, _, writer) in sessions)
        {
            var pushed = await writer.ReadNextAsync(PushTimeout);
            var responseJson = JsonSerializer.Deserialize<JsonElement>(pushed.Frame.Ciphertext.ToByteArray());
            Assert.True(responseJson.TryGetProperty("result", out _)); // none observed an unrefreshed-token failure
        }

        Assert.Equal(1, stub.TokenCallCount); // single-flight: N concurrent near-expiry dispatches, exactly ONE refresh POST.
    }

    /// <summary>
    /// Reality-over-plan deviation #1 (see class doc comment + the plan's own Step 8 inline
    /// correction): N SEPARATE consumer sessions, each given a REAL prior handshake
    /// ("initialize" + "notifications/initialized", ungated in the stub below) before the gated
    /// "tools/call" is dispatched. Without the prior handshake, each session's first-ever frame
    /// would trigger the own-initialize-fallback (Finding 16, B2) — a SECOND ungated-by-design
    /// upstream call per frame that would need its own gate release, breaking the 1:1
    /// correspondence between dispatched frames and `gate.Release(totalCalls)`'s count.
    /// </summary>
    [Fact]
    public async Task EgressCap_ExcessConcurrentConsumerCalls_AreBoundedNotUnbounded()
    {
        var gate = new SemaphoreSlim(0);
        var gateLock = new object();
        var inFlight = 0;
        var maxObservedInFlight = 0;
        using var stub = await StartStubAsync((ctx, method, id) =>
        {
            if (method != "tools/call")
                return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}"""); // handshake traffic — ungated.

            var current = Interlocked.Increment(ref inFlight);
            lock (gateLock) { maxObservedInFlight = Math.Max(maxObservedInFlight, current); }
            gate.Wait(TimeSpan.FromSeconds(5)); // hold every gated upstream call open until released below
            Interlocked.Decrement(ref inFlight);
            return (200, $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""");
        });

        const int totalCalls = EgressConcurrencyCeiling + 4; // deliberately over the cap
        var (server, firstSessionId, firstConnectionId, firstWriter) = await SetUpOAuthConsumerSessionAsync(
            stub.Url, accessToken: "at-1", refreshToken: "rt-1", accessExpiry: DateTimeOffset.UtcNow.AddHours(1),
            tokenEndpoint: $"{stub.Url}/token", clientId: "client-1", clientSecret: null);
        var proxy = fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(server.Id.Value);

        var sessions = new List<(SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)>
        {
            (firstSessionId, firstConnectionId, firstWriter),
        };
        for (var i = 1; i < totalCalls; i++)
            sessions.Add(await OpenConsumerConnectionAsync(server));

        // Real handshake per session FIRST (ungated above) — establishes Initialized +
        // PastInitializedBarrier so the gated dispatch below makes exactly ONE upstream call.
        foreach (var (sessionId, connectionId, writer) in sessions)
        {
            await proxy.DispatchFrameAsync(
                Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}"""),
                connectionId, sessionId, default);
            await writer.ReadNextAsync(PushTimeout);
            await proxy.DispatchFrameAsync(
                Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""),
                connectionId, sessionId, default);
        }

        var dispatches = sessions.Select((s, i) => proxy.DispatchFrameAsync(
            Encoding.UTF8.GetBytes($$$$"""{"jsonrpc":"2.0","id":{{{{i + 1}}}},"method":"tools/call","params":{}}"""),
            s.ConnectionId, s.SessionId, default)).ToList();
        await Task.Delay(500); // let the first wave actually reach the (blocked) stub
        gate.Release(totalCalls); // release everything so the test can finish
        await Task.WhenAll(dispatches);
        foreach (var (_, _, writer) in sessions)
            await writer.ReadNextAsync(PushTimeout);

        Assert.True(maxObservedInFlight <= EgressConcurrencyCeiling,
            $"expected at most {EgressConcurrencyCeiling} concurrent upstream calls, observed {maxObservedInFlight}");
    }

    // ── stub server (mirrors HttpMcpProxyGrainTests.StartStubMcpServerAsync, extended with a
    // /token route — see Grounding Note 9) ──

    private enum TokenEndpointBehavior { Success, InvalidGrant, Transient5xx }

    private async Task<StubServer> StartStubAsync(
        Func<HttpContext, string?, string, (int StatusCode, string? Body)> mcpResponder,
        string refreshedAccessToken = "at-refreshed",
        TokenEndpointBehavior tokenEndpointBehavior = TokenEndpointBehavior.Success)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var app = builder.Build();
        var tokenCallCount = 0;

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            var (statusCode, body) = mcpResponder(ctx, method, id);
            ctx.Response.StatusCode = statusCode;
            if (body is not null)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(body);
            }
        });
        app.MapPost("/token", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref tokenCallCount);
            if (tokenEndpointBehavior == TokenEndpointBehavior.InvalidGrant)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"invalid_grant"}""");
                return;
            }
            if (tokenEndpointBehavior == TokenEndpointBehavior.Transient5xx)
            {
                ctx.Response.StatusCode = 503;
                return;
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"access_token":"{{refreshedAccessToken}}","refresh_token":"rt-rotated","expires_in":3600}""");
        });

        await app.StartAsync();
        // Blocker 1 (fable plan-review): register this stub's real loopback address with the
        // shared façade harness (see the "Shared Test Harness" section before Task 4) and expose
        // the façade https:// URL from .Url — SetUpOAuthConsumerSessionAsync below stores THIS
        // value as both RemoteUrl and tokenEndpoint, so SsrfGuard.ValidateUrl (unconditionally
        // HTTPS-only) genuinely approves the token-exchange/refresh calls this grain makes.
        var facadeHost = OAuthFacadeHostRegistry.Register(new Uri(app.Urls.First()));
        return new StubServer(app, $"https://{facadeHost}", facadeHost, () => Volatile.Read(ref tokenCallCount));
    }

    private sealed class StubServer(WebApplication app, string url, string facadeHost, Func<int> tokenCallCount) : IDisposable
    {
        public string Url => url;
        public int TokenCallCount => tokenCallCount();
        public void Dispose()
        {
            OAuthFacadeHostRegistry.Unregister(facadeHost);
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private async Task<(McpServer Server, SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)> SetUpOAuthConsumerSessionAsync(
        string mcpUrl, string accessToken, string? refreshToken, DateTimeOffset accessExpiry,
        string tokenEndpoint, string clientId, string? clientSecret)
    {
        var seeded = await fixture.SeedUserAsync($"oauth-proxy-{Guid.NewGuid():N}@example.com", "OAuth Proxy Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-oauth-proxy-{Guid.NewGuid():N}", $"{mcpUrl}/mcp", McpServerAuthModes.Oauth, null, null);

        // KEK-aware factory (mirrors HttpMcpProxyGrainTests.CreateServerAsync's established
        // pattern — the shared fixture.Factory has no KEK configured).
        var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));
        using var scope = kekFactory.Services.CreateScope();
        var envelopeCrypto = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
        var repository = scope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();

        var doc = new McpOAuthTokenDocument(accessToken, refreshToken, accessExpiry, tokenEndpoint, "https://as.example.test", clientId, clientSecret);
        var ciphertext = await envelopeCrypto.EncryptAsync(server.SpaceId, McpServerSecretCrypto.OAuthAad(server.Id), McpOAuthTokenDocument.Serialize(doc), default);
        await repository.SetMcpServerOAuthTokenAsync(server.Id, ciphertext, default);
        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).MarkOAuthConnectedAsync();

        var (sessionId, connectionId, writer) = await OpenConsumerConnectionAsync(server);
        return (server, sessionId, connectionId, writer);
    }

    private async Task<(SessionId SessionId, ConnectionId ConnectionId, FakeAgentStreamWriter Writer)> OpenConsumerConnectionAsync(McpServer server)
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

    /// <summary>Captures GatewayToNodeMessage writes — mirrors HttpMcpProxyGrainTests's own copy.</summary>
    private sealed class FakeAgentStreamWriter : IAsyncStreamWriter<GatewayToNodeMessage>
    {
        private readonly Channel<GatewayToNodeMessage> _channel = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(GatewayToNodeMessage message) { _channel.Writer.TryWrite(message); return Task.CompletedTask; }
        public async Task<GatewayToNodeMessage> ReadNextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _channel.Reader.ReadAsync(cts.Token);
        }
    }
}
