using System.Diagnostics;
using System.Runtime.Versioning;
using Korat.Cli.Util;

namespace Korat.Cli.Service;

/// <summary>
/// Windows per-user service controller.
///
/// Primary mechanism: a per-user ONLOGON Scheduled Task (<c>schtasks.exe</c>), which
/// runs in the interactive session with the user's full PATH — the true analog of a
/// macOS LaunchAgent or <c>systemctl --user</c> unit.
///
/// Fallback mechanism: when <c>schtasks /Create</c> fails (e.g. on locked-down or
/// GPO-restricted machines), the controller writes a per-user autostart entry to
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. The Run-key entry
/// launches <c>korat service run</c> at every interactive logon — no admin rights
/// required. Unlike the scheduled task it does NOT restart on crash, and the start
/// is deferred to the next sign-in (unless explicitly triggered in the same session).
///
/// WHY a Scheduled Task over a Windows Service (SCM):
///   SCM services run in session 0 under a service account and get the system PATH,
///   not the interactive-user PATH. That means per-user npx/uvx/node installs (nvm,
///   %APPDATA%\npm, winget user-scope) would not resolve. SCM also requires elevation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScheduledTaskController : IServiceController
{
    // Delegate the string constants to the cross-platform pure-helper classes so
    // tests can access them without triggering CA1416 (class is [SupportedOSPlatform]).
    internal const string TaskName     = SchTasksCommand.TaskName;
    internal const string RunKeyValueName = RunKeyCommand.ValueName;
    internal const string RunKeyPath      = RunKeyCommand.KeyPath;

    // ── Install ───────────────────────────────────────────────────────────────

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        // ── Primary: schtasks /Create ──────────────────────────────────────────
        var createArgs = SchTasksCommand.BuildCreateArguments(TaskName, exePath);
        var (rc, _, err) = await ShellHelper.RunAsync("schtasks.exe", createArgs, ct);

        if (rc == 0)
        {
            Console.WriteLine($"Scheduled task '{TaskName}' registered.");

            // Start immediately so the daemon is live without needing to log out.
            var (rcRun, _, errRun) = await ShellHelper.RunAsync(
                "schtasks.exe", $"/Run /TN \"{TaskName}\"", ct);
            if (rcRun != 0)
                Console.WriteLine($"Note: task is registered but could not start immediately: {errRun.Trim()}");
            else
                Console.WriteLine("Publisher runtime started.");

            return;
        }

        // ── Fallback: HKCU Run key ─────────────────────────────────────────────
        // schtasks failed (rc != 0). Do NOT inspect the localized error string —
        // just attempt the registry fallback unconditionally.
        Console.WriteLine(
            $"Note: schtasks /Create failed (rc={rc}) — falling back to per-user registry autostart.");
        Console.WriteLine(
            "      (Task Scheduler may be restricted by Group Policy on this machine.)");

        WriteRunKeyValue(exePath);
        Console.WriteLine(
            $"Logon autostart registered at HKCU\\{RunKeyPath}\\{RunKeyValueName}.");
        Console.WriteLine(
            "Note: the publisher runtime will start automatically at your NEXT sign-in.");
        Console.WriteLine(
            "      Unlike a scheduled task, it will NOT restart automatically on crash.");
        Console.WriteLine(
            "      Admin / Task Scheduler was unavailable on this machine.");

        // Start an immediate detached instance for THIS session.
        SpawnDetachedServiceRun(exePath);
        Console.WriteLine("Publisher runtime started for this session.");
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath; // may be null; best-effort stop only

        // 1. Stop any running instance (best-effort — errors are swallowed).
        await ShellHelper.RunAsync("schtasks.exe", $"/End /TN \"{TaskName}\"", ct);

        // 2. Delete the scheduled task (/F = no confirmation, ignore "not found").
        var (rc, _, err) = await ShellHelper.RunAsync(
            "schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", ct);

        bool taskRemoved = rc == 0;
        if (!taskRemoved)
        {
            var msg = err.Trim();
            bool notFound =
                msg.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                rc == 1;
            if (!notFound)
                Console.WriteLine($"Note: schtasks /Delete returned rc={rc}: {msg}");
        }
        else
        {
            Console.WriteLine($"Scheduled task '{TaskName}' removed.");
        }

        // 3. Remove the HKCU Run-key entry (best-effort).
        bool runKeyRemoved = TryDeleteRunKeyValue();
        if (runKeyRemoved)
            Console.WriteLine($"Logon autostart registry entry removed.");

        // 4. If the Run-key was present, best-effort kill any running korat service process.
        if (runKeyRemoved && exePath is not null)
            BestEffortStopServiceProcess(exePath);

        if (!taskRemoved && !runKeyRemoved)
            Console.WriteLine("Service was not installed (neither scheduled task nor registry entry found).");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Check scheduled task.
        var (rc, stdout, stderr) = await ShellHelper.RunAsync(
            "schtasks.exe",
            $"/Query /TN \"{TaskName}\" /FO LIST /V",
            ct);

        bool taskExists = rc == 0;
        bool taskRunning = false;

        if (taskExists)
        {
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["Status:".Length..].Trim();
                    taskRunning = value.Equals("Running", StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
        }

        // Check HKCU Run key.
        bool runKeyExists = ReadRunKeyValue() is not null;

        // Build status.
        bool installed = taskExists || runKeyExists;
        if (!installed)
        {
            // Distinguish "error querying" vs "genuinely not installed".
            if (!taskExists)
            {
                var errMsg = stderr.Trim();
                bool notFound =
                    errMsg.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                    errMsg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                    rc == 1;
                if (!notFound)
                    return new ServiceStatus(false, false, $"Status query failed (rc={rc}): {errMsg}");
            }

            return new ServiceStatus(false, false, "Service not installed. Run `korat service install`.");
        }

        string mechanism;
        bool running;
        string detail;

        if (taskExists && runKeyExists)
        {
            mechanism = "scheduled task + registry Run-key";
            running = taskRunning;
            detail = taskRunning
                ? "Scheduled task is registered and currently running (registry Run-key also present)."
                : "Scheduled task is registered but not currently running (registry Run-key also present). Run `korat service reinstall` to restart.";
        }
        else if (taskExists)
        {
            mechanism = "scheduled task";
            running = taskRunning;
            detail = taskRunning
                ? "Scheduled task is registered and currently running."
                : "Scheduled task is registered but not currently running. It will start at next logon, or run `korat service reinstall` to restart now.";
        }
        else
        {
            // Run-key only.
            mechanism = "registry Run-key (Task Scheduler fallback)";
            running = false; // We can't query process state reliably cross-version; don't claim running.
            detail = "Logon autostart registered via HKCU Run-key (Task Scheduler was unavailable). " +
                     "The publisher runtime will start at next sign-in. No crash-restart.";
        }

        _ = mechanism; // consumed in detail strings above; suppress unused warning
        return new ServiceStatus(installed, running, detail);
    }

    // ── StartAsync / StopAsync (not part of IServiceController but useful internally) ──

    /// <summary>
    /// Starts the node service for the current session using whichever mechanism is installed.
    /// If the scheduled task exists, triggers it via <c>schtasks /Run</c>.
    /// Otherwise spawns a detached <c>service run</c> process.
    /// </summary>
    internal async Task StartAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine current executable path.");

        // Check for scheduled task first.
        var (rc, _, _) = await ShellHelper.RunAsync(
            "schtasks.exe", $"/Query /TN \"{TaskName}\" /FO LIST", ct);

        if (rc == 0)
        {
            var (rcRun, _, errRun) = await ShellHelper.RunAsync(
                "schtasks.exe", $"/Run /TN \"{TaskName}\"", ct);
            if (rcRun != 0)
                Console.WriteLine($"Note: schtasks /Run failed: {errRun.Trim()}");
            else
                Console.WriteLine("Publisher runtime started via scheduled task.");
            return;
        }

        // Run-key mode: spawn detached.
        SpawnDetachedServiceRun(exePath);
        Console.WriteLine("Publisher runtime started for this session.");
    }

    /// <summary>
    /// Stops the node service for the current session (best-effort).
    /// If the scheduled task exists, uses <c>schtasks /End</c>.
    /// Otherwise tries to terminate the process matching the korat service run invocation.
    /// </summary>
    internal async Task StopAsync(CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath;

        // Try schtasks /End first (covers the task-based case).
        await ShellHelper.RunAsync("schtasks.exe", $"/End /TN \"{TaskName}\"", ct);

        // Best-effort: also kill any detached service process in Run-key mode.
        if (exePath is not null)
            BestEffortStopServiceProcess(exePath);
    }

    // ── Internal platform helpers (called from unit-testable pure helpers) ─────

    [SupportedOSPlatform("windows")]
    private static void WriteRunKeyValue(string exePath)
    {
        var value = RunKeyCommand.BuildRunKeyValue(exePath);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunKeyValueName, value);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryDeleteRunKeyValue()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            var existing = key.GetValue(RunKeyValueName);
            if (existing is null) return false;
            key.DeleteValue(RunKeyValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string? ReadRunKeyValue()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunKeyValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SpawnDetachedServiceRun(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "service run",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note: could not start the publisher runtime for this session: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void BestEffortStopServiceProcess(string exePath)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // Match by main module path and command-line containing "service run".
                    if (proc.MainModule?.FileName?.Equals(exePath, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Best-effort: try to kill. Ignore all errors.
                        proc.Kill(entireProcessTree: false);
                    }
                }
                catch { /* swallow per-process errors */ }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch { /* swallow outer enumeration errors */ }
    }
}

