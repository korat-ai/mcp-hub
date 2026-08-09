using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;

namespace Korat.Auth.Tests;

/// <summary>
/// An SSO validator that answers nothing — the state this app is in until an issuer is
/// configured. Existing resolver tests use it so they keep testing what they always tested:
/// the cookie and CLI-token paths, unaffected by the new branch.
/// </summary>
public sealed class InertSsoTokens : ISsoTokenValidator
{
    public static readonly InertSsoTokens Instance = new();

    public bool Enabled => false;

    public Task<SsoPrincipal?> ValidateAsync(string token, CancellationToken ct)
        => Task.FromResult<SsoPrincipal?>(null);
}

/// <summary>A validator that accepts exactly one token and calls it one person.</summary>
public sealed class StubSsoTokens(string token, SsoPrincipal principal) : ISsoTokenValidator
{
    public bool Enabled => true;

    public Task<SsoPrincipal?> ValidateAsync(string candidate, CancellationToken ct)
        => Task.FromResult(candidate == token ? principal : null);
}

/// <summary>Nobody is linked. What a fresh install looks like.</summary>
public sealed class NoSsoIdentities : ISsoIdentityResolver
{
    public static readonly NoSsoIdentities Instance = new();

    public Task<UserId?> FindAsync(string ssoSubject, CancellationToken ct)
        => Task.FromResult<UserId?>(null);
}

/// <summary>Exactly one subject is linked, to the given person.</summary>
public sealed class StubSsoIdentities(string subject, UserId userId) : ISsoIdentityResolver
{
    public Task<UserId?> FindAsync(string ssoSubject, CancellationToken ct)
        => Task.FromResult<UserId?>(ssoSubject == subject ? userId : null);
}

/// <summary>No cookie session, ever. The bearer paths are what these tests are about.</summary>
public sealed class NoSessions : ISessionService
{
    public static readonly NoSessions Instance = new();

    public Task<SessionBumpResult?> ValidateAndBumpAsync(Guid sessionId, CancellationToken ct)
        => Task.FromResult<SessionBumpResult?>(null);

    public Task<LoginSession> CreateAsync(UserId userId, string? userAgent, string? ip, CancellationToken ct)
        => throw new NotSupportedException();
    public Task RevokeAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task RevokeOthersAsync(UserId userId, Guid exceptSessionId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<LoginSession>> ListActiveAsync(UserId userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LoginSession>>([]);
}

/// <summary>This app's own credential never matches. Keeps the SSO branch reachable.</summary>
public sealed class NoCliTokens : ICliTokenService
{
    public static readonly NoCliTokens Instance = new();

    public Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct)
        => Task.FromResult<(Guid, string)?>(null);

    public Task<CliTokenIssueResult> IssueAsync(Guid userId, string scope, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<bool> RevokeAsync(string rawToken, CancellationToken ct) => Task.FromResult(false);
    public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct) => Task.FromResult(0);
    public Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CliTokenListItem>>([]);
}
