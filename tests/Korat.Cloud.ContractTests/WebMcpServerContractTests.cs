using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Cloud.IntegrationTests;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.GrainInterfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.ContractTests;

public sealed class WebMcpServerContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [SkippableFact]
    public async Task SpacePage_ReturnsHtmlWithNodesAndServersSections()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/space/");
        // Built SPA is absent in a plain `dotnet test` run (produced by the Docker build,
        // served in CI/prod). Skip rather than fail when it's not present.
        Skip.If(response.StatusCode == HttpStatusCode.NotFound,
            "Built SPA not present in wwwroot (run a Docker/SPA build to exercise this contract).");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("MCP Servers", html);
        Assert.Contains("Nodes", html);
        Assert.Contains("Disable", html);
    }

    [Fact]
    public async Task McpServerDetail_RequiresAuth()
    {
        // Anonymous request must be rejected before any data lookup.
        var response = await fixture.Factory.CreateClient().GetAsync("/api/mcp-servers/some-server-id");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpServerDetail_ReturnsNotFoundForUnknownServer()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var response = await client.GetAsync("/api/mcp-servers/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateHttpMcpServer_Succeeds_And_NeverReturnsPlaintextSecret()
    {
        // Reality-over-plan note: this test sends a `secret`, which the POST handler envelope-
        // encrypts via IEnvelopeCrypto.EncryptAsync on the WEB HOST. The shared base
        // fixture.Factory has no KEK configured (fail-closed by design — see IEnvelopeCrypto's
        // doc comment), so EncryptAsync throws 500 there. Needs a dedicated KEK-configured
        // factory, same idiom as TelegramWebhookTests.NewWebhookHarnessAsync /
        // EnvelopeEncryptionIntegrationTests.CreateEnvelopeFactory — the plan's literal
        // fixture.CreateAuthenticatedClientAsync call doesn't exercise the encrypt path.
        using var client = await CreateKekAwareAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var name = $"http-srv-{Guid.NewGuid():N}";

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = name,
            remoteUrl = "https://example.test/mcp",
            authMode = "bearer",
            secret = "super-secret-token-value"
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, body.GetProperty("displayName").GetString());
        Assert.Equal("http_cloud", body.GetProperty("transport").GetString());
        Assert.True(body.GetProperty("hasSecret").GetBoolean());
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token-value", raw);
    }

    [Fact]
    public async Task CreateHttpMcpServer_RejectsSsrfUnsafeUrl()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-ssrf-{Guid.NewGuid():N}",
            remoteUrl = "http://169.254.169.254/latest/meta-data/",
            authMode = "none"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // NOTE: the increment-1 `CreateHttpMcpServer_RejectsOAuthAuthMode_Increment1Scope` test was
    // DELETED here. Increment-2 Task 1 flipped `McpServerAuthModes.IsValid` to accept "oauth", which
    // reverses the 400-on-oauth behavior this test asserted — so it must be removed the moment the
    // behavior changes (Task 1), not deferred to Task 4 (the plan's original sequencing left the
    // ContractTests suite red across Tasks 1-3; pulled the deletion forward to keep every task green).
    // Its replacement (POST oauth → connect action) lands with the Task 4 callback endpoint.

    /// <summary>Security gate LOW: the existing SSRF regression test above uses
    /// `http://169.254.169.254/...`, which is rejected by SsrfGuard's https-only check BEFORE
    /// the private/metadata IP-range check ever runs — so it never actually exercises the
    /// IP-block path. This case uses `https://` so the scheme check passes and the literal-IP
    /// block (SsrfGuard.IsBlockedAddress) is what rejects it.</summary>
    [Fact]
    public async Task CreateHttpMcpServer_RejectsSsrfUnsafeUrl_MetadataIp_ViaHttps()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-ssrf-https-{Guid.NewGuid():N}",
            remoteUrl = "https://169.254.169.254/",
            authMode = "none"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>Security gate BLOCKER regression: authHeaderName is SSRF-untrusted input — Task
    /// 4's HttpMcpProxyGrain injects it via Headers.TryAddWithoutValidation, so an unvalidated
    /// "Host" would override the Host header on an SSRF-pinned connection. Write FIRST (TDD):
    /// must FAIL against pre-fix code (POST had no authHeaderName validation at all) and PASS
    /// once the shared OutboundInferenceValidation.ValidateHeaderName gate is wired in.</summary>
    [Fact]
    public async Task CreateHttpMcpServer_RejectsForbiddenAuthHeaderName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-forbidden-header-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "header",
            authHeaderName = "Host"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Security gate BLOCKER regression: a header name with a space is not a valid RFC
    /// 7230 token and must be rejected — same TDD note as above.</summary>
    [Fact]
    public async Task CreateHttpMcpServer_RejectsInvalidRfc7230AuthHeaderName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);

        var resp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-badtoken-header-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "header",
            authHeaderName = "Bad Header"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PatchHttpMcpServer_UpdatesUrlWithoutRequiringSecret()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-{Guid.NewGuid():N}",
            remoteUrl = "https://old.test/mcp",
            authMode = "none"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { remoteUrl = "https://new.test/mcp" });

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://new.test/mcp", patched.GetProperty("remoteUrl").GetString());
    }

    /// <summary>Security gate LOW: PATCH-RemoteUrl-SSRF regression — mirrors POST's
    /// CreateHttpMcpServer_RejectsSsrfUnsafeUrl but exercises the PATCH handler's own
    /// SsrfGuard.ValidateUrl call (already present pre-fix; this pins down the behavior rather
    /// than testing the new authHeaderName gate).</summary>
    [Fact]
    public async Task PatchHttpMcpServer_RejectsSsrfUnsafeUrl()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-ssrf-{Guid.NewGuid():N}",
            remoteUrl = "https://old.test/mcp",
            authMode = "none"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/mcp-servers/{id}", new { remoteUrl = "https://169.254.169.254/" });

        Assert.Equal(HttpStatusCode.BadRequest, patchResp.StatusCode);
    }

    /// <summary>Security gate BLOCKER regression on PATCH: same forbidden-header rejection as
    /// POST — write FIRST (TDD), must FAIL against pre-fix code and PASS once the shared
    /// validator is wired into the PATCH handler.</summary>
    [Fact]
    public async Task PatchHttpMcpServer_RejectsForbiddenAuthHeaderName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-forbidden-header-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/mcp-servers/{id}", new { authMode = "header", authHeaderName = "Host" });

        Assert.Equal(HttpStatusCode.BadRequest, patchResp.StatusCode);
        var body = await patchResp.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Security gate BLOCKER regression on PATCH: same bad-RFC-7230-token rejection as
    /// POST.</summary>
    [Fact]
    public async Task PatchHttpMcpServer_RejectsInvalidRfc7230AuthHeaderName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-badtoken-header-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/mcp-servers/{id}", new { authMode = "header", authHeaderName = "Bad Header" });

        Assert.Equal(HttpStatusCode.BadRequest, patchResp.StatusCode);
    }

    /// <summary>Security gate MEDIUM regression: PATCH must enforce "header mode requires a
    /// header name" on the EFFECTIVE post-patch state, same as POST does at creation. A server
    /// created with authMode="none" is patched to authMode="header" with no authHeaderName
    /// supplied (and none already stored) — must be rejected, not silently stranded in a broken
    /// auth state.</summary>
    [Fact]
    public async Task PatchHttpMcpServer_SwitchingToHeaderModeWithoutHeaderName_Returns400()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-patch-headermode-noheadername-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { authMode = "header" });

        Assert.Equal(HttpStatusCode.BadRequest, patchResp.StatusCode);
    }

    /// <summary>Finding 16, M4: PATCH secret:"" must actually clear hasSecret, not just clear the
    /// ciphertext while leaving the stale hint (and therefore hasSecret:true) behind.</summary>
    [Fact]
    public async Task PatchHttpMcpServer_ClearingSecret_ActuallyClearsHasSecret()
    {
        // Same reality-over-plan reason as CreateHttpMcpServer_Succeeds_And_NeverReturnsPlaintextSecret:
        // the initial create sends a secret, requiring a KEK-configured factory.
        using var client = await CreateKekAwareAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-clearsecret-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "bearer",
            secret = "leaked-token-value"
        });
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("hasSecret").GetBoolean());
        var id = created.GetProperty("id").GetString();

        var patchResp = await client.PatchAsJsonAsync($"/api/mcp-servers/{id}", new { secret = "" });

        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var patched = await patchResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(patched.GetProperty("hasSecret").GetBoolean());
        Assert.True(patched.GetProperty("secretHint").ValueKind is JsonValueKind.Null);
    }

    /// <summary>Task 6 (Crux Finding 8): GET /api/space's mcpServers[] projection must carry
    /// http_cloud's own fields (transport/remoteUrl) and must NOT synthesize a bogus empty
    /// publisherNodeName from the always-"" PublisherNodeId an http_cloud row stores.</summary>
    [Fact]
    public async Task SpacePage_ListsHttpCloudServer_WithoutBogusPublisherName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-catalog-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none"
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var spaceResp = await client.GetAsync("/api/space");
        spaceResp.EnsureSuccessStatusCode();
        var space = await spaceResp.Content.ReadFromJsonAsync<JsonElement>();
        // Reality-over-plan: mcpServers[].id serializes as the McpServerId struct ({value:...}),
        // not a bare string — the plan's literal `s.GetProperty("id").GetString()` throws
        // (established convention, documented at apps/Korat.Cli/Commands/SpaceDtos.cs:56 —
        // "the API serializes id types as {value:…} objects"). The POST response's `id` (used
        // above) IS a bare string (`id = server.Id.Value`, Endpoints.cs) — only this array
        // element's shape differs.
        var server = space.GetProperty("mcpServers").EnumerateArray()
            .First(s => s.GetProperty("id").GetProperty("value").GetString() == id);

        Assert.Equal("http_cloud", server.GetProperty("transport").GetString());
        Assert.Equal("https://example.test/mcp", server.GetProperty("remoteUrl").GetString());
        Assert.False(server.TryGetProperty("publisherNodeName", out var pubName) && pubName.GetString() == "");
    }

    /// <summary>Task 6 / Finding 16, S3: the SAME blank-publisher-name artifact Crux 8 found in
    /// mcpServers also exists in pendingAccessRequests on this same endpoint — an http_cloud
    /// server's PublisherNodeId is always "" by design (not a corrupt/unresolved node), so a
    /// pending access request against it must not surface an empty publisherNodeName either.
    /// Seeds the pending request via the grain-direct ISpaceGrain.CreateAccessRequestAsync call
    /// (the established idiom in this repo — see
    /// Korat.Cloud.IntegrationTests/AccessRequestDisplayNamesTests.cs — rather than driving a
    /// full unapproved-session HTTP flow just to produce one pending row).</summary>
    [Fact]
    public async Task PendingAccessRequests_ForHttpCloudServer_HasNoBogusPublisherName()
    {
        using var client = await fixture.CreateAuthenticatedClientAsync(KoratIntegrationFixture.DevSpaceOwnerUserId);
        var createResp = await client.PostAsJsonAsync("/api/mcp-servers", new
        {
            displayName = $"http-srv-accreq-{Guid.NewGuid():N}",
            remoteUrl = "https://example.test/mcp",
            authMode = "none"
        });
        createResp.EnsureSuccessStatusCode();
        var serverId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Grant-less agent-client requesting this server → pending access request.
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var request = await spaceGrain.CreateAccessRequestAsync(
            ConsumerId.New(), new McpServerId(serverId!), NodeId.New());

        var spaceResp = await client.GetAsync("/api/space");
        var space = await spaceResp.Content.ReadFromJsonAsync<JsonElement>();
        var pending = space.GetProperty("pendingAccessRequests").EnumerateArray()
            .First(r => r.GetProperty("id").GetProperty("value").GetString() == request.Id.Value);

        Assert.False(pending.TryGetProperty("publisherNodeName", out var pubName) && pubName.GetString() == "");
    }

    /// <summary>Task-6-gate MEDIUM regression: the http_cloud fix to GET /api/space's mcpServers
    /// projection must not change the wire SHAPE of publisherNodeId for existing stdio_node rows.
    /// Pre-fix, `publisherNodeId = isHttpCloud ? null : (string?)s.PublisherNodeId.Value` emitted a
    /// bare string for stdio rows — a regression from the pre-Increment-1 shape, which serialized
    /// the strongly-typed NodeId struct directly (→ <c>{value:"…"}</c>), same as every other
    /// Id-struct field on this DTO (id, mcpServerId, etc.). Byte-for-byte contract preservation.</summary>
    [Fact]
    public async Task SpacePage_StdioServer_PublisherNodeId_SerializesAsValueObject_NotBareString()
    {
        var seeded = await fixture.SeedUserAsync($"stdio-shape-{Guid.NewGuid():N}@example.com", "Stdio Shape Test");
        var publisherNodeId = NodeId.New();
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = (await space.PublishMcpServerAsync(
            publisherNodeId, $"stdio-srv-{Guid.NewGuid():N}", "echo", "demo"))!;

        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var spaceResp = await client.GetAsync("/api/space");
        spaceResp.EnsureSuccessStatusCode();
        var spaceBody = await spaceResp.Content.ReadFromJsonAsync<JsonElement>();
        var row = spaceBody.GetProperty("mcpServers").EnumerateArray()
            .First(s => s.GetProperty("id").GetProperty("value").GetString() == server.Id.Value);

        var publisherNodeIdProp = row.GetProperty("publisherNodeId");
        Assert.Equal(JsonValueKind.Object, publisherNodeIdProp.ValueKind);
        Assert.Equal(publisherNodeId.Value, publisherNodeIdProp.GetProperty("value").GetString());
    }

    /// <summary>
    /// Builds an authenticated client against a <c>WithWebHostBuilder</c> factory variant that
    /// has an envelope KEK configured (the shared <c>fixture.Factory</c> deliberately has none —
    /// see IEnvelopeCrypto's fail-closed doc comment). Uses <see cref="ThreadGrainTestKek"/>
    /// (already the test SILO's configured KEK, KoratTestHost.cs) rather than a fresh local KEK
    /// id/bytes, so a future silo-hosted HttpMcpProxyGrain (Task 4) decrypting this same
    /// McpServer's secret resolves the SAME per-space DEK wrap — mirrors
    /// TelegramWebhookTests.NewWebhookHarnessAsync's identical reasoning for bot-token ciphers.
    /// All WithWebHostBuilder variants share the base factory's InMemory DB root, so the
    /// pre-seeded DevSpaceOwnerUserId session mint below is valid against either factory.
    /// </summary>
    private async Task<HttpClient> CreateKekAwareAuthenticatedClientAsync(UserId userId)
    {
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                ["Korat:Envelope:ActiveKekId"] = ThreadGrainTestKek.KekId,
            })));

        using var scope = factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var session = await sessions.CreateAsync(userId, "test-mcp-server", "127.0.0.1", default);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{CanonicalSigninHandler.SessionCookieName}={session.Id:N}");
        return client;
    }
}
