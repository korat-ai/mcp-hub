using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;

namespace Korat.Auth.Tests;

/// <summary>
/// Node Hello over gRPC with a token from the Korat sign-in provider.
///
/// This port needed its own branch and nearly did not get one. The REST surface accepts these
/// tokens already, so `korat login` would have succeeded and then `korat up`, `korat service run`
/// and the bridge would all have been turned away at Hello — and because this path fails closed,
/// the node would simply look unauthorised, with nothing pointing at the missing branch.
/// </summary>
public sealed class GrpcSsoBearerTests
{
    private const string Token = "header.payload.signature";
    private const string Subject = "9fdc73931e2548528f467372bc838d7d";

    private static Metadata WithBearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    private static SsoPrincipal Principal => new(Subject, "me@example.test", "dev-1", ["openid"]);

    [Fact]
    public async Task A_linked_subject_may_open_a_node_session()
    {
        var person = UserId.New();

        var result = await GrpcAuthHelper.TryResolveBearerAsync(
            WithBearer(Token), NoCliTokens.Instance,
            new StubSsoTokens(Token, Principal), new StubSsoIdentities(Subject, person),
            CancellationToken.None);

        Assert.Equal(BearerOutcome.Valid, result.Outcome);
        Assert.Equal(person.Value, result.UserId);
    }

    [Fact]
    public async Task A_valid_token_for_an_unlinked_subject_is_turned_away()
    {
        // Fail closed, like every other unresolvable credential on this port: a genuine token
        // whose owner has never signed in here is not a node identity.
        var result = await GrpcAuthHelper.TryResolveBearerAsync(
            WithBearer(Token), NoCliTokens.Instance,
            new StubSsoTokens(Token, Principal), NoSsoIdentities.Instance,
            CancellationToken.None);

        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task With_no_provider_configured_the_outcome_is_unchanged()
    {
        // The state this app is in today: an unknown credential stays Invalid, exactly as
        // before the branch existed. Adding the branch must not change what happens to
        // anything it does not recognise.
        var result = await GrpcAuthHelper.TryResolveBearerAsync(
            WithBearer("korat_cli_unknown"), NoCliTokens.Instance,
            InertSsoTokens.Instance, NoSsoIdentities.Instance,
            CancellationToken.None);

        Assert.Equal(BearerOutcome.Invalid, result.Outcome);
    }
}
