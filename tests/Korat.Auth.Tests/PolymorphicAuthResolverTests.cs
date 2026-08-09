using Microsoft.Extensions.Logging.Abstractions;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

public class PolymorphicAuthResolverTests
{
    private sealed class StubSession(SessionBumpResult? r) : ISessionService
    {
        public Task<LoginSession> CreateAsync(UserId u, string? ua, string? ip, CancellationToken ct) => throw new NotImplementedException();
        public Task<SessionBumpResult?> ValidateAndBumpAsync(Guid id, CancellationToken ct) => Task.FromResult(r);
        public Task RevokeAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeOthersAsync(UserId u, Guid except, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<LoginSession>> ListActiveAsync(UserId u, CancellationToken ct) => Task.FromResult<IReadOnlyList<LoginSession>>(Array.Empty<LoginSession>());
    }

    // No-op CLI token stub — existing tests don't exercise the Bearer branch.
    private sealed class NullCliTokens : ICliTokenService
    {
        public static readonly NullCliTokens Instance = new();
        public Task<CliTokenIssueResult> IssueAsync(Guid userId, string scope, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct)
            => Task.FromResult<(Guid UserId, string Scope)?>(null);
        public Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<bool> RevokeAsync(string rawToken, CancellationToken ct) => Task.FromResult(false);
        public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CliTokenListItem>>(Array.Empty<CliTokenListItem>());
        public Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct)
            => Task.FromResult(false);
    }

    private static HttpContext CtxWithSessionCookie(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{CanonicalSigninHandler.SessionCookieName}={sessionId}";
        return ctx;
    }

    [Fact]
    public async Task ValidSession_Resolves_UserId()
    {
        var sessionId = Guid.NewGuid();
        var expectedUser = UserId.New();
        var stub = new StubSession(new SessionBumpResult(expectedUser, DateTimeOffset.UtcNow.AddDays(30)));
        var r = new PolymorphicAuthResolver(stub, NullCliTokens.Instance, InertSsoTokens.Instance, NoSsoIdentities.Instance, NullLogger<PolymorphicAuthResolver>.Instance);
        var ctx = CtxWithSessionCookie(sessionId.ToString("N"));
        var resolved = await r.ResolveAsync(ctx, default);
        Assert.NotNull(resolved);
        Assert.Equal(expectedUser, resolved!.UserId);
    }

    [Fact]
    public async Task InvalidSession_ReturnsNull()
    {
        var stub = new StubSession(null);
        var r = new PolymorphicAuthResolver(stub, NullCliTokens.Instance, InertSsoTokens.Instance, NoSsoIdentities.Instance, NullLogger<PolymorphicAuthResolver>.Instance);
        var ctx = CtxWithSessionCookie(Guid.NewGuid().ToString("N"));
        Assert.Null(await r.ResolveAsync(ctx, default));
    }

    [Fact]
    public async Task NoCredentials_ReturnsNull()
    {
        var stub = new StubSession(null);
        var r = new PolymorphicAuthResolver(stub, NullCliTokens.Instance, InertSsoTokens.Instance, NoSsoIdentities.Instance, NullLogger<PolymorphicAuthResolver>.Instance);
        var ctx = new DefaultHttpContext();
        Assert.Null(await r.ResolveAsync(ctx, default));
    }

    [Fact]
    public async Task LegacyCookie_IsIgnored()
    {
        // After removal of dev-shortcut mode, the korat_owner cookie must have no effect.
        var stub = new StubSession(null);
        var r = new PolymorphicAuthResolver(stub, NullCliTokens.Instance, InertSsoTokens.Instance, NoSsoIdentities.Instance, NullLogger<PolymorphicAuthResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = "korat_owner=dev-owner-secret";
        Assert.Null(await r.ResolveAsync(ctx, default));
    }

    [Fact]
    public async Task LegacyOwnerTokenHeader_IsIgnored()
    {
        // After removal of dev-shortcut mode, X-Korat-Owner-Token must have no effect.
        var stub = new StubSession(null);
        var r = new PolymorphicAuthResolver(stub, NullCliTokens.Instance, InertSsoTokens.Instance, NoSsoIdentities.Instance, NullLogger<PolymorphicAuthResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Korat-Owner-Token"] = "dev-owner-secret";
        Assert.Null(await r.ResolveAsync(ctx, default));
    }
}
