using Korat.Cli.Config;

namespace Korat.Cloud.ContractTests;

[Collection("EnvironmentVariables")]
public sealed class KoratConfigPathsTests
{
    [Fact]
    public void GetWritePath_HonorsKoratConfigEnvVar()
    {
        var path = Path.Combine(Path.GetTempPath(), $"korat-write-{Guid.NewGuid():N}.json");
        WithEnv(config: path, action: () => Assert.Equal(path, KoratConfigPaths.GetWritePath()));
    }

    [Fact]
    public void FindExistingConfigPath_PrefersExplicitOverPlatform()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), $"korat-explicit-{Guid.NewGuid():N}.json");
        var platformDir = Path.Combine(Path.GetTempPath(), $"korat-platform-{Guid.NewGuid():N}", "korat");
        var platformPath = Path.Combine(platformDir, KoratConfigPaths.ConfigFileName);

        Directory.CreateDirectory(platformDir);
        File.WriteAllText(explicitPath, "{}");
        File.WriteAllText(platformPath, "{}");

        try
        {
            WithEnv(config: explicitPath, xdgConfigHome: Path.GetDirectoryName(platformDir), action: () =>
            {
                Assert.Equal(explicitPath, KoratConfigPaths.FindExistingConfigPath());
            });
        }
        finally
        {
            File.Delete(explicitPath);
            File.Delete(platformPath);
            Directory.Delete(Path.GetDirectoryName(platformDir)!, recursive: true);
        }
    }

    [Fact]
    public void FindExistingConfigPath_FallsBackToLegacyDotDir()
    {
        // Platform path on macOS/Windows uses OS-specific app data dirs that
        // cannot be isolated via HOME; this scenario is covered on Linux.
        if (!OperatingSystem.IsLinux())
            return;

        var home = Path.Combine(Path.GetTempPath(), $"korat-home-{Guid.NewGuid():N}");
        var legacyDir = Path.Combine(home, ".korat");
        Directory.CreateDirectory(legacyDir);
        var legacyPath = Path.Combine(legacyDir, KoratConfigPaths.ConfigFileName);
        File.WriteAllText(legacyPath, "{}");

        try
        {
            WithEnv(home: home, action: () =>
            {
                if (OperatingSystem.IsLinux())
                {
                    var emptyXdg = Path.Combine(home, "xdg");
                    Directory.CreateDirectory(emptyXdg);
                    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", emptyXdg);
                }

                Assert.Equal(legacyPath, KoratConfigPaths.FindExistingConfigPath());
            });
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void GetPlatformConfigDirectory_OnLinuxUsesXdgConfigHome()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var xdgRoot = Path.Combine(Path.GetTempPath(), $"xdg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(xdgRoot);

        try
        {
            WithEnv(xdgConfigHome: xdgRoot, action: () =>
            {
                Assert.Equal(
                    Path.Combine(xdgRoot, KoratConfigPaths.AppFolderName),
                    KoratConfigPaths.GetPlatformConfigDirectory());
            });
        }
        finally
        {
            Directory.Delete(xdgRoot);
        }
    }

    private static void WithEnv(string? config = null, string? xdgConfigHome = null, string? home = null, Action? action = null)
    {
        var priorConfig = Environment.GetEnvironmentVariable(KoratConfigPaths.ConfigEnvVar);
        var priorXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var priorHome = Environment.GetEnvironmentVariable("HOME");
        var priorUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        try
        {
            Environment.SetEnvironmentVariable(KoratConfigPaths.ConfigEnvVar, config);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdgConfigHome);
            if (home is not null)
            {
                Environment.SetEnvironmentVariable("HOME", home);
                Environment.SetEnvironmentVariable("USERPROFILE", home);
            }

            action?.Invoke();
        }
        finally
        {
            Environment.SetEnvironmentVariable(KoratConfigPaths.ConfigEnvVar, priorConfig);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", priorXdg);
            Environment.SetEnvironmentVariable("HOME", priorHome);
            Environment.SetEnvironmentVariable("USERPROFILE", priorUserProfile);
        }
    }
}
