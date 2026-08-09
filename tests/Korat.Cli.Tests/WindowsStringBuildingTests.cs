using Korat.Cli.Mcp;
using Korat.Cli.Service;

namespace Korat.Cli.Tests;

/// <summary>
/// Pure-logic unit tests for the Windows string-building helpers.
/// No OS calls, no process spawning — runs green on macOS/Linux CI.
/// </summary>
public class WindowsStringBuildingTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // McpServerProcess.NeedsWindowsCmdWrapper
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("npx", true)]
    [InlineData("npm", true)]
    [InlineData("uvx", true)]
    [InlineData("uv", true)]
    [InlineData("node", true)]
    [InlineData("python", true)]
    [InlineData("deno", true)]
    public void NeedsWindowsCmdWrapper_bare_names_return_true(string cmd, bool expected)
    {
        Assert.Equal(expected, McpServerProcess.NeedsWindowsCmdWrapper(cmd));
    }

    [Theory]
    [InlineData("foo.exe")]
    [InlineData("korat.exe")]
    [InlineData("FOO.EXE")]           // case-insensitive
    [InlineData("MyServer.Exe")]
    public void NeedsWindowsCmdWrapper_exe_extension_returns_false(string cmd)
    {
        Assert.False(McpServerProcess.NeedsWindowsCmdWrapper(cmd));
    }

    [Theory]
    [InlineData(@"C:\tools\foo.exe")]         // Windows absolute path with backslash
    [InlineData(@"C:\Program Files\my.exe")]  // path with spaces
    [InlineData(@".\local\server")]           // relative path with backslash
    [InlineData("./local/server")]            // relative path with forward slash
    [InlineData("/usr/local/bin/npx")]        // Unix-style absolute path
    public void NeedsWindowsCmdWrapper_paths_with_separators_return_false(string cmd)
    {
        Assert.False(McpServerProcess.NeedsWindowsCmdWrapper(cmd));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NeedsWindowsCmdWrapper_null_or_whitespace_returns_false(string? cmd)
    {
        Assert.False(McpServerProcess.NeedsWindowsCmdWrapper(cmd!));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // McpServerProcess.BuildWindowsCmdArguments
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildWindowsCmdArguments_with_arguments_produces_correct_string()
    {
        var result = McpServerProcess.BuildWindowsCmdArguments("npx", "server --flag");
        Assert.Equal("/c \"npx server --flag\"", result);
    }

    [Fact]
    public void BuildWindowsCmdArguments_empty_arguments_omits_trailing_space()
    {
        var result = McpServerProcess.BuildWindowsCmdArguments("npx", "");
        Assert.Equal("/c \"npx\"", result);
    }

    [Fact]
    public void BuildWindowsCmdArguments_whitespace_arguments_treated_as_empty()
    {
        var result = McpServerProcess.BuildWindowsCmdArguments("uvx", "   ");
        // string.IsNullOrWhiteSpace("   ") is true → inner = just launchCommand
        Assert.Equal("/c \"uvx\"", result);
    }

    [Fact]
    public void BuildWindowsCmdArguments_uvx_with_server_and_flags()
    {
        var result = McpServerProcess.BuildWindowsCmdArguments("uvx", "mcp-server-git --repository .");
        Assert.Equal("/c \"uvx mcp-server-git --repository .\"", result);
    }

    [Fact]
    public void BuildWindowsCmdArguments_starts_with_slash_c_space_quote()
    {
        var result = McpServerProcess.BuildWindowsCmdArguments("npm", "run mcp");
        Assert.StartsWith("/c \"", result);
        Assert.EndsWith("\"", result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SchTasksCommand.BuildCreateArguments
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCreateArguments_plain_path_produces_correct_schtasks_string()
    {
        // No spaces in path — nested quotes still required by schtasks /TR convention.
        var result = SchTasksCommand.BuildCreateArguments(
            "KoratNode",
            @"C:\korat\korat.exe");

        Assert.Equal(
            @"/Create /TN ""KoratNode"" /TR ""\""C:\korat\korat.exe\"" service run"" /SC ONLOGON /RL LIMITED /IT /F",
            result);
    }

    [Fact]
    public void BuildCreateArguments_path_with_spaces_produces_nested_quotes()
    {
        // This is the critical case: "C:\Program Files\korat\korat.exe" must be
        // escaped as  \"C:\Program Files\korat\korat.exe\"  inside the /TR value.
        var result = SchTasksCommand.BuildCreateArguments(
            "KoratNode",
            @"C:\Program Files\korat\korat.exe");

        // The /TR value (between outer quotes) must contain:  \"<exe>\" service run
        Assert.Contains(@"\""C:\Program Files\korat\korat.exe\""", result);
        Assert.Contains("service run", result);
    }

    [Fact]
    public void BuildCreateArguments_task_name_is_interpolated()
    {
        var result = SchTasksCommand.BuildCreateArguments("MyTask", @"C:\korat\korat.exe");

        Assert.Contains("/TN \"MyTask\"", result);
    }

    [Fact]
    public void BuildCreateArguments_contains_required_schtasks_flags()
    {
        var result = SchTasksCommand.BuildCreateArguments(
            "KoratNode",
            @"C:\korat\korat.exe");

        Assert.Contains("/Create", result);
        Assert.Contains("/SC ONLOGON", result);
        Assert.Contains("/RL LIMITED", result);
        Assert.Contains("/IT", result);
        Assert.Contains("/F", result);
    }

    [Fact]
    public void BuildCreateArguments_matches_original_inline_format()
    {
        // Regression: verify the extracted method produces byte-identical output
        // to the original inline interpolation in ScheduledTaskController.InstallAsync.
        const string taskName = "KoratNode";
        const string exePath = @"C:\Users\testuser\AppData\Local\korat\korat.exe";

        // Original inline expression (replicated here as the reference):
        var original = $"/Create /TN \"{taskName}\" /TR \"\\\"{exePath}\\\" service run\" /SC ONLOGON /RL LIMITED /IT /F";
        var extracted = SchTasksCommand.BuildCreateArguments(taskName, exePath);

        Assert.Equal(original, extracted);
    }
}
