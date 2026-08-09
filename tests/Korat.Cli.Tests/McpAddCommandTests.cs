using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="McpAddCommand.ShellSplit"/> and
/// <see cref="McpAddCommand.TokenizeArgs"/> edge cases.
/// </summary>
public class McpAddCommandTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // ShellSplit edge cases
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShellSplit_simple_command_no_args()
    {
        var (cmd, args) = McpAddCommand.ShellSplit("dotnet");
        Assert.Equal("dotnet", cmd);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void ShellSplit_command_with_args()
    {
        var (cmd, args) = McpAddCommand.ShellSplit("dotnet run --project foo");
        Assert.Equal("dotnet", cmd);
        Assert.Equal("run --project foo", args);
    }

    [Fact]
    public void ShellSplit_quoted_command_with_spaces()
    {
        var (cmd, args) = McpAddCommand.ShellSplit("\"my prog.exe\" --flag value");
        Assert.Equal("my prog.exe", cmd);
        Assert.Equal("--flag value", args);
    }

    [Fact]
    public void ShellSplit_unterminated_quote_returns_trimmed_command()
    {
        // Unterminated opening quote — treat the rest as the command, no args.
        var (cmd, args) = McpAddCommand.ShellSplit("\"unclosed");
        Assert.Equal("unclosed", cmd);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void ShellSplit_leading_trailing_spaces_are_trimmed()
    {
        // The whole command line is Trim()'d first, so trailing spaces are removed.
        // Internal extra spaces between command and first arg are consumed by TrimStart()
        // on the arg portion.
        var (cmd, args) = McpAddCommand.ShellSplit("  dotnet   run  ");
        Assert.Equal("dotnet", cmd);
        Assert.Equal("run", args);
    }

    [Fact]
    public void ShellSplit_empty_string_returns_empty_tuple()
    {
        var (cmd, args) = McpAddCommand.ShellSplit(string.Empty);
        Assert.Equal(string.Empty, cmd);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void ShellSplit_whitespace_only_returns_empty_tuple()
    {
        var (cmd, args) = McpAddCommand.ShellSplit("   ");
        Assert.Equal(string.Empty, cmd);
        Assert.Equal(string.Empty, args);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TokenizeArgs edge cases
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TokenizeArgs_empty_returns_empty_array()
    {
        Assert.Empty(McpAddCommand.TokenizeArgs(string.Empty));
    }

    [Fact]
    public void TokenizeArgs_whitespace_only_returns_empty_array()
    {
        Assert.Empty(McpAddCommand.TokenizeArgs("   "));
    }

    [Fact]
    public void TokenizeArgs_simple_args_split_on_spaces()
    {
        var tokens = McpAddCommand.TokenizeArgs("run --project foo");
        Assert.Equal(new[] { "run", "--project", "foo" }, tokens);
    }

    [Fact]
    public void TokenizeArgs_quoted_segment_with_spaces_is_single_token()
    {
        // --name "my server" → ["--name", "my server"]
        var tokens = McpAddCommand.TokenizeArgs("--name \"my server\"");
        Assert.Equal(new[] { "--name", "my server" }, tokens);
    }

    [Fact]
    public void TokenizeArgs_unterminated_quote_treats_rest_as_one_token()
    {
        // "\"unterminated" → ["unterminated"]  (quotes stripped, rest is one token)
        var tokens = McpAddCommand.TokenizeArgs("\"unterminated");
        Assert.Equal(new[] { "unterminated" }, tokens);
    }

    [Fact]
    public void TokenizeArgs_leading_and_trailing_spaces_ignored()
    {
        var tokens = McpAddCommand.TokenizeArgs("  a  b  ");
        Assert.Equal(new[] { "a", "b" }, tokens);
    }

    [Fact]
    public void TokenizeArgs_multiple_quoted_segments()
    {
        // --a "b c" --d "e f" → ["--a", "b c", "--d", "e f"]
        var tokens = McpAddCommand.TokenizeArgs("--a \"b c\" --d \"e f\"");
        Assert.Equal(new[] { "--a", "b c", "--d", "e f" }, tokens);
    }

    [Fact]
    public void TokenizeArgs_single_unquoted_arg()
    {
        var tokens = McpAddCommand.TokenizeArgs("--verbose");
        Assert.Equal(new[] { "--verbose" }, tokens);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #105/#96: TryValidateName
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("my-server")]
    [InlineData("my_server")]
    [InlineData("My Server 1")]
    [InlineData("abc123")]
    public void TryValidateName_accepts_valid_names(string name)
    {
        Assert.True(McpAddCommand.TryValidateName(name, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("bad/slash")]
    [InlineData("bad:colon")]
    [InlineData("emoji😀")]
    public void TryValidateName_rejects_invalid_names(string name)
    {
        Assert.False(McpAddCommand.TryValidateName(name, out var error));
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void TryValidateName_rejects_names_over_64_chars()
    {
        var tooLong = new string('a', 65);
        Assert.False(McpAddCommand.TryValidateName(tooLong, out var error));
        Assert.Contains("64", error);
    }
}
