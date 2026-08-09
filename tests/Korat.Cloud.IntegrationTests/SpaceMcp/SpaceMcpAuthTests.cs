using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Korat.Cloud.IntegrationTests.SpaceMcp;

/// <summary>
/// The auth gate on <c>POST /mcp/{spaceSeg}</c>.
///
/// <para>Р25 replaced what this file used to assert. The endpoint took a Space-pinned
/// <c>korat_cli_</c> token alongside OAuth; that path derived the consumer identity from the
/// TOKEN, and a machine has one token, so every agent on it arrived as the same consumer. The
/// tests below are the inverted form: NO CLI token of any scope opens this endpoint now, and the
/// cross-Space guard — which still matters — is proven on the OAuth bearer instead.</para>
///
/// S7: uses a real resolvable Space from <see cref="KoratIntegrationFixture.SeedUserAsync"/>
/// (a 32-hex SpaceId that <c>SpaceSlugService.ResolveSpaceSegmentAsync</c> accepts) — NOT the
/// legacy non-hex <c>"default"</c> Space id, which has no slug and 404s.
/// </summary>
[Trait("Category", "SpaceMcp")]
public sealed class SpaceMcpAuthTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    private static HttpRequestMessage BuildInitializeRequest(string spaceSeg)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // Deliberately NO Origin header — Task 7's responder (SpaceMcpDispatcher) now enforces
        // the Origin allow-list (S3), and real MCP clients (Cursor/Codex/Claude) send no Origin
        // at all; an absent Origin is always allowed regardless of the (default-empty) allow-list,
        // so this is both the realistic shape AND avoids coupling this Task-1 auth-scope test to
        // Task 7's separate Origin-gating test coverage (SpaceMcpResponderTests).
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        return request;
    }

    // ── (a) full-scope token → 403 (rejected, not just "not space-mcp") ─────────

    [Fact]
    public async Task FullToken_Returns403()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-full-{Guid.NewGuid():N}@example.com", "Space MCP Full");
        var fullToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var client = fixture.Factory.CreateClient();
        var request = BuildInitializeRequest(seeded.SpaceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fullToken);

        var response = await client.SendAsync(request);

        // Р25: previously 403 from the CLI branch's scope check. With that branch gone the token
        // reaches the OAuth validator, which does not know it — 401 with the metadata challenge,
        // which is also the more useful answer: it tells the client to go do OAuth.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── (b) cookie session only, no bearer → 401 ────────────────────────────────

    [Fact]
    public async Task CookieOnly_NoBearer_Returns401()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-cookie-{Guid.NewGuid():N}@example.com", "Space MCP Cookie");
        var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var request = BuildInitializeRequest(seeded.SpaceId);
        // Deliberately no Authorization header — SpaceMcpAuth must ignore the session
        // cookie entirely (it never goes through PolymorphicAuthResolver).

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── (c) a scoped CLI token no longer opens the endpoint (Р25 inversion) ─────

    [Fact]
    public async Task SpaceMcpScopedCliToken_IsRejected()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-scoped-{Guid.NewGuid():N}@example.com", "Space MCP Scoped");
        var scopedToken = await fixture.IssueScopedCliTokenAsync(seeded.UserId, seeded.SpaceId);

        var client = fixture.Factory.CreateClient();
        var request = BuildInitializeRequest(seeded.SpaceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scopedToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── (d) an OAuth bearer DOES open it — otherwise (c) would pass on a broken endpoint ──

    [Fact]
    public async Task OAuthToken_OwnSpace_ReachesHandler()
    {
        var seeded = await fixture.SeedUserAsync(
            $"space-mcp-oauth-{Guid.NewGuid():N}@example.com", "Space MCP OAuth");
        var (accessToken, _) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, seeded.UserId, seeded.SpaceId);

        var client = fixture.Factory.CreateClient();
        var request = BuildInitializeRequest(seeded.SpaceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── cross-Space guard, now on the OAuth bearer ──────────────────────────────

    [Fact]
    public async Task OAuthToken_UsedAgainstDifferentSpace_IsRejected()
    {
        // The guard itself did not change with Р25 — only what carries the identity. A bearer
        // minted for owner A's Space must not open owner B's endpoint, and the failure must not
        // disclose whether B's Space exists.
        var ownerA = await fixture.SeedUserAsync(
            $"space-mcp-crossA-{Guid.NewGuid():N}@example.com", "Space MCP Cross A");
        var ownerB = await fixture.SeedUserAsync(
            $"space-mcp-crossB-{Guid.NewGuid():N}@example.com", "Space MCP Cross B");

        var (accessToken, _) = await SpaceMcpOAuthTestAccess.IssueAsync(
            fixture, ownerA.UserId, ownerA.SpaceId);

        var client = fixture.Factory.CreateClient();
        var request = BuildInitializeRequest(ownerB.SpaceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected a rejection for a cross-Space bearer, got {(int)response.StatusCode}.");
    }
}
