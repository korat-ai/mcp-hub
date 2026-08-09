using System.Diagnostics;

namespace Korat.Cli.Util;

/// <summary>
/// Thin wrapper around <see cref="Process"/> for running OS commands and capturing output.
/// </summary>
internal static class ShellHelper
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and returns
    /// (exit code, stdout, stderr). Never throws on non-zero exit.
    /// </summary>
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
