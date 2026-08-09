using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for the <c>korat mcp remove</c> config mutation.
/// All tests use a temp directory to isolate from <c>~/.korat</c>.
/// </summary>
public class McpRemoveCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public McpRemoveCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"korat-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private LocalIdentityStore Store() => new(_configPath);

    private LocalIdentity SeedServers(params string[] names)
    {
        var store = Store();
        var identity = store.LoadOrCreate();
        identity.McpServers.Clear();
        foreach (var n in names)
            identity.McpServers.Add(new LocalMcpServer { DisplayName = n, LaunchCommand = "cmd", LaunchArguments = "" });
        store.Save(identity);
        return identity;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Happy-path: server removed from config
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_existing_server_reduces_count()
    {
        SeedServers("alpha", "beta", "gamma");

        // Directly invoke the config-mutation logic via the store.
        var store = Store();
        var identity = store.LoadOrCreate();
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "beta", StringComparison.OrdinalIgnoreCase));
        Assert.True(idx >= 0, "beta should exist before removal");
        identity.McpServers.RemoveAt(idx);
        store.Save(identity);

        var reloaded = Store().LoadOrCreate();
        Assert.Equal(2, reloaded.McpServers.Count);
        Assert.DoesNotContain(reloaded.McpServers, s =>
            string.Equals(s.DisplayName, "beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Remove_is_case_insensitive()
    {
        SeedServers("GitHub", "FileSystem");

        var store = Store();
        var identity = store.LoadOrCreate();
        // Simulate case-insensitive removal as McpRemoveCommand does.
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "github", StringComparison.OrdinalIgnoreCase));
        Assert.True(idx >= 0, "Should find 'GitHub' when searching with 'github'");
        identity.McpServers.RemoveAt(idx);
        store.Save(identity);

        var reloaded = Store().LoadOrCreate();
        Assert.Single(reloaded.McpServers);
        Assert.Equal("FileSystem", reloaded.McpServers[0].DisplayName);
    }

    [Fact]
    public void Remove_last_server_leaves_empty_list()
    {
        SeedServers("only");

        var store = Store();
        var identity = store.LoadOrCreate();
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "only", StringComparison.OrdinalIgnoreCase));
        Assert.True(idx >= 0);
        identity.McpServers.RemoveAt(idx);
        store.Save(identity);

        var reloaded = Store().LoadOrCreate();
        Assert.Empty(reloaded.McpServers);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Not-found: index is -1
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_nonexistent_server_returns_minus_one()
    {
        SeedServers("alpha");

        var identity = Store().LoadOrCreate();
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "nonexistent", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void Remove_from_empty_list_returns_minus_one()
    {
        SeedServers(); // no servers

        var identity = Store().LoadOrCreate();
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "anything", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(-1, idx);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Only the named server is removed; others are untouched
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_only_affects_named_server()
    {
        SeedServers("a", "b", "c");

        var store = Store();
        var identity = store.LoadOrCreate();
        var idx = identity.McpServers.FindIndex(s =>
            string.Equals(s.DisplayName, "b", StringComparison.OrdinalIgnoreCase));
        identity.McpServers.RemoveAt(idx);
        store.Save(identity);

        var reloaded = Store().LoadOrCreate();
        Assert.Equal(2, reloaded.McpServers.Count);
        Assert.Contains(reloaded.McpServers, s => s.DisplayName == "a");
        Assert.Contains(reloaded.McpServers, s => s.DisplayName == "c");
    }
}
