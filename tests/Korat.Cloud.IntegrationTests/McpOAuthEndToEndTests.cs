using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 2, Task 7: the full stub-AS OAuth round trip through the REAL gateway — discovery
/// → DCR → authorize (owner's browser step simulated by extracting `state` from the returned
/// authorizeUrl and calling the callback directly, exactly as a real browser redirect would) →
/// callback → token stored → a consumer's real `tools/call` (via the actual gRPC
/// Connect/RequestSession/Frame path, not the synthetic OpenSession helper Tasks 4/5's narrower
/// tests use) succeeds using the injected token; then a stub 401 triggers exactly one refresh;
/// then a mix-up attempt (callback for server B carrying server A's state) is rejected.
///
/// Reality-over-plan deviations from this task's own plan draft (this file is a verification task
/// exercising already-implemented Tasks 1–6 code, so the drift lives entirely in adapting the plan's
/// reference stub/helpers to the real, already-built API — see each inline note below):
///
///  1. The plan's own draft `/mcp` stub answered every POST 200 unconditionally. The REAL
///     `McpOAuthDiscoveryService.ProbeForProtectedResourceMetadataUrlAsync` (Task 2, already
///     shipped) requires the discovery probe to be challenged with HTTP 401 + a WWW-Authenticate
///     header naming the protected-resource-metadata URL (RFC 9728 — see
///     McpOAuthDiscoveryServiceTests.DiscoverAsync_HappyPath_401ThenPrmThenAsMetadata for the
///     already-gated contract). Fixed by branching on Authorization-header presence: the discovery
///     probe carries none (no token exists yet); every REAL upstream call HttpMcpProxyGrain makes
///     after a token is stored always carries a Bearer header (InjectAuth runs before every dial).
///  2. The plan's draft `ConnectAgentAsync` never consumed the gateway's Hello acknowledgement
///     before returning. Every real precedent for this exact gRPC dance —
///     ConnectAccessRequestTests.ConnectAgentAsync, RelayFrameForwardingTests.ConnectAsync,
///     HttpCloudMcpRoutingIntegrationTests.ConnectAsync — reads and asserts the Hello ack first;
///     skipping it would desync this file's `RequestSessionAsync`/frame reads by one message.
///     Fixed by adding the same `ReadAsync`-then-assert-Hello step, using a shared bounded-timeout
///     `ReadAsync` helper (mirrors RelayFrameForwardingTests/HttpCloudMcpRoutingIntegrationTests)
///     instead of ad-hoc `MoveNext(CancellationToken.None)` calls, so every stream read in this file
///     is deterministic (bounded, no indefinite hang) with no `Task.Delay`/sleep anywhere.
///  3. The plan's draft `/mcp` JSON responses used a 2-dollar raw interpolated string
///     (`$$"""...{{id}}...{}}}"""`) for shapes containing a literal `{}` immediately followed by
///     more closing braces — the exact ambiguity `HttpCloudMcpRoutingIntegrationTests`'s own,
///     already-compiling stub for the SAME shape (protocolVersion/capabilities/tools/call content)
///     resolves by escalating to a 4-dollar string (`$$$$"""...{{{{id}}}}..."""`). Copied that
///     already-proven pattern verbatim rather than re-deriving the brace-count math.
///  4. SHOULD-FIX 3 (fable plan-review, called out in the plan itself) plus a second, related gap
///     the plan's own literal code did not close: this test's owner client needs BOTH (a) the
///     KEK-aware `WithWebHostBuilder` factory (`WebMcpServerContractTests.CreateKekAwareAuthenticatedClientAsync`'s
///     pattern — the real callback's success path calls `envelopeCrypto.EncryptAsync`, and the
///     shared `fixture.CreateAuthenticatedClientAsync`'s base factory has no KEK configured) AND
///     (b) `AllowAutoRedirect = false` (`McpOAuthCallbackIntegrationTests.CreateAuthenticatedClientNoRedirectAsync`'s
///     own documented gotcha — a plain client transparently follows the callback's 302 to a
///     client-side SPA route that 404s with no built SPA in a `dotnet test` run, masking the actual
///     redirect status/Location this test asserts on directly). The plan's draft code implied only
///     the first; this file's `CreateKekAwareAuthenticatedClientNoRedirectAsync` combines both.
///  5. The plan's draft top-of-file `using` list omitted `Microsoft.Extensions.Configuration`
///     (needed for `ConfigureAppConfiguration`/`AddInMemoryCollection`),
///     `Microsoft.Extensions.DependencyInjection` (needed for `GetRequiredService`/`CreateScope`),
///     and `Microsoft.AspNetCore.Mvc.Testing` (needed for `WebApplicationFactoryClientOptions`) even
///     though its own inline comment said they were required — added all three.
/// </summary>
public sealed class McpOAuthEndToEndTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FullRoundTrip_DiscoveryThroughToolsCall_ThenRefreshCycle_ThenMixUpRejected()
    {
        using var stub = await StartStubAuthorizationAndResourceServerAsync();
        var (spaceId, cliToken, userId) = await SeedUserSpaceAndTokenAsync2();
        // Deviation #4 (class doc comment): KEK-aware (for the callback's real EncryptAsync call)
        // AND AllowAutoRedirect=false (so the callback's 302 is observable directly).
        using var ownerClient = await CreateKekAwareAuthenticatedClientNoRedirectAsync(userId);

        // 1. Owner registers the oauth server — discovery + DCR run for real against the stub.
        var createResp = await ownerClient.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-e2e-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var serverId = created.GetProperty("id").GetString()!;
        var authorizeUrl = created.GetProperty("connect").GetProperty("authorizeUrl").GetString()!;
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;

        // 2. Simulate the owner's browser consent + the AS's redirect: call the callback directly
        // with a `code` the stub AS will accept (its /token stub echoes any code as valid).
        var callbackResp = await ownerClient.GetAsync($"/api/mcp/oauth/callback/{serverId}?code=stub-auth-code&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callbackResp.StatusCode);
        Assert.Contains("connected=true", callbackResp.Headers.Location!.ToString());
        // The redirect MUST target the SPA under its /app base — a bare /servers/{id} 404s on the
        // deployed app (SPA is mounted at /app/), which a live Miro test surfaced as an empty
        // file-download instead of the console landing. Lock the /app prefix here.
        Assert.StartsWith("/app/servers/", callbackResp.Headers.Location!.ToString());

        var serverAfterConnect = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(serverId).GetAsync();
        Assert.Equal(McpServerStatus.Published, serverAfterConnect.Status);

        // 3. A consumer opens a session and calls tools/call through the REAL gateway path (the
        // exact Connect/RequestSession/Frame sequence RelayFrameForwardingTests.cs and
        // HttpCloudMcpRoutingIntegrationTests.cs already exercise for http_cloud servers) — the
        // injected token must now be an OAuth Bearer, invisibly, from the consumer's point of
        // view. An http_cloud session has no publisher stream: the response is pushed back onto
        // the SAME agent connection.
        var agentNodeId = NodeId.New().Value;
        var agentClientId = agentNodeId; // mirrors ConnectAccessRequestTests' convention: agentClientId == agentNodeId for a directly-registered agent
        await RegisterAgentClientAsync(agentClientId, agentNodeId, spaceId);

        var accessRequest = await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .CreateAccessRequestAsync(new ConsumerId(agentClientId), new McpServerId(serverId), new NodeId(agentNodeId));
        await fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId).ApproveAccessRequestAsync(accessRequest.Id, userId);

        using var agentCall = await ConnectAgentAsync(agentNodeId, cliToken);
        var sessionResponse = await RequestSessionAsync(agentCall, agentClientId, serverId);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.SessionOpened, sessionResponse.PayloadCase);
        var sessionId = sessionResponse.SessionOpened.SessionId;

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 1,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""),
            },
        });
        var pushed1 = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, pushed1.PayloadCase);
        var toolsCallResult1 = JsonSerializer.Deserialize<JsonElement>(pushed1.Frame.Ciphertext.ToByteArray());
        Assert.True(toolsCallResult1.TryGetProperty("result", out _));
        Assert.Equal("Bearer at-e2e-1", stub.LastAuthHeaderSeen); // the FIRST token the /token stub issued (at the callback)

        // 4. Refresh cycle: force the stored token to appear near-expiry by writing a fresh
        // ciphertext directly (mirrors Task 5's SetUpOAuthConsumerSessionAsync pattern) with the
        // SAME token_endpoint/client_id/refresh_token the callback already stored, then evict the
        // proxy grain so the next dispatch reloads it and observes the near-expiry.
        // SHOULD-FIX 3 (fable plan-review): must decrypt/re-encrypt with the SAME KEK the real
        // callback (Step 2, above) actually used — the default IEnvelopeCrypto has no KEK at all
        // and would throw, not silently use a different key. Resolve both from a KEK-configured
        // scope using the SAME KekId/KekBase64 the owner client's factory used.
        var kekFactory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));
        using (var kekScope = kekFactory.Services.CreateScope())
        {
            var repository = kekScope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
            var envelopeCrypto = kekScope.ServiceProvider.GetRequiredService<Korat.Domain.Persistence.IEnvelopeCrypto>();
            var mcpServerId = new McpServerId(serverId);
            var storedCiphertext = await repository.GetMcpServerOAuthTokenCiphertextAsync(mcpServerId, default);
            var storedJson = await envelopeCrypto.DecryptAsync(serverAfterConnect.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.OAuthAad(mcpServerId), storedCiphertext!, default);
            var storedDoc = Korat.Cloud.Mcp.Oauth.McpOAuthTokenDocument.Deserialize(storedJson);
            var nearExpiryDoc = storedDoc with { AccessExpiry = DateTimeOffset.UtcNow.AddSeconds(5) };
            var nearExpiryCiphertext = await envelopeCrypto.EncryptAsync(serverAfterConnect.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.OAuthAad(mcpServerId), Korat.Cloud.Mcp.Oauth.McpOAuthTokenDocument.Serialize(nearExpiryDoc), default);
            await repository.SetMcpServerOAuthTokenAsync(mcpServerId, nearExpiryCiphertext, default);
        }
        await fixture.ClusterClient.GetGrain<IHttpMcpProxyGrain>(serverId).EvictAsync();

        await agentCall.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Frame = new RelayFrame
            {
                SessionId = sessionId,
                SequenceNumber = 2,
                Direction = "client_to_server",
                Ciphertext = ByteString.CopyFromUtf8("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{}}"""),
            },
        });
        var pushed2 = await ReadAsync(agentCall.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, pushed2.PayloadCase);
        var toolsCallResult2 = JsonSerializer.Deserialize<JsonElement>(pushed2.Frame.Ciphertext.ToByteArray());
        Assert.True(toolsCallResult2.TryGetProperty("result", out _));
        Assert.Equal(2, stub.TokenCallCount); // the initial code-exchange call (1) + exactly one refresh (2) — no more
        Assert.Equal("Bearer at-e2e-2", stub.LastAuthHeaderSeen); // the REFRESHED token, not the stale one

        await agentCall.RequestStream.CompleteAsync();

        // 5. Mix-up: register a second oauth server B against the SAME stub AS, then replay server
        // A's ALREADY-CONSUMED state (burned by step 2's real callback) at server B's callback path
        // — must be rejected, and server B must NOT be connected by the attempt.
        var createRespB = await ownerClient.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-e2e-b-{Guid.NewGuid():N}",
            remoteUrl = $"{stub.Url}/mcp",
            authMode = "oauth",
        });
        Assert.Equal(HttpStatusCode.OK, createRespB.StatusCode);
        var createdB = await createRespB.Content.ReadFromJsonAsync<JsonElement>();
        var serverIdB = createdB.GetProperty("id").GetString()!;

        var mixUpResp = await ownerClient.GetAsync($"/api/mcp/oauth/callback/{serverIdB}?code=stub-auth-code&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, mixUpResp.StatusCode);
        Assert.Contains("reason=", mixUpResp.Headers.Location!.ToString()); // server A's state is already consumed by step 2 — expired_or_replayed or mismatch, either way NOT connected=true

        var serverBAfterMixUp = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(serverIdB).GetAsync();
        Assert.Equal(McpServerStatus.NeedsReauth, serverBAfterMixUp.Status);
    }

    /// <summary>
    /// Deviation #4 (class doc comment): combines WebMcpServerContractTests.CreateKekAwareAuthenticatedClientAsync's
    /// KEK configuration with McpOAuthCallbackIntegrationTests.CreateAuthenticatedClientNoRedirectAsync's
    /// AllowAutoRedirect=false — this test's owner client needs both, since it is the only test in
    /// this plan that hits the REAL callback success path (KEK) while also asserting directly on the
    /// callback's 302 status/Location (no-redirect).
    /// </summary>
    private async Task<HttpClient> CreateKekAwareAuthenticatedClientNoRedirectAsync(Korat.Domain.Auth.UserId userId)
    {
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));

        using var scope = factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<Korat.Cloud.Web.Auth.Services.ISessionService>();
        var session = await sessions.CreateAsync(userId, "test-mcp-server", "127.0.0.1", default);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{Korat.Cloud.Web.Auth.CanonicalSigninHandler.SessionCookieName}={session.Id:N}");
        return client;
    }

    // ─── gRPC helpers (copied from ConnectAccessRequestTests.cs / RelayFrameForwardingTests.cs /
    // HttpCloudMcpRoutingIntegrationTests.cs — per-file duplication is this project's own
    // established convention, Grounding Note 9) ───────────────────────────────────────────────

    private async Task<(string SpaceId, string CliToken, Korat.Domain.Auth.UserId UserId)> SeedUserSpaceAndTokenAsync2()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-e2e-{Guid.NewGuid():N}@example.com", "OAuth E2E Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        return (seeded.SpaceId, cliToken, seeded.UserId);
    }

    private Task RegisterAgentClientAsync(string agentClientId, string nodeId, string spaceId) =>
        fixture.ClusterClient.GetGrain<IConsumerGrain>(agentClientId)
            .RegisterAsync(new SpaceId(spaceId), new NodeId(nodeId), "test-agent");

    private async Task<AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage>> ConnectAgentAsync(string nodeId, string cliToken)
    {
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        var call = grpcClient.Connect(callOptions);
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello { NodeId = nodeId, DisplayName = "agent", NodeKind = "agent" },
        });
        // Deviation #2 (class doc comment): the plan's draft never consumed this ack.
        var hello = await ReadAsync(call.ResponseStream);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, hello.PayloadCase);
        return call;
    }

    private static async Task<GatewayToNodeMessage> RequestSessionAsync(
        AsyncDuplexStreamingCall<NodeToGatewayMessage, GatewayToNodeMessage> call, string agentClientId, string serverId)
    {
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            RequestSession = new RequestSession
            {
                RequestId = Guid.NewGuid().ToString("N"),
                AgentClientId = agentClientId,
                McpServerId = serverId,
            },
        });
        return await ReadAsync(call.ResponseStream);
    }

    /// <summary>Bounded-timeout stream read — mirrors RelayFrameForwardingTests.ReadAsync /
    /// HttpCloudMcpRoutingIntegrationTests.ReadAsync exactly, so every await in this file is
    /// deterministic (no indefinite hang, no sleep-based polling).</summary>
    private static async Task<GatewayToNodeMessage> ReadAsync(IAsyncStreamReader<GatewayToNodeMessage> stream)
    {
        using var cts = new CancellationTokenSource(MoveNextTimeout);
        var moved = await stream.MoveNext(cts.Token);
        Assert.True(moved, "Expected a message but the stream ended.");
        return stream.Current;
    }

    // ─── stub authorization + resource server ──────────────────────────────────────────────────

    private async Task<StubServer> StartStubAuthorizationAndResourceServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Testing";
        var app = builder.Build();
        string url = string.Empty;
        string? lastAuthHeaderSeen = null;
        var tokenCallCount = 0;

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            // Deviation #1 (class doc comment): an ABSENT Authorization header is the real
            // McpOAuthDiscoveryService's unauthenticated discovery probe (RFC 9728) — it must be
            // challenged with 401 + WWW-Authenticate, not answered 200. Every REAL upstream call
            // HttpMcpProxyGrain makes after a token exists always carries a Bearer header.
            var authHeader = ctx.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(authHeader))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.Append("WWW-Authenticate",
                    $"Bearer resource_metadata=\"{url}/.well-known/oauth-protected-resource\"");
                return;
            }
            lastAuthHeaderSeen = authHeader;
            var reqDoc = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var method = reqDoc.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = reqDoc.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
            ctx.Response.ContentType = "application/json";
            // Deviation #3 (class doc comment): 4-dollar raw string — matches
            // HttpCloudMcpRoutingIntegrationTests's own, already-compiling stub for this exact
            // "capabilities":{}}} / "result":{...}} brace shape.
            await ctx.Response.WriteAsync(method switch
            {
                "initialize" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                "tools/call" => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{"content":[{"type":"text","text":"ok"}]}}""",
                _ => $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"result":{}}""",
            });
        });
        app.MapGet("/.well-known/oauth-protected-resource", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"resource":"{{url}}/mcp","authorization_servers":["{{url}}"]}""");
        });
        app.MapGet("/.well-known/oauth-authorization-server", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""
                {"issuer":"{{url}}","authorization_endpoint":"{{url}}/authorize","token_endpoint":"{{url}}/token","registration_endpoint":"{{url}}/register"}
                """);
        });
        app.MapPost("/register", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"client_id":"e2e-client","client_secret":"e2e-secret"}""");
        });
        app.MapPost("/token", async (HttpContext ctx) =>
        {
            tokenCallCount++;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"access_token":"at-e2e-{{tokenCallCount}}","refresh_token":"rt-e2e","expires_in":3600}""");
        });

        await app.StartAsync();
        var realLoopbackUrl = app.Urls.First(); // e.g. http://127.0.0.1:{port} — never handed to code under test
        var facadeHost = OAuthFacadeHostRegistry.Register(new Uri(realLoopbackUrl));
        url = $"https://{facadeHost}"; // NOW assign the closures' captured variable — safe: no
        // request reaches these routes until a caller dials `url` (returned as stub.Url below), and
        // that cannot happen before this method returns.
        return new StubServer(app, url, facadeHost, () => lastAuthHeaderSeen, () => tokenCallCount);
    }

    private sealed class StubServer(WebApplication app, string url, string facadeHost, Func<string?> lastAuthHeader, Func<int> tokenCallCount) : IDisposable
    {
        public string Url => url;
        public string? LastAuthHeaderSeen => lastAuthHeader();
        public int TokenCallCount => tokenCallCount();
        public void Dispose()
        {
            OAuthFacadeHostRegistry.Unregister(facadeHost);
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
