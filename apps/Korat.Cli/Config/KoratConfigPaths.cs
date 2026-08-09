using System.Runtime.InteropServices;

namespace Korat.Cli.Config;

/// <summary>
/// Пути к config.json CLI на Windows / macOS / Linux (+ legacy ~/.korat).
/// </summary>
internal static class KoratConfigPaths
{
    public const string AppFolderName = "korat";
    public const string ConfigFileName = "config.json";
    public const string ConfigEnvVar = "KORAT_CONFIG";

    /// <summary>
    /// Ordered candidates when reading an existing config (first match wins).
    /// When <see cref="ConfigEnvVar"/> is set, only that path is considered.
    /// </summary>
    public static IReadOnlyList<string> GetSearchPaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable(ConfigEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return [explicitPath];

        var paths = new List<string> { GetPlatformConfigFilePath() };

        var legacyPath = GetLegacyConfigFilePath();
        if (!paths.Contains(legacyPath, StringComparer.Ordinal))
            paths.Add(legacyPath);

        return paths;
    }

    /// <summary>
    /// Path used when creating or updating config. Honors <see cref="ConfigEnvVar"/> when set.
    /// </summary>
    public static string GetWritePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(ConfigEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        return GetPlatformConfigFilePath();
    }

    public static string? FindExistingConfigPath()
    {
        foreach (var path in GetSearchPaths())
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static string GetPlatformConfigFilePath() =>
        Path.Combine(GetPlatformConfigDirectory(), ConfigFileName);

    public static string GetLegacyConfigFilePath() =>
        Path.Combine(GetLegacyConfigDirectory(), ConfigFileName);

    /// <summary>
    /// Windows/macOS: %APPDATA%/korat (Roaming / Application Support).
    /// Linux and other Unix: $XDG_CONFIG_HOME/korat or ~/.config/korat.
    /// </summary>
    public static string GetPlatformConfigDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName);
        }

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return Path.Combine(xdgConfigHome, AppFolderName);

        return Path.Combine(GetUserHomeDirectory(), ".config", AppFolderName);
    }

    public static string GetLegacyConfigDirectory() =>
        Path.Combine(GetUserHomeDirectory(), ".korat");

    /// <summary>
    /// The ~/.korat directory used for CLI credentials and other per-user state.
    /// On all platforms this is always the legacy ~/.korat path so that CLI credentials
    /// remain in one well-known, easy-to-back-up location.
    /// </summary>
    public static string BaseDir => GetLegacyConfigDirectory();

    /// <summary>
    /// Creates <paramref name="dir"/> and, on non-Windows, restricts it to owner-only
    /// (0700). Best-effort: the chmod is wrapped in try/catch because some filesystems
    /// (SMB / overlayfs) reject it. Use this instead of a bare
    /// <see cref="Directory.CreateDirectory(string)"/> for any directory that holds CLI
    /// credentials or identity state, so the directory is hardened even when it is first
    /// created before <c>korat login</c> runs.
    /// </summary>
    public static void EnsureDirSecure(string dir)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best-effort — filesystem (e.g. SMB share) may not support chmod.
            }
        }
    }

    private static string GetUserHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
            return userProfile;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
