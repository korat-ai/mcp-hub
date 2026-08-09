using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests;

public sealed class HttpMcpServerGrainTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task CreateHttpMcpServerAsync_PublishesWithNoPublisherNode()
    {
        var seeded = await fixture.SeedUserAsync(
            $"http-mcp-grain-{Guid.NewGuid():N}@example.com", "HTTP MCP Grain Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);

        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-{Guid.NewGuid():N}",
            "https://example.test/mcp",
            McpServerAuthModes.Bearer,
            authHeaderName: null,
            secretHint: "…ab12");

        Assert.Equal(McpServerTransports.HttpCloud, server.Transport);
        Assert.Equal(string.Empty, server.PublisherNodeId.Value);
        Assert.Equal("https://example.test/mcp", server.RemoteUrl);
        Assert.Equal(McpServerAuthModes.Bearer, server.AuthMode);
        Assert.Equal("…ab12", server.SecretHint);
        Assert.Equal(McpServerStatus.Published, server.Status);
    }

    [Fact]
    public async Task CreateHttpMcpServerAsync_DuplicateName_ThrowsRegardlessOfTransport()
    {
        var seeded = await fixture.SeedUserAsync(
            $"http-mcp-dup-{Guid.NewGuid():N}@example.com", "HTTP MCP Dup Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var name = $"http-srv-dup-{Guid.NewGuid():N}";

        // A stdio_node server with this name already exists.
        await space.PublishMcpServerAsync(NodeId.New(), name, "echo", "x");

        await Assert.ThrowsAsync<KoratDomainException>(() =>
            space.CreateHttpMcpServerAsync(name, "https://example.test/mcp", McpServerAuthModes.None, null, null));
    }

    [Fact]
    public async Task UpdateHttpCloudConfigAsync_ChangesUrlWithoutTouchingSecretHint()
    {
        var seeded = await fixture.SeedUserAsync(
            $"http-mcp-update-{Guid.NewGuid():N}@example.com", "HTTP MCP Update Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-upd-{Guid.NewGuid():N}", "https://old.test/mcp",
            McpServerAuthModes.Header, "X-Api-Key", "…ffff");

        var serverGrain = fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value);
        var updated = await serverGrain.UpdateHttpCloudConfigAsync(
            remoteUrl: "https://new.test/mcp", authMode: null, authHeaderName: null, secretHint: null);

        Assert.Equal("https://new.test/mcp", updated.RemoteUrl);
        Assert.Equal(McpServerAuthModes.Header, updated.AuthMode); // unchanged (null = keep)
        Assert.Equal("X-Api-Key", updated.AuthHeaderName);          // unchanged
        Assert.Equal("…ffff", updated.SecretHint);                  // unchanged — no secret update requested
    }

    /// <summary>Finding 16, M4: clearSecretHint=true must null SecretHint even though secretHint
    /// (the value parameter) is also null — the two are independent, mirroring clearAuthHeaderName's
    /// distinct-from-authHeaderName convention. Without this flag, "null = keep" cannot express
    /// "clear" and a cleared secret's stale hint would silently persist forever.</summary>
    [Fact]
    public async Task UpdateHttpCloudConfigAsync_ClearSecretHint_NullsHintIndependentlyOfOtherFields()
    {
        var seeded = await fixture.SeedUserAsync(
            $"http-mcp-clearsecret-{Guid.NewGuid():N}@example.com", "HTTP MCP Clear Secret Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-clearsecret-{Guid.NewGuid():N}", "https://old.test/mcp",
            McpServerAuthModes.Bearer, authHeaderName: null, secretHint: "…beef");

        var serverGrain = fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value);
        var updated = await serverGrain.UpdateHttpCloudConfigAsync(
            remoteUrl: null, authMode: null, authHeaderName: null, secretHint: null,
            clearAuthHeaderName: false, clearSecretHint: true);

        Assert.Null(updated.SecretHint);
        Assert.Equal("https://old.test/mcp", updated.RemoteUrl); // unchanged — only the secret was cleared
    }

    [Fact]
    public async Task MarkOAuthConnected_FromNeedsReauth_TransitionsToPublished()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-mark-{Guid.NewGuid():N}@example.com", "OAuth Mark Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-mark-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);
        Assert.Equal(McpServerStatus.NeedsReauth, server.Status); // oauth create starts pre-consent

        var updated = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).MarkOAuthConnectedAsync();

        Assert.Equal(McpServerStatus.Published, updated.Status);
    }

    [Fact]
    public async Task MarkNeedsReauth_FromPublished_TransitionsBack()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-mark2-{Guid.NewGuid():N}@example.com", "OAuth Mark Test 2");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-mark2-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);
        await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).MarkOAuthConnectedAsync();

        var updated = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).MarkNeedsReauthAsync();

        Assert.Equal(McpServerStatus.NeedsReauth, updated.Status);
    }

    [Fact]
    public async Task MarkOAuthConnected_OnDisabledServer_StaysDisabled_DoesNotFailOpenToPublished()
    {
        // T1 opus-nit, closed in Task 4: a server the owner has since Disabled must NOT be
        // silently re-published just because a token arrived (e.g. a slow/replayed callback
        // completing after the owner disabled the server) — admission-time Status must never be
        // fail-opened by a callback race.
        var seeded = await fixture.SeedUserAsync($"oauth-mark-disabled-{Guid.NewGuid():N}@example.com", "OAuth Mark Disabled Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-mark-disabled-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);
        var serverGrain = fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value);
        await serverGrain.DisableAsync(seeded.UserId);

        var updated = await serverGrain.MarkOAuthConnectedAsync();

        Assert.Equal(McpServerStatus.Disabled, updated.Status);
    }

    [Fact]
    public async Task EnableAsync_OAuthServerWithNoStoredToken_StaysNeedsReauth_NotPublished()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-enable-{Guid.NewGuid():N}@example.com", "OAuth Enable Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync(
            $"http-srv-enable-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth,
            authHeaderName: null, secretHint: null);
        Assert.Equal(McpServerStatus.NeedsReauth, server.Status);

        // The owner (or a stray script) calls /enable directly on a never-consented oauth server —
        // must NOT bypass consent (spec's NeedsReauth state machine, "enable never manufactures a
        // Published oauth server").
        var changed = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value)
            .EnableAsync(seeded.UserId);

        var afterwards = await fixture.ClusterClient.GetGrain<IMcpServerGrain>(server.Id.Value).GetAsync();
        Assert.False(changed); // already at its correct effective (NeedsReauth) state
        Assert.Equal(McpServerStatus.NeedsReauth, afterwards.Status);
    }
}
