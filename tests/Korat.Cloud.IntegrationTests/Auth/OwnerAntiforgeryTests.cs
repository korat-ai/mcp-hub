using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests.Auth;

public sealed class OwnerAntiforgeryTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CookieMutation_WithoutXsrf_IsRejectedBeforeChangingState_EvenWithBearer(bool withBearer)
    {
        var user = await fixture.SeedUserAsync($"owner-csrf-{Guid.NewGuid():N}@test.local", "Owner");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var server = (await space.PublishMcpServerAsync(NodeId.New(), "Docs", "echo", ""))!;
        using var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(user.UserId);
        if (withBearer)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await fixture.IssueCliTokenAsync(user.UserId));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/mcp-servers/{server.Id.Value}/disable");
        request.Headers.Add("Origin", "https://untrusted.example.test");
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Content = new FormUrlEncodedContent([]);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery-failure", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
        Assert.Equal(McpServerStatus.Published, (await space.GetMcpServerAsync(server.Id))!.Status);
    }

    [Theory]
    [InlineData("POST", "/api/access-requests/missing/approve", null)]
    [InlineData("POST", "/api/grants/missing/revoke", null)]
    [InlineData("DELETE", "/api/mcp-servers/missing", null)]
    [InlineData("PATCH", "/api/mcp-servers/missing", "{\"displayName\":\"changed\"}")]
    public async Task OtherOwnerMutations_RequireXsrfBeforeHandler(string method, string path, string? json)
    {
        var user = await fixture.SeedUserAsync($"owner-method-{Guid.NewGuid():N}@test.local", "Owner");
        using var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(user.UserId);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery-failure", (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegitimateOwnerMutation_WithXsrfOrHeadlessBearer_Works(bool bearer)
    {
        var user = await fixture.SeedUserAsync($"owner-legit-{Guid.NewGuid():N}@test.local", "Owner");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(user.SpaceId);
        var server = (await space.PublishMcpServerAsync(NodeId.New(), "Docs", "echo", ""))!;
        using var client = bearer ? fixture.Factory.CreateClient() : await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(user.UserId);
        if (bearer) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await fixture.IssueCliTokenAsync(user.UserId));

        using var response = await client.PostAsync($"/api/mcp-servers/{server.Id.Value}/disable", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(McpServerStatus.Disabled, (await space.GetMcpServerAsync(server.Id))!.Status);
    }

    [Fact]
    public async Task CookieRead_DoesNotRequireXsrf()
    {
        var user = await fixture.SeedUserAsync($"owner-read-{Guid.NewGuid():N}@test.local", "Owner");
        using var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(user.UserId);
        using var response = await client.GetAsync("/api/space");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record ErrorResponse(string Error);
}
