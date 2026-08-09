using System.Diagnostics;
using System.Text;
using Korat.Cli.Mcp;

namespace Korat.Cli.Tests;

/// <summary>
/// Exercises the real subprocess lifecycle of <see cref="McpServerProcess"/>: spawn,
/// stdin → stdout round-trip through the byte pumps, kill-on-dispose, unexpected child
/// exit, and spawn-failure handling. The child is the built Korat.Demo.EchoMcp assembly
/// (a line echo: stdin line "foo" → stdout "echoed: foo"), launched via the host's
/// `dotnet` so it is portable across Windows and POSIX.
/// </summary>
public class McpServerProcessTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Korat.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Korat.slnx not found above test bin dir.");
    }

    /// <summary>
    /// Locates the built EchoMcp dll. The test project has a build-order ProjectReference
    /// to it, so a matching configuration/TFM output exists by the time the test runs.
    /// </summary>
    private static string EchoMcpDllPath()
    {
        var binRoot = Path.Combine(FindRepoRoot(), "apps", "Korat.Demo.EchoMcp", "bin");
        Assert.True(Directory.Exists(binRoot), $"EchoMcp bin dir not found: {binRoot}");
        var dll = Directory
            .EnumerateFiles(binRoot, "Korat.Demo.EchoMcp.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.NotNull(dll);
        return dll!;
    }

    private static McpServerProcess SpawnEcho()
    {
        // FileName "dotnet" + arguments "<dll>" — the OS `dotnet` muxer launches the
        // managed echo. Quote the path in case the repo lives under a directory with spaces.
        var dll = EchoMcpDllPath();
        return new McpServerProcess("dotnet", $"\"{dll}\"");
    }

    /// <summary>Reads stdout chunks until <paramref name="terminator"/> is seen or the timeout elapses.</summary>
    private static async Task<string> ReadUntilAsync(McpServerProcess proc, string terminator, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await proc.StdoutChunks.WaitToReadAsync(cts.Token))
            {
                while (proc.StdoutChunks.TryRead(out var chunk))
                {
                    sb.Append(Encoding.UTF8.GetString(chunk));
                    if (sb.ToString().Contains(terminator, StringComparison.Ordinal))
                        return sb.ToString();
                }
            }
        }
        catch (OperationCanceledException) { /* fall through; assertion on caller side */ }
        return sb.ToString();
    }

    [Fact]
    public async Task RoundTrips_a_line_through_stdin_and_stdout()
    {
        await using var proc = SpawnEcho();

        await proc.WriteStdinAsync(Encoding.UTF8.GetBytes("hello world\n"), default);

        var output = await ReadUntilAsync(proc, "echoed: hello world", TimeSpan.FromSeconds(20));
        Assert.Contains("echoed: hello world", output);
    }

    [Fact]
    public async Task DisposeAsync_kills_the_child_process()
    {
        var proc = SpawnEcho();

        // Grab the underlying PID via reflection. After DisposeAsync calls _process.Dispose()
        // the managed Process object detaches, so we must remember the PID and re-query the OS.
        var processField = typeof(McpServerProcess).GetField(
            "_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var child = (Process)processField.GetValue(proc)!;
        var pid = child.Id;
        Assert.False(child.HasExited);

        await proc.DisposeAsync();

        // The OS process must be gone (DisposeAsync must terminate it, not orphan it).
        // Poll briefly: kill + reap is not perfectly instantaneous.
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try
            {
                using var os = Process.GetProcessById(pid);
                if (os.HasExited) { gone = true; break; }
            }
            catch (ArgumentException)
            {
                // No process with that id — it has been reaped.
                gone = true;
                break;
            }
            await Task.Delay(100);
        }

        Assert.True(gone, "DisposeAsync must terminate the child instead of orphaning it.");
    }

    [Fact]
    public async Task Child_exit_completes_the_stdout_channel()
    {
        await using var proc = SpawnEcho();

        // Closing stdin makes the echo loop see EOF and exit; the pump then completes
        // the stdout channel. WaitToReadAsync returning false signals completion.
        await proc.ShutdownAsync(TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Drain any buffered chunks, then expect completion (false) rather than hanging.
        while (await proc.StdoutChunks.WaitToReadAsync(cts.Token))
            while (proc.StdoutChunks.TryRead(out _)) { }

        // Reaching here (WaitToReadAsync returned false) means the channel completed.
        Assert.True(true);
    }

    [Fact]
    public void Spawning_a_bogus_command_throws_rather_than_hanging()
    {
        Assert.ThrowsAny<Exception>(() =>
            new McpServerProcess("this-command-does-not-exist-korat-test", string.Empty));
    }

    [Fact]
    public void Empty_launch_command_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new McpServerProcess("   ", string.Empty));
    }
}
