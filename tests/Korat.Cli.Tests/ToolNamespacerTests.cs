using Korat.Cli.Mcp.Aggregation;
using Xunit;
using Korat.Mcp;

public class ToolNamespacerTests
{
    [Fact]
    public void Slug_lowercases_and_replaces_invalid_chars()
    {
        Assert.Equal("my_server", ToolNamespacer.Slug("My Server!", "id1"));
        Assert.Equal("work_mac_fs", ToolNamespacer.Slug("work_mac_fs", "id2"));
    }

    [Fact]
    public void Slug_collapses_underscore_runs_so_it_never_contains_the_separator()
    {
        // A displayName with consecutive non-alphanumeric chars must NOT yield "__" (the
        // namespace separator) inside the slug — otherwise TrySplit (splits on first "__")
        // mis-routes the tool call. Regression for the observed "request-access__iphone__iphone".
        Assert.Equal("iphone_test", ToolNamespacer.Slug("iPhone (test)", "id1"));
        Assert.Equal("a_b", ToolNamespacer.Slug("a   b", "id2"));     // multiple spaces
        Assert.Equal("a_b", ToolNamespacer.Slug("a..b", "id3"));      // multiple dots
        Assert.DoesNotContain("__", ToolNamespacer.Slug("x...y???z", "id4"));

        // Round-trip routing guarantee: namespaced name splits back to the exact slug + tool.
        var slug = ToolNamespacer.Slug("iPhone (test)", "id1");
        Assert.True(ToolNamespacer.TrySplit(ToolNamespacer.Namespaced(slug, "screen_capture"), out var s, out var t));
        Assert.Equal(slug, s);
        Assert.Equal("screen_capture", t);
    }

    [Fact]
    public void Slug_collision_disambiguates_with_id_suffix()
    {
        // Two different ids whose names fold to the same slug get distinct slugs.
        // "My Server" → "my_server"; "my_server" → "my_server" — genuine collision.
        var taken = new HashSet<string>();
        var a = ToolNamespacer.UniqueSlug("My Server", "aaaa1111", taken);
        var b = ToolNamespacer.UniqueSlug("my_server", "bbbb2222", taken);
        Assert.NotEqual(a, b);
        Assert.StartsWith("my_server", a);
    }

    [Fact]
    public void Namespaced_name_combines_slug_and_tool_and_parses_back()
    {
        var n = ToolNamespacer.Namespaced("github", "create_issue");
        Assert.Equal("github__create_issue", n);
        Assert.True(ToolNamespacer.TrySplit("github__create_issue", out var slug, out var tool));
        Assert.Equal("github", slug);
        Assert.Equal("create_issue", tool);
    }

    [Fact]
    public void Namespaced_truncation_boundary_on_underscore_does_not_corrupt_split()
    {
        // Regression: when slug+separator+tool exceeds the 64-char cap, the slug is
        // truncated. If the truncation boundary happens to land right after a '_', the
        // untrimmed slice would end in '_' and appending "__" would yield 3 underscores in a
        // row — TrySplit (splits on the FIRST "__") would then hand back a tool name with a
        // spurious leading '_'. Chosen lengths pin the truncation boundary exactly on the '_'
        // at index 10 of the slug (keep = 64 - 2 - 51 = 11, so slug[..11] == "xxxxxxxxxx_").
        var slug = new string('x', 10) + "_" + new string('y', 50); // 61 chars, '_' at index 10
        var tool = new string('t', 51); // room = 64 - 2 - 51 = 11 = keep

        var namespaced = ToolNamespacer.Namespaced(slug, tool);

        Assert.DoesNotContain("___", namespaced);
        Assert.True(ToolNamespacer.TrySplit(namespaced, out var splitSlug, out var splitTool));
        Assert.Equal(tool, splitTool); // no spurious leading '_'
        Assert.False(splitSlug.EndsWith('_'));
        Assert.Equal(new string('x', 10), splitSlug);
    }

    [Fact]
    public void RequestAccess_tool_name_is_well_formed()
    {
        Assert.Equal("request-access__postgres", ToolNamespacer.RequestAccessTool("postgres"));
    }
}
