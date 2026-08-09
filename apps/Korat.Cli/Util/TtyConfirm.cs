using System.Text;

namespace Korat.Cli.Util;

/// <summary>
/// #165 (`korat nodes prune`): reads a y/N confirmation from the controlling terminal, mirroring
/// <c>UpgradeCommand.RunAsync</c>'s confirm idiom (/dev/tty on macOS/Linux so the prompt still
/// works even when stdin is piped; Console.ReadLine on Windows, which has no /dev/tty). Extracted
/// as a static entry point (rather than inlined per-command like UpgradeCommand) so it can be
/// passed around as an injectable delegate — callers substitute a canned answer in tests instead
/// of touching a real terminal.
/// </summary>
internal static class TtyConfirm
{
    /// <summary>
    /// Writes <paramref name="prompt"/> to stderr, then reads a line from the terminal and
    /// returns true only for "y"/"yes" (case-insensitive). Returns false — with a diagnostic on
    /// stderr — when no terminal is available (redirected stdin on Windows, unopenable /dev/tty
    /// elsewhere), same guard UpgradeCommand uses.
    /// </summary>
    public static Task<bool> AskAsync(string prompt)
    {
        Console.Error.Write(prompt);

        string? answer;
        if (OperatingSystem.IsWindows())
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("stdin is redirected. Use --yes to skip confirmation.");
                return Task.FromResult(false);
            }
            answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        }
        else
        {
            try
            {
                using var tty = new StreamReader(
                    new FileStream("/dev/tty", FileMode.Open, FileAccess.Read),
                    Encoding.UTF8, leaveOpen: false);
                answer = tty.ReadLine()?.Trim().ToLowerInvariant();
            }
            catch
            {
                // /dev/tty unavailable (e.g. piped execution without a terminal)
                Console.Error.WriteLine("Cannot open /dev/tty for confirmation. Use --yes to skip.");
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(answer == "y" || answer == "yes");
    }
}