/// <summary>
/// Pure string helpers for building <c>schtasks.exe</c> argument strings.
/// Not attributed with <c>[SupportedOSPlatform]</c> so these can be unit-tested
/// cross-platform without analyzer warnings.
/// </summary>
internal static class SchTasksCommand
{
    /// <summary>Name of the scheduled task (flat, no subfolder path).</summary>
    internal const string TaskName = "KoratNode";

    /// <summary>
    /// Builds the full <c>schtasks /Create</c> argument string for registering the
    /// Korat node as a per-user ONLOGON Scheduled Task. Handles nested quote escaping
    /// so that exe paths containing spaces (e.g. <c>C:\Program Files\korat\korat.exe</c>)
    /// are correctly quoted inside the <c>/TR</c> value.
    ///
    /// Example output:
    /// <c>/Create /TN "KoratNode" /TR "\"C:\Program Files\korat\korat.exe\" service run" /SC ONLOGON /RL LIMITED /IT /F</c>
    /// </summary>
    internal static string BuildCreateArguments(string taskName, string exePath)
    {
        // /TR value must be: "\"<exePath>\" service run"
        // The outer quotes delimit the /TR value for schtasks; the inner \" escape
        // quotes the exe path itself so cmd.exe handles spaces in the path correctly.
        return $"/Create /TN \"{taskName}\" /TR \"\\\"{exePath}\\\" service run\" /SC ONLOGON /RL LIMITED /IT /F";
    }
}

/// <summary>
/// Pure string helpers for building HKCU Run-key values.
/// Not attributed with <c>[SupportedOSPlatform]</c> so these can be unit-tested
/// cross-platform without analyzer warnings without triggering CA1416.
/// </summary>
internal static class RunKeyCommand
{
    /// <summary>Name of the HKCU Run-key value used as a fallback.</summary>
    internal const string ValueName = "KoratNode";

    /// <summary>
    /// Full registry key path for the per-user logon autostart entries.
    /// Opened under <c>HKCU</c> (no elevation required).
    /// </summary>
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Builds the registry value string that will be stored in
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
    ///
    /// The value is: <c>"&lt;exePath&gt;" service run</c>
    /// (the exe path is double-quoted so Windows handles spaces in the path correctly
    /// when it expands the Run-key command at logon).
    ///
    /// Example: <c>"C:\Program Files\korat\korat.exe" service run</c>
    /// </summary>
    internal static string BuildRunKeyValue(string exePath)
        => $"\"{exePath}\" service run";
}
