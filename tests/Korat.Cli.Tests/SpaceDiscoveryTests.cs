using Korat.Cli.Mcp.Aggregation;
using Xunit;

public class SpaceDiscoveryTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes;
        public StubHandler(Dictionary<string, string> routes) => _routes = routes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = _routes.TryGetValue(path, out var b) ? b : "[]";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Classifies_granted_and_ungranted_published_servers()
    {
        var space = """
        { "mcpServers": [
          { "id": {"value":"s1"}, "displayName":"GitHub", "status":"Published", "isAsserted":true },
          { "id": {"value":"s2"}, "displayName":"Postgres", "status":"Published", "isAsserted":true },
          { "id": {"value":"s3"}, "displayName":"Old", "status":"Disabled", "isAsserted":true } ] }
        """;
        // GROUNDED /api/grants shape: plain-string ids, not {value:...}
        var grants = """
        [ { "id":"g1", "status":"Active", "agentClientId":"ag1", "mcpServerId":"s1" } ]
        """;
        var http = new HttpClient(new StubHandler(new() { ["/api/space"]=space, ["/api/grants"]=grants }))
        { BaseAddress = new Uri("http://x/") };

        var result = await SpaceDiscovery.DiscoverAsync(http, agentClientId: "ag1", default);

        Assert.Contains(result.Granted, s => s.Id == "s1" && s.DisplayName == "GitHub");
        Assert.Contains(result.Ungranted, s => s.Id == "s2" && s.DisplayName == "Postgres");
        Assert.DoesNotContain(result.Granted, s => s.Id == "s3");      // Disabled excluded
        Assert.DoesNotContain(result.Ungranted, s => s.Id == "s3");
    }

    [Fact]
    public async Task Grant_for_a_different_agent_does_not_count_as_granted()
    {
        var space = """
        { "mcpServers": [ { "id": {"value":"s1"}, "displayName":"GitHub", "status":"Published", "isAsserted":true } ] }
        """;
        var grants = """
        [ { "id":"g1", "status":"Active", "agentClientId":"OTHER", "mcpServerId":"s1" } ]
        """;
        var http = new HttpClient(new StubHandler(new() { ["/api/space"]=space, ["/api/grants"]=grants }))
        { BaseAddress = new Uri("http://x/") };

        var result = await SpaceDiscovery.DiscoverAsync(http, agentClientId: "ag1", default);

        Assert.Contains(result.Ungranted, s => s.Id == "s1");
        Assert.DoesNotContain(result.Granted, s => s.Id == "s1");
    }

    [Fact]
    public async Task Revoked_grant_does_not_count_as_granted()
    {
        var space = """
        { "mcpServers": [ { "id": {"value":"s1"}, "displayName":"GitHub", "status":"Published", "isAsserted":true } ] }
        """;
        var grants = """
        [ { "id":"g1", "status":"Revoked", "agentClientId":"ag1", "mcpServerId":"s1" } ]
        """;
        var http = new HttpClient(new StubHandler(new() { ["/api/space"]=space, ["/api/grants"]=grants }))
        { BaseAddress = new Uri("http://x/") };

        var result = await SpaceDiscovery.DiscoverAsync(http, agentClientId: "ag1", default);

        Assert.Contains(result.Ungranted, s => s.Id == "s1");
        Assert.DoesNotContain(result.Granted, s => s.Id == "s1");
    }
}
