using System.Text.Json.Nodes;
using Korat.Cli.Mcp.Aggregation;
using Xunit;
using Korat.Mcp;

public class AggregateCatalogTests
{
    [Fact]
    public void Build_lists_granted_tools_namespaced_with_display_name_in_description()
    {
        var cat = new AggregateCatalog();
        cat.SetGranted("s1", "github", "GitHub", new[] {
            new ToolInfo("github__create_issue", "create_issue", "github",
                (JsonObject)JsonNode.Parse("""{"type":"object"}""")!, "Create an issue") });
        cat.SetUngranted(new[] { new ServerDescriptor("s2", "Postgres", true) });

        var list = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray();
        Assert.Contains(list, t => t!["name"]!.GetValue<string>() == "github__create_issue"
            && t!["description"]!.GetValue<string>().StartsWith("[GitHub]"));
        Assert.Contains(list, t => t!["name"]!.GetValue<string>() == "request-access__postgres");

        Assert.True(cat.TryResolve("github__create_issue", out var route) && route!.Kind == RouteKind.Tool);
        Assert.True(cat.TryResolve("request-access__postgres", out var ra) && ra!.Kind == RouteKind.RequestAccess && ra.ServerId == "s2");
    }

    [Fact]
    public void Unknown_name_does_not_resolve()
    {
        var cat = new AggregateCatalog();
        Assert.False(cat.TryResolve("nope__nope", out _));
    }

    [Fact]
    public void Compatibility_tools_are_always_listed_and_resolve_to_control_routes()
    {
        var cat = new AggregateCatalog();
        var tools = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray();

        Assert.Contains(tools,
            tool => tool!["name"]!.GetValue<string>() == AggregateCatalog.ControlListToolsName);
        Assert.Contains(tools,
            tool => tool!["name"]!.GetValue<string>() == AggregateCatalog.ControlCallToolName);
        Assert.Contains(tools,
            tool => tool!["name"]!.GetValue<string>() == AggregateCatalog.ControlRequestAccessName);

        Assert.True(cat.TryResolve(AggregateCatalog.ControlListToolsName, out var listRoute));
        Assert.Equal(RouteKind.ControlListTools, listRoute!.Kind);
        Assert.True(cat.TryResolve(AggregateCatalog.ControlCallToolName, out var callRoute));
        Assert.Equal(RouteKind.ControlCallTool, callRoute!.Kind);
        Assert.True(cat.TryResolve(AggregateCatalog.ControlRequestAccessName, out var accessRoute));
        Assert.Equal(RouteKind.ControlRequestAccess, accessRoute!.Kind);
    }

    [Fact]
    public void Granted_tool_carries_inputSchema_and_original_route_fields()
    {
        var cat = new AggregateCatalog();
        cat.SetGranted("s1", "github", "GitHub", new[] {
            new ToolInfo("github__create_issue", "create_issue", "github",
                (JsonObject)JsonNode.Parse("""{"type":"object","properties":{}}""")!, "Create an issue") });

        var tool = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray()
            .First(t => t!["name"]!.GetValue<string>() == "github__create_issue")!;
        Assert.NotNull(tool["inputSchema"]);

        Assert.True(cat.TryResolve("github__create_issue", out var r));
        Assert.Equal("create_issue", r!.OriginalName);
        Assert.Equal("github", r.Slug);
        Assert.Equal("s1", r.ServerId);
    }

    [Fact]
    public void RemoveGranted_drops_that_servers_tools()
    {
        var cat = new AggregateCatalog();
        cat.SetGranted("s1", "github", "GitHub", new[] {
            new ToolInfo("github__create_issue","create_issue","github",
                (JsonObject)JsonNode.Parse("""{"type":"object"}""")!, "d") });
        cat.RemoveGranted("s1");
        var tools = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray();
        // Only the granted tool should be gone; the control tools stay.
        Assert.DoesNotContain(tools, t => t!["name"]!.GetValue<string>() == "github__create_issue");
        Assert.False(cat.TryResolve("github__create_issue", out _));
    }

    [Fact]
    public void SetUpgradeAvailable_true_injects_update_korat_tool_and_resolves_to_Upgrade()
    {
        var cat = new AggregateCatalog();
        cat.SetUpgradeAvailable(true, "0.3.0", "0.2.8");

        var tools = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray();
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "update_korat");

        Assert.True(cat.TryResolve("update_korat", out var r));
        Assert.Equal(RouteKind.Upgrade, r!.Kind);
    }

    [Fact]
    public void SetUpgradeAvailable_false_removes_update_korat_tool_and_TryResolve_returns_false()
    {
        var cat = new AggregateCatalog();
        cat.SetUpgradeAvailable(true, "0.3.0", "0.2.8");
        cat.SetUpgradeAvailable(false, "0.3.0", "0.2.8");

        var tools = JsonNode.Parse(cat.ToolsListJson())!["tools"]!.AsArray();
        Assert.DoesNotContain(tools, t => t!["name"]!.GetValue<string>() == "update_korat");
        Assert.False(cat.TryResolve("update_korat", out _));
    }

}
