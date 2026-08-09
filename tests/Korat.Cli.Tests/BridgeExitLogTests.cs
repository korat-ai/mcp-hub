using System.Text;
using System.Text.RegularExpressions;
using Korat.Cli.Util;

namespace Korat.Cli.Tests;

/// <summary>
/// Tests for <see cref="BridgeExitLog"/>: appends timestamped start/exit lines to
/// <c>~/.korat/logs/connect-&lt;agent&gt;.log</c> (or an injected <c>logDirOverride</c> in
/// tests, so the real <c>~/.korat</c> is never touched — same temp-dir idiom as
/// <see cref="DoctorCommandTests"/>).
/// </summary>
public class BridgeExitLogTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "korat-bridgelog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Append_CreatesFile_WithExpectedLineFormat()
    {
        var dir = NewTempDir();
        try
        {
            BridgeExitLog.Append("cursor", "started pid=123 cloud=https://my.korat.dev target=github", dir);

            var path = Path.Combine(dir, "connect-cursor.log");
            Assert.True(File.Exists(path));

            var line = File.ReadAllLines(path).Single();
            // "<ISO8601-utc> [<pid>] <message>"
            var match = Regex.Match(
                line,
                @"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z) \[(?<pid>\d+)\] (?<msg>.+)$");
            Assert.True(match.Success, $"line did not match expected format: {line}");
            Assert.True(DateTime.TryParse(match.Groups["ts"].Value, out _));
            Assert.Equal(Environment.ProcessId.ToString(), match.Groups["pid"].Value);
            Assert.Equal("started pid=123 cloud=https://my.korat.dev target=github", match.Groups["msg"].Value);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Append_Twice_AppendsBothLines()
    {
        var dir = NewTempDir();
        try
        {
            BridgeExitLog.Append("cursor", "first line", dir);
            BridgeExitLog.Append("cursor", "second line", dir);

            var lines = File.ReadAllLines(Path.Combine(dir, "connect-cursor.log"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("first line", lines[0]);
            Assert.Contains("second line", lines[1]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("Cursor", "connect-cursor.log")]
    [InlineData("Claude Code!", "connect-claude-code-.log")]
    [InlineData("my_agent-01", "connect-my_agent-01.log")]
    [InlineData("", "connect-unnamed.log")]
    [InlineData("   ", "connect-unnamed.log")]
    public void Append_SanitizesAgentNameForFilename(string agentName, string expectedFileName)
    {
        var dir = NewTempDir();
        try
        {
            BridgeExitLog.Append(agentName, "hello", dir);
            Assert.True(File.Exists(Path.Combine(dir, expectedFileName)),
                $"expected {expectedFileName} to exist in {dir}; found: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Append_WhenFileExceeds1MB_TruncatesToLastHalfBeforeAppending()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "connect-cursor.log");
            // Seed >1MB of clearly-tagged FIRST/SECOND lines so we can assert which half survives.
            var sb = new StringBuilder();
            var padding = new string('x', 200);
            var lineCount = 0;
            while (sb.Length < 1024 * 1024 + 10_000)
            {
                var tag = lineCount < 3000 ? "FIRST" : "SECOND";
                sb.Append($"2020-01-01T00:00:00.0000000Z [1] {tag}-{lineCount}-{padding}").Append('\n');
                lineCount++;
            }
            File.WriteAllText(path, sb.ToString());
            Assert.True(new FileInfo(path).Length > 1024 * 1024);

            BridgeExitLog.Append("cursor", "new-line-after-truncate", dir);

            var contents = File.ReadAllText(path);
            Assert.DoesNotContain("FIRST-0-", contents);
            Assert.Contains("SECOND-", contents);
            Assert.Contains("new-line-after-truncate", contents);
            // Truncated file should be meaningfully smaller than double the cap.
            Assert.True(new FileInfo(path).Length < 1024 * 1024 * 2);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Append_WhenLogDirIsUnwritable_SwallowsErrorAndDoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            // Point logDirOverride at a path that already exists as a FILE (not a directory) —
            // Directory.CreateDirectory throws IOException on this, exercising the swallow path.
            var blockingFile = Path.Combine(dir, "not-a-directory");
            File.WriteAllText(blockingFile, "blocker");

            var exception = Record.Exception(() =>
                BridgeExitLog.Append("cursor", "should not throw", blockingFile));

            Assert.Null(exception);
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
