using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Korat.Cli.Util;

namespace Korat.Cli.Commands;

public static class UpgradeCommand
{
    // Binaries are published to the public korat-ai/homebrew-tap distribution
    // repository. install.sh and `korat upgrade` deliberately use the same source.
    internal const string MirrorReleases = "https://github.com/korat-ai/homebrew-tap/releases";

    private const string LatestRedirectUrl =
        MirrorReleases + "/latest/download/SHA256SUMS";

    /// <summary>Describes how the korat binary was installed on this machine (#101/#103).</summary>
    internal enum InstallMethod
    {
        /// <summary>Installed via <c>brew install korat</c>.</summary>
        Homebrew,
        /// <summary>Running on Windows (self-upgrade not yet implemented).</summary>
        Windows,
        /// <summary>Installed via install.sh into ~/.korat/bin, or an unknown path.</summary>
        SelfManaged,
    }

    public static Command Create()
    {
        var command = new Command("upgrade", "Upgrade korat to the latest release.");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt.");
        command.AddOption(yesOption);
        command.SetHandler(async (bool yes) => await RunAsync(yes), yesOption);
        return command;
    }

    /// <summary>
    /// #101/#103: detects the install method by inspecting the running executable's path.
    /// </summary>
    internal static InstallMethod DetectInstallMethod()
    {
        if (OperatingSystem.IsWindows())
            return InstallMethod.Windows;

        var processPath = Environment.ProcessPath ?? string.Empty;

        var isHomebrew =
            processPath.Contains("/Cellar/", StringComparison.OrdinalIgnoreCase) ||
            processPath.StartsWith("/opt/homebrew/", StringComparison.OrdinalIgnoreCase) ||
            processPath.StartsWith("/usr/local/Cellar/", StringComparison.OrdinalIgnoreCase) ||
            processPath.StartsWith("/usr/local/bin/korat", StringComparison.OrdinalIgnoreCase);

        return isHomebrew ? InstallMethod.Homebrew : InstallMethod.SelfManaged;
    }

    /// <summary>
    /// #101: returns a one-line description of the install method and the correct
    /// upgrade command. Shared with <c>korat version</c> so both surfaces agree.
    /// </summary>
    internal static (string Description, string UpgradeCommand) GetInstallInfo()
    {
        return DetectInstallMethod() switch
        {
            InstallMethod.Homebrew =>
                ("Homebrew", "brew upgrade korat"),
            InstallMethod.Windows =>
                ("Windows", "irm https://get.korat.ai/install.ps1 | iex"),
            InstallMethod.SelfManaged =>
                ("self-managed (~/.korat/bin)", "curl -fsSL https://get.korat.ai/install.sh | sh"),
            _ =>
                ("unknown", "curl -fsSL https://get.korat.ai/install.sh | sh"),
        };
    }

