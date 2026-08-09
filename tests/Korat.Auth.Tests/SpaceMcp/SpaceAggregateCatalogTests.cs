using Korat.Cloud.Mcp.Space;
using Korat.Mcp;

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Space-MCP (increment 1, Task 5): unit tests for <see cref="AggregateCatalog"/> — the
/// per-session catalog merging granted (namespaced) tools with ungranted "request-access" stubs.
/// Pure in-memory, no Orleans/gRPC — mirrors the CLI's own AggregateCatalogTests intent, ported
/// to the cloud namespace and shape (no submit_feedback/update_korat — B2/Task 4 dropped those).
/// </summary>
public class SpaceAggregateCatalogTests
{
    private static ToolInfo Tool(string slug, string originalName, string? description = "a tool") =>
        new(ToolNamespacer.Namespaced(slug, originalName), originalName, slug, Schema: null, description);

    [Fact]
    public void SetGranted_ToolsAppearNamespaced()
    {
        var catalog = new AggregateCatalog();

        catalog.SetGranted("srv-1", "myserver", "My Server", [Tool("myserver", "echo")]);

        var json = catalog.ToolsListJson();
        Assert.Contains("\"myserver__echo\"", json);
    }

    [Fact]
    public void SetUngranted_CollidingDisplayNames_GetDistinctRequestAccessNames()
    {
        var catalog = new AggregateCatalog();

        // Two servers whose display names collapse to the SAME base slug ("iphone") — must not
        // silently clobber one another's request-access__ entry.
        catalog.SetUngranted(
        [
            new ServerDescriptor("srv-aaa11111", "iPhone"),
            new ServerDescriptor("srv-bbb22222", "iPhone"),
        ]);

        var json = catalog.ToolsListJson();
        Assert.Contains("request-access__iphone", json);
        // The second entry must be disambiguated (UniqueSlug appends a short server-id suffix),
        // not dropped — both request-access tools must be present and distinct.
        var occurrences = json.Split("request-access__").Length - 1;
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void TryResolve_GrantedTool_RoundTrips()
    {
        var catalog = new AggregateCatalog();
        catalog.SetGranted("srv-1", "myserver", "My Server", [Tool("myserver", "echo")]);

        var resolved = catalog.TryResolve("myserver__echo", out var route);

        Assert.True(resolved);
        Assert.NotNull(route);
        Assert.Equal(RouteKind.Tool, route!.Kind);
        Assert.Equal("myserver", route.Slug);
        Assert.Equal("echo", route.OriginalName);
        Assert.Equal("srv-1", route.ServerId);
    }

    [Fact]
    public void TryResolve_RequestAccessStub_RoundTrips()
    {
        var catalog = new AggregateCatalog();
        catalog.SetUngranted([new ServerDescriptor("srv-ungranted", "Ungranted Server")]);
        var expectedName = ToolNamespacer.RequestAccessTool(ToolNamespacer.Slug("Ungranted Server", "srv-ungranted"));

        var resolved = catalog.TryResolve(expectedName, out var route);

        Assert.True(resolved);
        Assert.NotNull(route);
        Assert.Equal(RouteKind.RequestAccess, route!.Kind);
        Assert.Equal("srv-ungranted", route.ServerId);
    }

    [Fact]
    public void TryResolve_UnknownName_ReturnsFalse()
    {
        var catalog = new AggregateCatalog();

        var resolved = catalog.TryResolve("nope__nothing", out var route);

        Assert.False(resolved);
        Assert.Null(route);
    }

    [Fact]
    public void RemoveGranted_EvictsOnlyThatServersTools()
    {
        var catalog = new AggregateCatalog();
        catalog.SetGranted("srv-1", "one", "Server One", [Tool("one", "a")]);
        catalog.SetGranted("srv-2", "two", "Server Two", [Tool("two", "b")]);

        catalog.RemoveGranted("srv-1");

        var json = catalog.ToolsListJson();
        Assert.DoesNotContain("\"one__a\"", json);
        Assert.Contains("\"two__b\"", json);
    }

    [Fact]
    public void HasGrantedServer_TracksAnEmptyToolCatalog()
    {
        var catalog = new AggregateCatalog();

        catalog.SetGranted("srv-empty", "empty", "Empty", []);

        Assert.True(catalog.HasGrantedServer("srv-empty"));
        Assert.True(catalog.RemoveGranted("srv-empty"));
        Assert.False(catalog.HasGrantedServer("srv-empty"));
    }

    [Fact]
    public void RemoveGrantedExcept_PrunesOnlyServersMissingFromAuthoritativeSet()
    {
        var catalog = new AggregateCatalog();
        catalog.SetGranted("srv-1", "one", "Server One", [Tool("one", "a")]);
        catalog.SetGranted("srv-2", "two", "Server Two", [Tool("two", "b")]);

        var changed = catalog.RemoveGrantedExcept(new HashSet<string> { "srv-2" });

        Assert.True(changed);
        Assert.False(catalog.HasGrantedServer("srv-1"));
        Assert.True(catalog.HasGrantedServer("srv-2"));
        Assert.DoesNotContain("\"one__a\"", catalog.ToolsListJson());
        Assert.Contains("\"two__b\"", catalog.ToolsListJson());
    }

    [Fact]
    public void SetUngranted_ReplacesThePreviousSet()
    {
        var catalog = new AggregateCatalog();
        catalog.SetUngranted([new ServerDescriptor("srv-1", "First")]);
        Assert.Contains("request-access__first", catalog.ToolsListJson());

        catalog.SetUngranted([new ServerDescriptor("srv-2", "Second")]);

        var json = catalog.ToolsListJson();
        Assert.DoesNotContain("request-access__first", json);
        Assert.Contains("request-access__second", json);
    }
}
