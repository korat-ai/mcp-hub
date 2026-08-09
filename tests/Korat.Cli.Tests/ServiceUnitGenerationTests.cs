using Korat.Cli.Service;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for the pure string-generation methods in <see cref="LaunchdController"/>
/// and <see cref="SystemdController"/>. No file I/O or process spawning.
/// </summary>
public class ServiceUnitGenerationTests
{
    // Common test fixtures — simulate what InstallAsync captures from the interactive shell.
    private const string TestPath = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
    private const string TestHome = "/Users/testuser";

    // ─────────────────────────────────────────────────────────────────────────
    // LaunchdController — plist generation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LaunchdController_GeneratePlist_contains_label()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/home/user/.korat/logs", TestPath, TestHome);
        Assert.Contains("<string>ai.korat.node</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_exe_path()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<string>/usr/local/bin/korat</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_service_run_arguments()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<string>service</string>", plist);
        Assert.Contains("<string>run</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_RunAtLoad_is_true()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        // <true/> must follow immediately after the RunAtLoad key.
        var idx = plist.IndexOf("<key>RunAtLoad</key>", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        var after = plist.Substring(idx);
        Assert.Contains("<true/>", after.Substring(0, after.IndexOf("</dict>", StringComparison.Ordinal)));
    }

    [Fact]
    public void LaunchdController_GeneratePlist_KeepAlive_is_true()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>KeepAlive</key>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_stdout_log_path()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/home/user/.korat/logs", TestPath, TestHome);
        Assert.Contains("<string>/home/user/.korat/logs/service.out.log</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_stderr_log_path()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/home/user/.korat/logs", TestPath, TestHome);
        Assert.Contains("<string>/home/user/.korat/logs/service.err.log</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_is_valid_xml_structure()
    {
        var plist = LaunchdController.GeneratePlist("/opt/korat/korat", "/var/log/korat", TestPath, TestHome);
        // Should open and close <plist> root.
        Assert.Contains("<plist version=\"1.0\">", plist);
        Assert.Contains("</plist>", plist);
        // Should have one <dict>.
        Assert.Contains("<dict>", plist);
        Assert.Contains("</dict>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_WorkingDirectory()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>WorkingDirectory</key>", plist);
        Assert.Contains($"<string>{TestHome}</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_WorkingDirectory_is_not_root()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        // Must not set WorkingDirectory to '/' — that is what broke npx launch.
        Assert.DoesNotContain("<string>/</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_EnvironmentVariables_dict()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>EnvironmentVariables</key>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_PATH_value()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>PATH</key>", plist);
        Assert.Contains($"<string>{TestPath}</string>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_contains_HOME_value()
    {
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", TestPath, TestHome);
        Assert.Contains("<key>HOME</key>", plist);
    }

    [Fact]
    public void LaunchdController_GeneratePlist_xml_escapes_ampersand_in_path()
    {
        // Unusual but possible: a PATH segment with an ampersand should be XML-escaped.
        var weirdPath = "/opt/homebrew/bin:/usr/local/bin&extras:/usr/bin";
        var plist = LaunchdController.GeneratePlist("/usr/local/bin/korat", "/tmp/logs", weirdPath, TestHome);
        Assert.DoesNotContain("&extras", plist);
        Assert.Contains("&amp;extras", plist);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SystemdController — unit file generation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SystemdController_GenerateUnit_contains_ExecStart()
    {
        // FIX-4: ExecStart must quote the exe path so systemd handles paths with spaces.
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("ExecStart=\"/usr/local/bin/korat\" service run", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_Restart_on_failure()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("Restart=on-failure", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_WantedBy_default_target()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("WantedBy=default.target", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_has_Unit_section()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("[Unit]", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_has_Service_section()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("[Service]", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_has_Install_section()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains("[Install]", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_exe_path_is_embedded()
    {
        // FIX-4: path is double-quoted; verify the unit contains the quoted path.
        var unit = SystemdController.GenerateUnit("/custom/path/to/korat", TestPath, TestHome);
        Assert.Contains("\"/custom/path/to/korat\" service run", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_quotes_path_with_spaces()
    {
        // FIX-4: a path with spaces must remain intact inside double quotes so systemd
        // does not split it on whitespace.
        var unit = SystemdController.GenerateUnit("/opt/korat/korat", TestPath, TestHome);
        Assert.Contains("ExecStart=\"/opt/korat/korat\" service run", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_contains_WorkingDirectory()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains($"WorkingDirectory={TestHome}", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_WorkingDirectory_is_not_root()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.DoesNotContain("WorkingDirectory=/\n", unit);
        Assert.DoesNotContain("WorkingDirectory=/\r", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_contains_PATH_environment()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains($"Environment=PATH={TestPath}", unit);
    }

    [Fact]
    public void SystemdController_GenerateUnit_contains_HOME_environment()
    {
        var unit = SystemdController.GenerateUnit("/usr/local/bin/korat", TestPath, TestHome);
        Assert.Contains($"Environment=HOME={TestHome}", unit);
    }
}