    internal static async Task RunAsync(bool yes)
    {
        // #101/#103: surface the install method and correct upgrade command up front,
        // before any network I/O, so the user gets actionable guidance immediately.
        var installMethod = DetectInstallMethod();
        var (installDescription, upgradeCommand) = GetInstallInfo();
        Console.Error.WriteLine($"Install method: {installDescription}");
        Console.Error.WriteLine($"Upgrade command: {upgradeCommand}");
        Console.Error.WriteLine();

        // #103: Windows self-replace is not yet implemented. This is informational,
        // not an error — the user did nothing wrong, so exit 0.
        if (installMethod == InstallMethod.Windows)
        {
            Console.WriteLine(
                "Automatic in-place upgrade is not yet supported on Windows.\n" +
                "To upgrade, run the PowerShell one-liner above, or download the latest\n" +
                "win-x64 .zip from:\n" +
                "  " + MirrorReleases + "/latest");
            return; // exit 0 — informational
        }

        // #101: Homebrew installs must be upgraded via brew to keep the package manifest
        // consistent. Exit with code 1 (action required) after printing guidance.
        if (installMethod == InstallMethod.Homebrew)
        {
            Console.Error.WriteLine(
                "This binary was installed via Homebrew.\n" +
                "Run `brew upgrade korat` to upgrade instead.\n" +
                "Self-replacing a Homebrew binary would break Homebrew's package manifest.");
            Environment.ExitCode = 1;
            return;
        }


        // Step 1: resolve "latest" version via 302 redirect
        var latestVersion = await ResolveLatestVersionAsync();
        if (latestVersion is null)
        {
            Console.Error.WriteLine("Could not resolve the latest release version.");
            Environment.Exit(1);
            return; // unreachable — satisfies flow analysis
        }

        // Step 2: compare with current version
        // Normalise: strip build metadata for comparison, add "v" prefix if missing
        var currentVersion = CliVersion.Bare();
        var latestClean = latestVersion.TrimStart('v');

        if (currentVersion == latestClean)
        {
            Console.WriteLine($"Already on latest ({latestVersion})");
            return;
        }

        // Step 3: print upgrade notice to stderr
        Console.Error.WriteLine($"Upgrading korat from v{currentVersion} to {latestVersion}");

        // Step 4: confirm unless --yes
        if (!yes)
        {
            Console.Error.Write("Continue? [y/N] ");
            string? answer;
            if (OperatingSystem.IsWindows())
            {
                // On Windows there is no /dev/tty; read directly from Console which maps
                // to the attached console window even when stdin is redirected.
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("stdin is redirected. Use --yes to skip confirmation.");
                    Environment.Exit(1);
                    return; // unreachable
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
                    Environment.Exit(1);
                    return; // unreachable
                }
            }

            if (answer != "y" && answer != "yes")
            {
                Console.Error.WriteLine("Upgrade cancelled.");
                return;
            }
        }

        // Step 5: detect platform
        var platform = DetectPlatform();

        // Build URLs
        var assetName = $"korat-cli-{latestVersion}-{platform}.tar.gz";
        var baseUrl = $"{MirrorReleases}/download/{latestVersion}";
        var assetUrl = $"{baseUrl}/{assetName}";
        var sumsUrl = $"{baseUrl}/SHA256SUMS";

        // Step 6: download archive and SHA256SUMS.
        // Explicit status checks so a mirror/CDN outage (get.korat.ai 522, a 404, a transient
        // GitHub-releases 5xx) fails LOUDLY with the URL + HTTP status + a retry hint — not the
        // opaque "status code does not indicate success" with no context that #336 reported.
        using var httpDl = new HttpClient();

        Console.Error.WriteLine($"Downloading {assetName}");
        Console.Error.WriteLine($"  from {assetUrl}");
        var assetBytes = await DownloadBytesOrFailAsync(httpDl, assetUrl, "release archive");

        Console.Error.WriteLine("Downloading SHA256SUMS");
        var sumsBytes = await DownloadBytesOrFailAsync(httpDl, sumsUrl, "checksum file");
        var sumsContent = Encoding.UTF8.GetString(sumsBytes);

        // Step 7: verify SHA-256
        var computedHash = SHA256.HashData(assetBytes);
        var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        var expectedHash = ParseSha256Sums(sumsContent, assetName);
        if (expectedHash is null)
        {
            Console.Error.WriteLine($"No entry for {assetName} in SHA256SUMS.");
            Environment.Exit(1);
        }

        if (computedHex != expectedHash)
        {
            Console.Error.WriteLine($"SHA-256 mismatch!\n  expected: {expectedHash}\n  got:      {computedHex}");
            Environment.Exit(1);
        }

