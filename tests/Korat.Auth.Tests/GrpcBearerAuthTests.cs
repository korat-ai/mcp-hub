using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Cloud.Web.Auth.Services;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="GrpcAuthHelper.TryResolveBearerAsync"/>.
///
/// The helper is extracted from <c>NodeGatewayService</c> so that the Bearer-vs-owner
/// metadata resolution logic can be tested without spinning up a gRPC server.
///
/// Tested invariants:
///   • <c>authorization: Bearer korat_cli_good</c> → Valid outcome, stub's UserId.
///   • <c>authorization: Bearer bad-token</c>      → Invalid outcome (present-but-rejected).
///   • No authorization header at all              → Absent outcome (fall back to owner path).
///   • Case-insensitive "bearer" prefix            → resolves correctly.
///   • Empty / whitespace token after "Bearer "    → Absent (treated as missing header).
///   • Non-Bearer scheme                           → Absent (legacy owner-token path).
/// </summary>
public class GrpcBearerAuthTests
{
    // ── stub ─────────────────────────────────────────────────────────────────

    private sealed class StubCliTokens(Guid? returnedUserId) : ICliTokenService
    {
        public Task<CliTokenIssueResult> IssueAsync(Guid userId, string scope, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct)
            => Task.FromResult(rawToken == "korat_cli_good" ? returnedUserId : (Guid?)null);

        public Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct)
        {
            if (rawToken == "korat_cli_good" && returnedUserId is not null)
                return Task.FromResult<(Guid UserId, string Scope)?>((returnedUserId.Value, "full"));
            // N-f: a restricted Space-MCP-scoped token, otherwise a perfectly valid credential.
            if (rawToken == "korat_cli_spacemcp" && returnedUserId is not null)
                return Task.FromResult<(Guid UserId, string Scope)?>((returnedUserId.Value, "space-mcp:some-space-id"));
            if (rawToken == "korat_cli_bridgeonly" && returnedUserId is not null)
                return Task.FromResult<(Guid UserId, string Scope)?>((returnedUserId.Value, "bridge-only"));
            // N-f (adversarial review, second pass): an arbitrary/unknown scope that is neither
            // "full" nor "bridge-only" nor "space-mcp:*" — a perfectly valid credential otherwise,
            // proving the allowlist rejects by default rather than merely denylisting the one
            // family we already knew about.
            if (rawToken == "korat_cli_unknownscope" && returnedUserId is not null)
                return Task.FromResult<(Guid UserId, string Scope)?>((returnedUserId.Value, "some-future-scope"));
            return Task.FromResult<(Guid UserId, string Scope)?>(null);
        }

        public Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<bool> RevokeAsync(string rawToken, CancellationToken ct)
            => Task.FromResult(false);

        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CliTokenListItem>>(Array.Empty<CliTokenListItem>());

        public Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct)
            => Task.FromResult(false);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Metadata MetadataWithAuthorization(string value)
    {
        var md = new Metadata();
        md.Add("authorization", value);
        return md;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidBearerToken_ReturnsValid_WithUserId()
    {
        var expectedUserId = Guid.NewGuid();
        var stub = new StubCliTokens(expectedUserId);

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_good"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Valid, result.Outcome);
        Assert.Equal(expectedUserId, result.UserId);
    }

    [Fact]
    public async Task InvalidBearerToken_ReturnsInvalid_NotAbsent()
    {
        // A Bearer header was present but the token was rejected (invalid/expired/revoked).
        // Must return Invalid so the caller can fail closed, NOT Absent (which would fall
        // through to the owner-token path and degrade a just-revoked token).
        var stub = new StubCliTokens(Guid.NewGuid());

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_bad"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task NoAuthorizationHeader_ReturnsAbsent()
    {
        var stub = new StubCliTokens(Guid.NewGuid());

        var result = await GrpcAuthHelper.TryResolveBearerAsync(new Metadata(), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Absent, result.Outcome);
    }

    [Fact]
    public async Task BearerPrefix_IsCaseInsensitive()
    {
        var expectedUserId = Guid.NewGuid();
        var stub = new StubCliTokens(expectedUserId);

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("bearer korat_cli_good"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Valid, result.Outcome);
        Assert.Equal(expectedUserId, result.UserId);
    }

    [Fact]
    public async Task WhitespaceToken_AfterBearer_ReturnsAbsent()
    {
        // An empty/whitespace string after "Bearer " is treated as Absent
        // (some libraries emit bare "Bearer" with no token — treat it like a missing header).
        var stub = new StubCliTokens(Guid.NewGuid());

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer   "), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Absent, result.Outcome);
    }

    [Fact]
    public async Task NonBearerScheme_ReturnsAbsent()
    {
        var stub = new StubCliTokens(Guid.NewGuid());

        // e.g. the legacy owner header set as "authorization: <secret>" (not Bearer)
        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("dev-owner-secret"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Absent, result.Outcome);
    }

    // ── Sec M1: revoked CLI token must not fall through to owner path ─────────

    [Fact]
    public async Task RevokedCliToken_ReturnsInvalid_PreventingOwnerPathFallthrough()
    {
        // Regression guard for sec M1: a token that ValidateAsync rejects (revoked/expired)
        // must return Invalid, not Absent — so NodeGatewayService fails the Hello rather than
        // degrading to the owner-token path. The stub simulates a revoked token by returning
        // null for the "korat_cli_revoked" token.
        var stub = new StubCliTokens(Guid.NewGuid()); // ValidateAsync returns null for unknown tokens

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_revoked"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        // Must be Invalid (header present, token rejected) — NOT Absent (no header).
        // This is the invariant that prevents fall-through to the owner-token path.
        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
        Assert.Null(result.UserId);
    }

    // ── N-f: a space-mcp-scoped token must never also work as a full node credential ─────────

    [Fact]
    public async Task SpaceMcpScopedToken_ReturnsInvalid_NeverAcceptedAsNodeCredential()
    {
        // N-f (adversarial review): a "space-mcp:{spaceId}" token is a RESTRICTED credential
        // minted only for the Streamable-HTTP Space-MCP responder — it must never also work as a
        // full relay-node Hello credential (that would defeat the whole least-privilege point of
        // the restricted scope). Must be Invalid (fail closed), not Valid.
        var stub = new StubCliTokens(Guid.NewGuid());

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_spacemcp"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task BridgeOnlyScopedToken_StillAcceptedAtNodeHello()
    {
        // Regression guard: N-f targets ONLY the space-mcp:* scope family — "bridge-only" (a real
        // machine relay credential) must keep working at node Hello exactly as before.
        var expectedUserId = Guid.NewGuid();
        var stub = new StubCliTokens(expectedUserId);

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_bridgeonly"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Valid, result.Outcome);
        Assert.Equal(expectedUserId, result.UserId);
    }

    // ── N-f (adversarial review, second pass): allowlist, not denylist ───────────────────────

    [Fact]
    public async Task UnknownScopedToken_ReturnsInvalid_AllowlistRejectsByDefault()
    {
        // N-f (second pass): the fix flips the space-mcp:*-only denylist into an allowlist of
        // exactly "full"/"bridge-only" — an otherwise perfectly valid credential carrying ANY
        // other scope (not merely the one family we already knew about) must be refused as a node
        // Hello credential, fail-closed, by default.
        var stub = new StubCliTokens(Guid.NewGuid());

        var result = await GrpcAuthHelper.TryResolveBearerAsync(MetadataWithAuthorization("Bearer korat_cli_unknownscope"), stub, InertSsoTokens.Instance, NoSsoIdentities.Instance, default);

        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
        Assert.Null(result.UserId);
    }
}
