using Korat.Cloud.IntegrationTests;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Relay.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for the Bearer branch of <c>HandleHelloAsync</c> in
/// <c>NodeGatewayService</c>.
///
/// Covered invariants:
///   • A valid Bearer token with a provisioned default Space → Hello accepted;
///     the returned GatewayHello confirms the connection (SpaceId resolved server-side).
///   • A valid Bearer token for a user who has no default Space → AccessDenied
///     with reason "No default Space for user".
///   • A valid Bearer token whose user already has a node registered under a
///     different Space → AccessDenied with reason "SpaceId does not match node
///     registration" (SEC-CRITICAL-1 cross-tenant re-homing guard, Bearer path).
///   • GatewayHello advertises non-empty CurrentCliVersion and MinSupportedCliVersion
///     from cloud configuration (CLI version negotiation, Task 2).
/// </summary>
public sealed class NodeGatewayBearerHelloTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ValidBearer_WithProvisionedSpace_HelloAccepted()
    {
        // Arrange: seed a real user+space and issue a CLI token for that user.
        var seeded = await fixture.SeedUserAsync(
            $"bearer-hello-ok-{Guid.NewGuid():N}@example.com",
            "Bearer Hello OK");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        // Act: send a Hello without NodeAuthToken — Bearer is sufficient auth.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "bearer-hello-ok",
                // No NodeAuthToken — Bearer path skips HMAC check.
                // SpaceId is intentionally omitted — server resolves it from the token.
            }
        });

        // Assert: first response must be GatewayHello (accepted).
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(
            GatewayToNodeMessage.PayloadOneofCase.Hello,
            call.ResponseStream.Current.PayloadCase);
    }

    [Fact]
    public async Task ValidBearer_WithProvisionedSpace_HelloAdvertisesCliVersions()
    {
        // Arrange: seed a real user+space and issue a CLI token for that user.
        var seeded = await fixture.SeedUserAsync(
            $"bearer-hello-versions-{Guid.NewGuid():N}@example.com",
            "Bearer Hello Versions");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        // Act: send a Hello with a cli_version field populated.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "bearer-hello-versions",
                CliVersion = "0.2.8",
            }
        });

        // Assert: GatewayHello must advertise non-empty current and min CLI versions.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, response.PayloadCase);
        Assert.NotEmpty(response.Hello.CurrentCliVersion);
        Assert.NotEmpty(response.Hello.MinSupportedCliVersion);
    }

    [Fact]
    public async Task BelowMinSupportedCli_IsAccepted_NotRefused()
    {
        // Locks the deprecation-window invariant: a CLI below MinSupportedCliVersion (default 0.2.0)
        // is NUDGED (cloud logs a warning) but NEVER refused — old CLIs must keep working.
        var seeded = await fixture.SeedUserAsync(
            $"bearer-hello-belowmin-{Guid.NewGuid():N}@example.com",
            "Bearer Hello BelowMin");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "bearer-hello-belowmin",
                CliVersion = "0.1.0", // below the default MinSupportedCliVersion (0.2.0)
            }
        });

        // Accepted: a normal GatewayHello, NOT AccessDenied.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Hello, call.ResponseStream.Current.PayloadCase);
    }

    [Fact]
    public async Task ValidBearer_NoDefaultSpace_ReturnsAccessDenied()
    {
        // Arrange: seed a User row (required so ValidateAsync's join succeeds) but do NOT
        // provision a default Space, so SpaceResolver returns null and HandleHelloAsync returns
        // AccessDenied("No default Space for user"). ValidateAsync now checks User.Status = Active,
        // so a CliToken for a non-existent user would not validate at all — we must seed the User.
        var orphanUserId = await fixture.SeedUserWithoutSpaceAsync(
            $"orphan-no-space-{Guid.NewGuid():N}@example.com");
        var cliToken = await fixture.IssueCliTokenAsync(orphanUserId);

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        // Act.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "bearer-no-space",
            }
        });

        // Assert: first response must be AccessDenied.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal("No default Space for user", response.AccessDenied.Reason);
    }

    // ── No auth → rejected ───────────────────────────────────────────────────

    [Fact]
    public async Task Hello_WithoutBearer_ReturnsAccessDenied()
    {
        // Nodes must authenticate via Bearer. A Hello with no Authorization header
        // and no NodeAuthToken must be rejected with AccessDenied.
        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        using var call = grpcClient.Connect(); // no call options = no Bearer header

        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "no-auth-test",
            }
        });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal("Invalid node auth token", response.AccessDenied.Reason);
    }

    // ── Sec M1: revoked CLI token must be rejected ───────────────────────────

    [Fact]
    public async Task RevokedBearerToken_ReturnsAccessDenied()
    {
        // Regression guard for sec M1: after revoking a CLI token, sending it in the
        // Bearer header must be rejected outright.
        //
        // Arrange: seed a real user+space and issue then immediately revoke a CLI token.
        var seeded = await fixture.SeedUserAsync(
            $"bearer-revoked-{Guid.NewGuid():N}@example.com",
            "Revoked Bearer Test");
        var cliToken = await fixture.IssueCliTokenAsync(seeded.UserId);

        // Revoke the token so ValidateAsync will return null for it.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
            await cliTokens.RevokeAsync(cliToken, default);
        }

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliToken);
        using var call = grpcClient.Connect(callOptions);

        // Act: send a Hello with the revoked token.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = NodeId.New().Value,
                DisplayName = "bearer-revoked-test",
                // No NodeAuthToken — Bearer path; HMAC check is not used.
            }
        });

        // Assert: must get AccessDenied.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.NotEqual("", response.AccessDenied.Reason);
    }

    [Fact]
    public async Task ValidBearer_NodeAlreadyOwnedByAnotherSpace_ReturnsAccessDenied()
    {
        // Arrange: seed two independent users (each gets their own default Space).
        var userA = await fixture.SeedUserAsync(
            $"bearer-owner-a-{Guid.NewGuid():N}@example.com",
            "Bearer Owner A");
        var userB = await fixture.SeedUserAsync(
            $"bearer-owner-b-{Guid.NewGuid():N}@example.com",
            "Bearer Owner B");

        // Pre-register a node under user B's Space.
        var victimNodeId = NodeId.New().Value;
        await fixture.SeedNodeForSpaceAsync(victimNodeId, userB.SpaceId);

        // Issue a CLI token for user A.
        var cliTokenA = await fixture.IssueCliTokenAsync(userA.UserId);

        var grpcClient = GrpcTestClient.Create(fixture.Factory);
        var callOptions = GrpcTestClient.BearerCallOptions(cliTokenA);
        using var call = grpcClient.Connect(callOptions);

        // Act: user A tries to connect with the NodeId that belongs to user B's Space.
        await call.RequestStream.WriteAsync(new NodeToGatewayMessage
        {
            Hello = new NodeHello
            {
                NodeId = victimNodeId,
                DisplayName = "bearer-hijack-attempt",
            }
        });

        // Assert: gateway must reject with SpaceId mismatch — not silently re-home the node.
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var response = call.ResponseStream.Current;
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.AccessDenied, response.PayloadCase);
        Assert.Equal("SpaceId does not match node registration", response.AccessDenied.Reason);
    }
}
