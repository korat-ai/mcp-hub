using Microsoft.Extensions.Logging.Abstractions;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

public class PolymorphicAuthResolverBearerTests
{
    // ─── stubs ───────────────────────────────────────────────────────────────

    private sealed class StubSession(SessionBumpResult? r) : ISessionService
    {
        public Task<LoginSession> CreateAsync(UserId u, string? ua, string? ip, CancellationToken ct) => throw new NotImplementedException();
        public Task<SessionBumpResult?> ValidateAndBumpAsync(Guid id, CancellationToken ct) => Task.FromResult(r);
        public Task RevokeAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeOthersAsync(UserId u, Guid except, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<LoginSession>> ListActiveAsync(UserId u, CancellationToken ct) => Task.FromResult<IReadOnlyList<LoginSession>>(Array.Empty<LoginSession>());
    }

    private sealed class StubCliTokens(Guid? returnedUserId, string scope = "full") : ICliTokenService
    {
        public Task<CliTokenIssueResult> IssueAsync(Guid userId, string s, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct) =>
            Task.FromResult(rawToken == "korat_cli_good" ? returnedUserId : (Guid?)null);
        public Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct)
        {
            if (rawToken == "korat_cli_good" && returnedUserId is not null)
                return Task.FromResult<(Guid UserId, string Scope)?>((returnedUserId.Value, scope));
            return Task.FromResult<(Guid UserId, string Scope)?>(null);
        }
        public Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<bool> RevokeAsync(string rawToken, CancellationToken ct) => Task.FromResult(false);
        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CliTokenListItem>>(Array.Empty<CliTokenListItem>());
        public Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct)
            => Task.FromResult(false);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static PolymorphicAuthResolver BuildResolver(
        ICliTokenService cliTokens,
        ISessionService? sessions = null,
        ISsoTokenValidator? ssoTokens = null,
        ISsoIdentityResolver? ssoIdentities = null)
    {
        return new PolymorphicAuthResolver(
            sessions ?? new StubSession(null),
            cliTokens,
            ssoTokens ?? InertSsoTokens.Instance,
            ssoIdentities ?? NoSsoIdentities.Instance,
            NullLogger<PolymorphicAuthResolver>.Instance);
    }

    private static HttpContext CtxWithBearer(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx;
    }

    private static HttpContext CtxWithCookieAndBearer(string sessionId, string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{CanonicalSigninHandler.SessionCookieName}={sessionId}";
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx;
    }

    // ─── tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BearerCliToken_Resolves_WhenNoSession()
    {
        var expectedUserId = Guid.NewGuid();
        var resolver = BuildResolver(new StubCliTokens(expectedUserId));

        var ctx = CtxWithBearer("korat_cli_good");
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        Assert.Equal(new UserId(expectedUserId), resolved!.UserId);
    }

    [Fact]
    public async Task BadBearer_ReturnsNull()
    {
        var resolver = BuildResolver(new StubCliTokens(null));

        var ctx = CtxWithBearer("korat_cli_bad");
        Assert.Null(await resolver.ResolveAsync(ctx, default));
    }

    [Fact]
    public async Task ValidSession_Beats_ValidBearer()
    {
        var sessionUserId = UserId.New();
        var bearerUserId = Guid.NewGuid();

        var sessionStub = new StubSession(new SessionBumpResult(sessionUserId, DateTimeOffset.UtcNow.AddDays(30)));
        var resolver = BuildResolver(new StubCliTokens(bearerUserId), sessions: sessionStub);

        var ctx = CtxWithCookieAndBearer(Guid.NewGuid().ToString("N"), "korat_cli_good");
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        // LoginSession should win — userId matches the session, not the bearer stub's userId
        Assert.Equal(sessionUserId, resolved!.UserId);
    }

    [Fact]
    public async Task BearerCliToken_CaseInsensitive_BearerPrefix()
    {
        var expectedUserId = Guid.NewGuid();
        var resolver = BuildResolver(new StubCliTokens(expectedUserId));

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "bearer korat_cli_good";
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        Assert.Equal(new UserId(expectedUserId), resolved!.UserId);
    }

    [Fact]
    public async Task EmptyBearerToken_ReturnsNull()
    {
        var resolver = BuildResolver(new StubCliTokens(Guid.NewGuid()));

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer   ";
        Assert.Null(await resolver.ResolveAsync(ctx, default));
    }

    // ── MAJOR-2: Scope is propagated through ResolvedIdentity ────────────────

    [Fact]
    public async Task BridgeOnlyToken_ResolvedIdentity_CarriesScope_BridgeOnly()
    {
        // A bridge-only token must resolve to a non-null identity whose Scope is "bridge-only".
        var userId = Guid.NewGuid();
        var resolver = BuildResolver(new StubCliTokens(userId, scope: "bridge-only"));

        var ctx = CtxWithBearer("korat_cli_good");
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        Assert.Equal(new UserId(userId), resolved!.UserId);
        Assert.Equal("bridge-only", resolved.Scope);
    }

    [Fact]
    public async Task FullToken_ResolvedIdentity_CarriesScope_Full()
    {
        var userId = Guid.NewGuid();
        var resolver = BuildResolver(new StubCliTokens(userId, scope: "full"));

        var ctx = CtxWithBearer("korat_cli_good");
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        Assert.Equal("full", resolved!.Scope);
    }

    [Fact]
    public async Task SessionAuth_ResolvedIdentity_HasDefaultScope_Full()
    {
        // Cookie/session auth always produces Scope="full" (the ResolvedIdentity default).
        var sessionUserId = UserId.New();
        var sessionStub = new StubSession(new SessionBumpResult(sessionUserId, DateTimeOffset.UtcNow.AddDays(30)));
        var resolver = BuildResolver(new StubCliTokens(null), sessions: sessionStub);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{CanonicalSigninHandler.SessionCookieName}={Guid.NewGuid():N}";
        var resolved = await resolver.ResolveAsync(ctx, default);

        Assert.NotNull(resolved);
        Assert.Equal("full", resolved!.Scope);
    }
}