        // Step 8: resolve the running executable and its install location.
        // Environment.ProcessPath is the path of the current process on .NET 6+.
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            Console.Error.WriteLine("Cannot determine the running executable path. Upgrade aborted.");
            Environment.Exit(1);
        }

        // Homebrew installs are under a Cellar or the Homebrew prefix — self-replacing
        // them would break Homebrew's package manifest. The user should run
        // `brew upgrade korat` instead.
        var isHomebrew =
            processPath.Contains("/Cellar/", StringComparison.OrdinalIgnoreCase) ||
            processPath.StartsWith("/opt/homebrew/", StringComparison.OrdinalIgnoreCase) ||
            processPath.StartsWith("/usr/local/", StringComparison.OrdinalIgnoreCase);
        if (isHomebrew)
        {
            Console.Error.WriteLine(
                "This binary was installed via Homebrew. " +
                "Run `brew upgrade korat` to upgrade instead.");
            Environment.Exit(1);
        }

        var finalPath = processPath;
        var koratBinDir = Path.GetDirectoryName(finalPath) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".korat", "bin");
        Directory.CreateDirectory(koratBinDir);

        var newPath = Path.Combine(koratBinDir, "korat.new");

        using (var ms = new MemoryStream(assetBytes))
        using (var gz = new GZipStream(ms, CompressionMode.Decompress))
        using (var tar = new TarReader(gz, leaveOpen: false))
        {
            bool extracted = false;
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync()) != null)
            {
                // The archive contains a single entry named "Korat.Cli"
                if (string.Equals(Path.GetFileName(entry.Name), "Korat.Cli",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await using var dest = new FileStream(newPath, FileMode.Create, FileAccess.Write);
                    await entry.DataStream!.CopyToAsync(dest);
                    extracted = true;
                    break;
                }
            }

            if (!extracted)
            {
                Console.Error.WriteLine("Archive did not contain expected 'Korat.Cli' entry.");
                Environment.Exit(1);
            }
        }

        // #91: prepare the REPLACEMENT fully BEFORE swapping it in. Once File.Move replaces the
        // running executable's on-disk file, macOS raises SIGBUS the next time it must page in a
        // not-yet-resident code page of THIS process — so the old code crashed during the tail
        // steps (chmod / xattr spawn / final WriteLine) even though the swap had already succeeded.
        // Do every page-faulting step now, on `newPath`; make File.Move the LAST filesystem op and
        // exit immediately so no further code is paged in.

        // Step 9: chmod +x on the replacement (macOS/Linux only — Windows not supported by upgrade).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
