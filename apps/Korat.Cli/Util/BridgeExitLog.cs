using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Korat.Cli.Util;

/// <summary>
/// A3 (node-visibility-doctor): appends timestamped start/exit lines to
/// <c>~/.korat/logs/connect-&lt;agent&gt;.log</c> so a dead or crashed
/// <c>korat connect --bridge</c> session leaves a korat-side trace — independent of
/// whatever the MCP client's own log captured (or didn't).
///
/// Secrets hygiene: callers must never pass a secret value (e.g. the access token) in
/// <paramref name="message"/> — this class does not redact anything, it only appends.
///
/// MUST NEVER throw: logging must never break the bridge it is observing. Every IO
/// error is swallowed.
/// </summary>
internal static class BridgeExitLog
{
    private const long MaxBytesBeforeTruncate = 1024 * 1024; // 1 MB

    /// <summary>
    /// Appends a line formatted as <c>"&lt;ISO8601-utc&gt; [&lt;pid&gt;] &lt;message&gt;"</c>
    /// to <c>&lt;logDir&gt;/connect-&lt;sanitized-agent&gt;.log</c> (default logDir:
    /// <c>~/.korat/logs</c>). <paramref name="agentName"/> is sanitized to
    /// <c>[a-z0-9-_]</c> so it is always a safe filename fragment. When the target file
    /// already exceeds 1 MB, it is truncated to keep only its last half before the new
    /// line is appended. Any IO failure (unwritable directory, permission error, disk
    /// full, ...) is swallowed — this method never throws.
    /// </summary>
    internal static void Append(string agentName, string message, string? logDirOverride = null)
    {
        try
        {
            var logDir = logDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".korat", "logs");
            Directory.CreateDirectory(logDir);

            var path = Path.Combine(logDir, $"connect-{Sanitize(agentName)}.log");
            TruncateIfTooLarge(path);

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0} [{1}] {2}{3}",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Environment.ProcessId,
                message,
                Environment.NewLine);
            File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Logging must never break the bridge — swallow everything (disk full,
            // read-only filesystem, permission denied, path too long, ...).
        }
    }

    /// <summary>Lowercases and replaces any character outside [a-z0-9-_] with '-'.</summary>
    private static string Sanitize(string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return "unnamed";
        var lowered = agentName.Trim().ToLowerInvariant();
        var sanitized = Regex.Replace(lowered, "[^a-z0-9_-]", "-");
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }

    /// <summary>Keeps only the last half of the file's lines when it exceeds 1 MB.</summary>
    private static void TruncateIfTooLarge(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length <= MaxBytesBeforeTruncate) return;

        var lines = File.ReadAllLines(path);
        var tail = lines.Skip(lines.Length / 2).ToArray();
        File.WriteAllLines(path, tail, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
