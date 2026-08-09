using Korat.Domain.Entities;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Domain.Tests;

public class StateTransitionTests
{
    private static readonly UserId AnyUserId = UserId.New();

    [Fact]
    public void ApproveAccessRequest_FromPending_Succeeds()
    {
        var request = CreatePendingRequest();
        StateTransitions.ApproveAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        Assert.Equal(AccessRequestStatus.Approved, request.Status);
    }

    [Fact]
    public void ApproveAlreadyApprovedRequest_IsIdempotent()
    {
        var request = CreatePendingRequest();
        var applied = StateTransitions.ApproveAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        Assert.True(applied);
        var reapplied = StateTransitions.ApproveAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        Assert.False(reapplied); // idempotent — no exception, no mutation
    }

    [Fact]
    public void ApproveDeniedRequest_ThrowsKoratDomainException()
    {
        var request = CreatePendingRequest();
        StateTransitions.DenyAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        var ex = Assert.Throws<KoratDomainException>(() =>
            StateTransitions.ApproveAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow));
        Assert.Equal(KoratErrorCode.InvalidStateTransition, ex.Code);
    }

    [Fact]
    public void DenyAccessRequest_FromPending_Succeeds()
    {
        var request = CreatePendingRequest();
        StateTransitions.DenyAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        Assert.Equal(AccessRequestStatus.Denied, request.Status);
    }

    [Fact]
    public void DenyAccessRequest_AlreadyDenied_ThrowsKoratDomainException()
    {
        var request = CreatePendingRequest();
        StateTransitions.DenyAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        var ex = Assert.Throws<KoratDomainException>(() =>
            StateTransitions.DenyAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow));
        Assert.Equal(KoratErrorCode.InvalidStateTransition, ex.Code);
    }

    [Fact]
    public void DenyAccessRequest_AlreadyApproved_ThrowsKoratDomainException()
    {
        var request = CreatePendingRequest();
        StateTransitions.ApproveAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow);
        var ex = Assert.Throws<KoratDomainException>(() =>
            StateTransitions.DenyAccessRequest(request, AnyUserId, DateTimeOffset.UtcNow));
        Assert.Equal(KoratErrorCode.InvalidStateTransition, ex.Code);
    }

    [Fact]
    public void RevokeGrant_FromActive_Succeeds()
    {
        var grant = CreateActiveGrant();
        StateTransitions.RevokeGrant(grant, AnyUserId, DateTimeOffset.UtcNow);
        Assert.Equal(GrantStatus.Revoked, grant.Status);
    }

    [Fact]
    public void RevokeGrant_AlreadyRevoked_ThrowsKoratDomainException()
    {
        var grant = CreateActiveGrant();
        StateTransitions.RevokeGrant(grant, AnyUserId, DateTimeOffset.UtcNow);
        var ex = Assert.Throws<KoratDomainException>(() =>
            StateTransitions.RevokeGrant(grant, AnyUserId, DateTimeOffset.UtcNow));
        Assert.Equal(KoratErrorCode.InvalidStateTransition, ex.Code);
    }

    [Fact]
    public void DisableMcpServer_SetsDisabledStatus()
    {
        var server = CreatePublishedServer();
        var changed = StateTransitions.DisableMcpServer(server, DateTimeOffset.UtcNow);
        Assert.True(changed);
        Assert.Equal(McpServerStatus.Disabled, server.Status);
    }

    [Fact]
    public void DisableMcpServer_AlreadyDisabled_IsIdempotentNoOp()
    {
        var server = CreatePublishedServer();
        var firstNow = DateTimeOffset.UtcNow;
        StateTransitions.DisableMcpServer(server, firstNow);

        var reapplied = StateTransitions.DisableMcpServer(server, firstNow.AddMinutes(5));

        Assert.False(reapplied); // idempotent — no exception, no mutation
        Assert.Equal(McpServerStatus.Disabled, server.Status);
        Assert.Equal(firstNow, server.UpdatedAt); // UpdatedAt must NOT bump on the no-op
    }

    [Fact]
    public void EnableMcpServer_FromDisabled_SetsPublishedStatus()
    {
        var server = CreatePublishedServer();
        StateTransitions.DisableMcpServer(server, DateTimeOffset.UtcNow);

        var changed = StateTransitions.EnableMcpServer(server, DateTimeOffset.UtcNow);

        Assert.True(changed);
        Assert.Equal(McpServerStatus.Published, server.Status);
    }

    [Fact]
    public void EnableMcpServer_AlreadyPublished_IsIdempotentNoOp()
    {
        var server = CreatePublishedServer();
        var createdUpdatedAt = server.UpdatedAt;

        var changed = StateTransitions.EnableMcpServer(server, createdUpdatedAt.AddMinutes(5));

        Assert.False(changed); // idempotent — no exception, no mutation
        Assert.Equal(McpServerStatus.Published, server.Status);
        Assert.Equal(createdUpdatedAt, server.UpdatedAt); // UpdatedAt must NOT bump on the no-op
    }

    [Fact]
    public void EnableMcpServer_OAuthServerWithNoUsableToken_GoesToNeedsReauth_NotPublished()
    {
        var server = CreatePublishedServer();
        server.AuthMode = McpServerAuthModes.Oauth;
        StateTransitions.DisableMcpServer(server, DateTimeOffset.UtcNow);

        var changed = StateTransitions.EnableMcpServer(server, DateTimeOffset.UtcNow, hasUsableOAuthToken: false);

        Assert.True(changed);
        Assert.Equal(McpServerStatus.NeedsReauth, server.Status);
    }

    [Fact]
    public void EnableMcpServer_OAuthServerWithUsableToken_GoesToPublished()
    {
        var server = CreatePublishedServer();
        server.AuthMode = McpServerAuthModes.Oauth;
        StateTransitions.DisableMcpServer(server, DateTimeOffset.UtcNow);

        var changed = StateTransitions.EnableMcpServer(server, DateTimeOffset.UtcNow, hasUsableOAuthToken: true);

        Assert.True(changed);
        Assert.Equal(McpServerStatus.Published, server.Status);
    }

    [Fact]
    public void EnableMcpServer_OAuthServerAlreadyNeedsReauth_WithNoToken_IsIdempotentNoOp()
    {
        var server = CreatePublishedServer();
        server.AuthMode = McpServerAuthModes.Oauth;
        server.Status = McpServerStatus.NeedsReauth;
        var createdUpdatedAt = server.UpdatedAt;

        var changed = StateTransitions.EnableMcpServer(server, createdUpdatedAt.AddMinutes(5), hasUsableOAuthToken: false);

        Assert.False(changed); // idempotent — already in its correct effective state
        Assert.Equal(McpServerStatus.NeedsReauth, server.Status);
        Assert.Equal(createdUpdatedAt, server.UpdatedAt);
    }

    [Fact]
    public void EnableMcpServer_NonOAuthServer_IgnoresTokenFlag_AlwaysPublishes()
    {
        var server = CreatePublishedServer();
        server.AuthMode = McpServerAuthModes.Bearer; // non-oauth: the token flag must be irrelevant
        StateTransitions.DisableMcpServer(server, DateTimeOffset.UtcNow);

        var changed = StateTransitions.EnableMcpServer(server, DateTimeOffset.UtcNow, hasUsableOAuthToken: false);

        Assert.True(changed);
        Assert.Equal(McpServerStatus.Published, server.Status);
    }

    [Fact]
    public void CloseSession_SetsClosedWithReason()
    {
        var session = CreateOpeningSession();
        StateTransitions.CloseSession(session, SessionCloseReason.Revoked, DateTimeOffset.UtcNow);
        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(SessionCloseReason.Revoked, session.CloseReason);
    }

    private static AccessRequest CreatePendingRequest() => new()
    {
        Id = AccessRequestId.New(),
        SpaceId = SpaceId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = McpServerId.New(),
        RequestedByNodeId = NodeId.New(),
        PublisherNodeId = NodeId.New(),
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static Grant CreateActiveGrant() => new()
    {
        Id = GrantId.New(),
        SpaceId = SpaceId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = McpServerId.New(),
        ApprovedByUserId = AnyUserId,
        ApprovedAt = DateTimeOffset.UtcNow
    };

    private static McpServer CreatePublishedServer() => new()
    {
        Id = McpServerId.New(),
        SpaceId = SpaceId.New(),
        PublisherNodeId = NodeId.New(),
        DisplayName = "filesystem",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static RelaySession CreateOpeningSession() => new()
    {
        Id = SessionId.New(),
        SpaceId = SpaceId.New(),
        GrantId = GrantId.New(),
        ConsumerId = ConsumerId.New(),
        McpServerId = McpServerId.New(),
        ClientNodeId = NodeId.New(),
        PublisherNodeId = NodeId.New(),
        HomeGatewayId = GatewayId.New(),
        StartedAt = DateTimeOffset.UtcNow
    };
}
