using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>
/// Resolving a person from a token issued by the Korat sign-in provider.
///
/// This is the branch that makes this app a resource server for SSO. It sits after the CLI
/// token branch, and the order costs nothing: the two credentials are told apart by shape,
/// so neither pays for the other's presence.
/// </summary>
public sealed class SsoBearerResolutionTests
{
    private const string Token = "header.payload.signature";
    private const string Subject = "9fdc73931e2548528f467372bc838d7d";

    private static HttpContext WithBearer(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx;
    }

    private static PolymorphicAuthResolver Build(ISsoTokenValidator tokens, ISsoIdentityResolver identities) =>
        new(NoSessions.Instance, NoCliTokens.Instance, tokens, identities,
            NullLogger<PolymorphicAuthResolver>.Instance);

    [Fact]
    public async Task A_linked_subject_resolves_to_that_person()
    {
        var person = UserId.New();
        var resolver = Build(
            new StubSsoTokens(Token, new SsoPrincipal(Subject, "me@example.test", "dev-1", ["openid"])),
            new StubSsoIdentities(Subject, person));

        var resolved = await resolver.ResolveAsync(WithBearer(Token), CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(person, resolved!.UserId);

        // Full privilege, because the alternative never existed in production: the only place
        // that ever issued a credential passed "full", and "bridge-only" was never handed out.
        Assert.Equal("full", resolved.Scope);
    }

    [Fact]
    public async Task A_valid_token_for_an_unlinked_subject_resolves_to_nobody()
    {
        // The signature is genuine and the person exists at the provider — they have simply
        // never signed in here. Creating the account on this path would mean anyone holding a
        // provider token could populate this database through the relay port, which has no
        // rate limit and no human in front of it.
        var resolver = Build(
            new StubSsoTokens(Token, new SsoPrincipal(Subject, "me@example.test", "dev-1", ["openid"])),
            NoSsoIdentities.Instance);

        Assert.Null(await resolver.ResolveAsync(WithBearer(Token), CancellationToken.None));
    }

    [Fact]
    public async Task A_token_the_validator_rejects_resolves_to_nobody()
    {
        var resolver = Build(
            new StubSsoTokens(Token, new SsoPrincipal(Subject, null, null, [])),
            new StubSsoIdentities(Subject, UserId.New()));

        Assert.Null(await resolver.ResolveAsync(WithBearer("some.other.token"), CancellationToken.None));
    }

    [Fact]
    public async Task With_no_provider_configured_nothing_changes()
    {
        // The state this app is in today. The branch must be inert, not merely harmless:
        // an inert branch that still queries the database would slow every request that
        // carries our own credential.
        var resolver = Build(InertSsoTokens.Instance, new ThrowingIdentities());

        Assert.Null(await resolver.ResolveAsync(WithBearer(Token), CancellationToken.None));
    }

    /// <summary>Fails the test if the identity lookup is reached at all.</summary>
    private sealed class ThrowingIdentities : ISsoIdentityResolver
    {
        public Task<UserId?> FindAsync(string ssoSubject, CancellationToken ct)
            => throw new InvalidOperationException(
                "поиск личности не должен вызываться, когда провайдер не настроен");
    }
}
