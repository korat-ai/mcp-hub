using System.Diagnostics;
using System.Threading.Channels;

namespace Korat.Cli.Mcp;

/// <summary>
/// One MCP-server subprocess plus two byte-pumps:
///   stdin  ← <see cref="WriteStdinAsync"/> (frames arriving from the agent)
///   stdout → <see cref="ReadStdoutAsync"/> (bytes to be wrapped in outbound frames)
///
/// Lifetime is tied to a single relay session. <see cref="ShutdownAsync"/> closes stdin
/// to signal end-of-input, waits briefly, then kills the process if it has not exited.
/// </summary>
internal sealed class McpServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<byte[]> _stdoutChannel =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _stdoutPumpTask;
    private volatile bool _disposed;

    /// <summary>
    /// Spawns the process. The constructor returns once the OS has forked and the
    /// stdin/stdout pipes are open — there is no readiness handshake (we don't speak
    /// MCP framing yet).
    /// </summary>
    public McpServerProcess(string launchCommand, string launchArguments)
    {
        if (string.IsNullOrWhiteSpace(launchCommand))
            throw new ArgumentException("launchCommand must not be empty.", nameof(launchCommand));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {
            // On Windows, CreateProcess (UseShellExecute=false) can only launch real PE
            // executables (.exe). Common MCP launchers like 'npx' and 'npm' are installed
            // as .cmd shims on Windows (npx.cmd, npm.cmd) which CreateProcess cannot run
            // directly. Wrap them via cmd.exe /c to let the command processor resolve the
            // shim and handle quoting — while keeping UseShellExecute=false so stdin/stdout
            // redirection (required by the bridge) still works.
            //
            // Only wrap when the command looks like a bare name (no path separator, no .exe
            // extension). Absolute paths to .exe files are launched directly.
            bool needsCmdWrapper = NeedsWindowsCmdWrapper(launchCommand);

            string fileName;
            string arguments;
            if (needsCmdWrapper)
            {
                fileName = "cmd.exe";
                // /c tells cmd.exe to run the command and then exit.
                // Wrap launchCommand and launchArguments in double-quotes so cmd.exe
                // treats the whole thing as a single command to expand. Inner quotes in
                // launchArguments are passed through as-is; this covers the common case
                // of `npx <server>` and `uvx <server> --flag`.
                arguments = BuildWindowsCmdArguments(launchCommand, launchArguments);
            }
            else
            {
                fileName = launchCommand;
                arguments = launchArguments ?? string.Empty;
            }

            psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = home,
            };

            // Augment PATH with the npm global-bin directory (%APPDATA%\npm) and similar
            // per-user tool directories so npx/uvx/node resolve correctly even when
            // korat service run was started by a Scheduled Task whose PATH may not include
            // all per-user tool locations.
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var extraDirs = new[]
            {
                Path.Combine(appData, "npm"),           // npm global bin on Windows
                Path.Combine(home, ".local", "bin"),    // uv / uvx install location
            };

            var pathParts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = extraDirs.Where(d => !pathParts.Contains(d));
            var augmented = string.Join(';', toAdd.Concat(pathParts));
            psi.Environment["PATH"] = augmented;
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = launchCommand,
                Arguments = launchArguments ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // Defense-in-depth: if the daemon was started with a restricted PATH (launchd/systemd
            // minimal environment) ensure common tool directories are present so commands like
            // npx/uvx/node/python resolve without relying solely on the baked-in unit PATH.
            // WorkingDirectory defaults to the user's home so relative paths in MCP server
            // configs resolve sensibly instead of landing in '/'.

            // Set a safe working directory — avoid leaving it as '/' which causes
            // "No such file or directory" when the server tries to resolve relative paths.
            if (string.IsNullOrEmpty(psi.WorkingDirectory) || psi.WorkingDirectory == "/")
                psi.WorkingDirectory = home;

            // Augment PATH with directories that tools like npx/uvx/node typically live in
            // under homebrew, nvm, and ~/.local/bin installs, without clobbering any PATH
            // already baked into the unit by 'korat service install'.
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var extraDirs = new[]
            {
                Path.Combine(home, ".local", "bin"),
                "/opt/homebrew/bin",
                "/opt/homebrew/sbin",
                "/usr/local/bin",
            };

            var pathParts = currentPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            var toAdd = extraDirs.Where(d => !pathParts.Contains(d));
            var augmented = string.Join(':', toAdd.Concat(pathParts));

            psi.Environment["PATH"] = augmented;
        }

        _process = new Process { StartInfo = psi };
        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start subprocess: {launchCommand} {launchArguments}");

        _stdoutPumpTask = Task.Run(StdoutPumpAsync);

        // Surface stderr to the host's stderr so the operator can see what their MCP
        // server is logging — but never the payload (stdout). MCP servers commonly
        // emit diagnostics on stderr.
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = _process.StandardError;
                string? line;
                while ((line = await stderr.ReadLineAsync()) is not null)
                {
                    Console.Error.WriteLine($"[mcp] {line}");
                }
            }
            catch { /* best-effort */ }
        });
    }

    /// <summary>
    /// Returns true when the given command name should be launched via <c>cmd.exe /c</c>
    /// on Windows. This is the case for bare command names (no directory separator, no
    /// <c>.exe</c> extension) such as <c>npx</c>, <c>npm</c>, <c>uvx</c>, <c>uv</c>,
    /// <c>node</c>, and <c>python</c> — all of which are commonly installed on Windows as
    /// <c>.cmd</c> shims that <c>CreateProcess</c> (UseShellExecute=false) cannot resolve
    /// directly.
    ///
    /// Pure string logic — no OS calls. Safe to call cross-platform (e.g. from tests).
    /// </summary>
    internal static bool NeedsWindowsCmdWrapper(string launchCommand)
    {
        if (string.IsNullOrWhiteSpace(launchCommand))
            return false;

        // If the command contains a directory separator it is an absolute/relative path.
        // Assume the caller knows what they're doing (e.g. "C:\tools\server.exe").
        // Use hardcoded separator chars so this pure helper is cross-platform testable.
        if (launchCommand.Contains('\\') || launchCommand.Contains('/'))
            return false;

        // Explicit .exe extension — CreateProcess can launch it directly.
        if (launchCommand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Bare name without extension — likely a .cmd shim (npx, npm, uvx, etc.).
        return true;
    }

    /// <summary>
    /// Builds the <c>cmd.exe</c> argument string for wrapping a bare command in
    /// <c>/c "inner"</c> form. Pure string logic — no OS calls.
    /// </summary>
    internal static string BuildWindowsCmdArguments(string launchCommand, string launchArguments)
    {
        var inner = string.IsNullOrWhiteSpace(launchArguments)
            ? launchCommand
            : $"{launchCommand} {launchArguments}";
        return $"/c \"{inner}\"";
    }

    /// <summary>Channel of stdout byte chunks. Reader-side iterated by the bridge.</summary>
    public ChannelReader<byte[]> StdoutChunks => _stdoutChannel.Reader;

    /// <summary>Writes <paramref name="bytes"/> to the subprocess's stdin and flushes.</summary>
    public async Task WriteStdinAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_disposed || _process.HasExited)
            throw new InvalidOperationException("MCP server subprocess has exited.");

        var stdin = _process.StandardInput.BaseStream;
        await stdin.WriteAsync(bytes, cancellationToken);
        await stdin.FlushAsync(cancellationToken);
    }

    private async Task StdoutPumpAsync()
    {
        var ct = _pumpCts.Token;
        var stdout = _process.StandardOutput.BaseStream;
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, ct);
                if (read <= 0)
                    break;

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await _stdoutChannel.Writer.WriteAsync(chunk, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _stdoutChannel.Writer.TryComplete(ex);
            return;
        }

        _stdoutChannel.Writer.TryComplete();
    }

    /// <summary>
    /// Closes stdin to signal "no more input", waits up to <paramref name="gracePeriod"/>
    /// for the subprocess to exit, then kills it.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan? gracePeriod = null)
    {
        if (_disposed) return;
        await CoreShutdownAsync(gracePeriod);
    }

    /// <summary>
    /// The actual teardown body — close stdin, wait briefly, kill on timeout, then
    /// cancel the pump and complete the channel. Has NO <see cref="_disposed"/> guard so
    /// it runs on the dispose path too (where <see cref="_disposed"/> has already been set).
    /// </summary>
    private async Task CoreShutdownAsync(TimeSpan? gracePeriod)
    {
        try { _process.StandardInput.Close(); } catch { /* ignore */ }

        var deadline = gracePeriod ?? TimeSpan.FromSeconds(3);
        try
        {
            using var cts = new CancellationTokenSource(deadline);
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        _pumpCts.Cancel();
        _stdoutChannel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Run the teardown body directly (CoreShutdownAsync has no _disposed guard) so
        // disposing without an explicit ShutdownAsync still closes stdin, waits, and kills
        // the subprocess tree instead of orphaning it.
        await CoreShutdownAsync(null);
        try { await _stdoutPumpTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }

        _process.Dispose();
        _pumpCts.Dispose();
    }
}