#pragma warning disable CA1416 // platform guard is explicit above
            File.SetUnixFileMode(newPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        }

        // Step 10: remove com.apple.quarantine on macOS (on the replacement, before the swap).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xattr",
                    ArgumentList = { "-d", "com.apple.quarantine", newPath },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (proc != null)
                    await proc.WaitForExitAsync();
                // ignore non-zero exit — attribute may not exist
            }
            catch
            {
                // xattr not available or other issue — not fatal
            }
        }

        // Print success BEFORE the swap — afterwards we must not fault in any more code pages (#91).
        // This is also why we do NOT exec the new binary to self-verify the version here: launching
        // a subprocess after File.Move would page in code of THIS (now-replaced) executable and raise
        // SIGBUS on macOS. The SHA-256 check above already proves the replacement's integrity, so a
        // post-swap `korat version` would add risk without adding assurance.
        Console.WriteLine($"Upgraded to {latestVersion} (checksum verified). Run `korat version` to confirm.");

        // Step 11: atomic self-replace — the LAST filesystem op — then exit immediately (#91).
        File.Move(newPath, finalPath, overwrite: true);
        Environment.Exit(0);
    }

    /// <summary>
    /// A2 (doctor): resolves the latest published CLI version (e.g. <c>"v0.4.1"</c>) via the
    /// same 302-redirect trick <see cref="RunAsync"/> uses, WITHOUT calling
    /// <see cref="Environment.Exit"/> — callers decide how to react to a null result.
    /// Extracted so <c>korat doctor</c>'s "version" check reuses this exact redirect logic
    /// instead of duplicating it. Returns <see langword="null"/> on any failure (bad redirect
    /// status, missing/unparseable Location header, or a network/transport exception) so
    /// offline machines degrade gracefully instead of throwing.
    /// </summary>
    /// <param name="handlerOverride">Test seam for the transport.</param>
    /// <param name="timeout">
    /// Final-review LOW fix: optional <see cref="HttpClient.Timeout"/> override. Defaults to
    /// <see langword="null"/>, which keeps the BCL's 100s default — <c>korat upgrade</c>'s own
    /// call site (<see cref="RunAsync"/>) is unaffected. <c>korat doctor</c> passes an explicit
    /// short timeout so a blackholed network doesn't hang the whole report.
    /// </param>
    internal static async Task<string?> ResolveLatestVersionAsync(
        HttpMessageHandler? handlerOverride = null, TimeSpan? timeout = null)
    {
        try
        {
            var handler = handlerOverride ?? new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler, disposeHandler: handlerOverride is null);
            if (timeout is { } t) http.Timeout = t;
            var req = new HttpRequestMessage(HttpMethod.Head, LatestRedirectUrl);
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (resp.StatusCode != System.Net.HttpStatusCode.Found &&
                resp.StatusCode != System.Net.HttpStatusCode.MovedPermanently)
            {
                return null;
            }

            var location = resp.Headers.Location?.ToString();
            if (location is null)
                return null;

            // Location example: /korat-ai/homebrew-tap/releases/download/v0.1.2/SHA256SUMS
            var segments = location.Split('/');
            int downloadIdx = Array.IndexOf(segments, "download");
            if (downloadIdx < 0 || downloadIdx + 1 >= segments.Length)
                return null;

            return segments[downloadIdx + 1]; // e.g. "v0.1.2"
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> and returns its bytes, or terminates the process with exit
    /// code 1 after printing an actionable message (URL + HTTP status, or the transport error) on any
    /// failure. The bare <c>HttpClient.GetByteArrayAsync</c> throws an opaque "status code does not
    /// indicate success" with no URL — the unactionable failure #336 saw during a mirror outage.
    /// </summary>
    private static async Task<byte[]> DownloadBytesOrFailAsync(HttpClient http, string url, string what)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"Failed to download the {what}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n" +
                    $"  url: {url}\n" +
                    "The release mirror may be temporarily unavailable. Re-run `korat upgrade` in a few\n" +
                    "minutes, or install manually: curl -fsSL https://get.korat.ai/install.sh | sh");
                Environment.Exit(1);
            }

            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine(
                $"Failed to download the {what}: {ex.Message}\n" +
                $"  url: {url}\n" +
                "Check your network connection, then re-run `korat upgrade`, or install manually:\n" +
                "  curl -fsSL https://get.korat.ai/install.sh | sh");
            Environment.Exit(1);
            return []; // unreachable — Environment.Exit(1) does not return
        }
    }

    internal static string DetectPlatform()
    {
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (isWindows)
        {
            // Windows self-upgrade via korat upgrade is not yet implemented.
            // The win-x64 .zip is available on the GitHub releases page; download
            // and extract Korat.Cli.exe manually, or use the PowerShell one-liner:
            //   irm https://get.korat.ai/install.ps1 | iex
            Console.Error.WriteLine(
                "Automatic upgrade is not yet supported on Windows.\n" +
                "To upgrade, run the PowerShell one-liner:\n" +
                "  irm https://get.korat.ai/install.ps1 | iex\n" +
                "Or download the latest win-x64 .zip from:\n" +
                "  " + MirrorReleases + "/latest");
            Environment.Exit(1);
        }

        var os = isMac ? "darwin" : isLinux ? "linux" : null;
        if (os is null)
        {
            Console.Error.WriteLine("Unsupported OS. Only macOS and Linux are supported by `korat upgrade`.");
            Environment.Exit(1);
        }

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => null,
        };

        if (arch is null)
        {
            Console.Error.WriteLine($"Unsupported architecture: {RuntimeInformation.OSArchitecture}");
            Environment.Exit(1);
        }

        return $"{os}-{arch}";
    }

    internal static string? ParseSha256Sums(string content, string filename)
    {
        // SHA256SUMS format: "<hex>  <filename>" (two spaces) or "<hex> <filename>"
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd();
            // split on two-space or one-space separator
            var spaceIdx = trimmed.IndexOf("  ", StringComparison.Ordinal);
            if (spaceIdx < 0)
                spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx < 0)
                continue;

            var hash = trimmed[..spaceIdx].Trim();
            var name = trimmed[(spaceIdx + 1)..].TrimStart().Trim();

            // name may be prefixed with "./" or just the filename
            if (Path.GetFileName(name).Equals(filename, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }

        return null;
    }
}
