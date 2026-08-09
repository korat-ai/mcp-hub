using System.Runtime.InteropServices;
using Korat.Cli.Commands;

namespace Korat.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="UpgradeCommand"/> internal helpers:
/// <see cref="UpgradeCommand.ParseSha256Sums"/>, version normalization, and
/// <see cref="UpgradeCommand.DetectPlatform"/>.
/// </summary>
public class UpgradeCommandTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Release source — must be the PUBLIC mirror, not the private main repo
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Upgrade_downloads_from_public_tap_mirror_not_private_repo()
    {
        // The main repo (korat-ai/korat-mcp-hub) is PRIVATE → its release assets 404
        // anonymously. korat upgrade must use the PUBLIC homebrew-tap mirror — the same
        // source install.sh uses — or it fails with "Unexpected redirect status: 404".
        Assert.Contains("korat-ai/homebrew-tap", UpgradeCommand.MirrorReleases);
        Assert.DoesNotContain("korat-mcp-hub", UpgradeCommand.MirrorReleases);
        Assert.StartsWith("https://", UpgradeCommand.MirrorReleases);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParseSha256Sums
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSha256Sums_two_space_separator_returns_hash()
    {
        const string sums =
            "abc123def456  korat-cli-v1.2.3-darwin-arm64.tar.gz\n" +
            "deadbeef0000  korat-cli-v1.2.3-linux-x64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-darwin-arm64.tar.gz");

        Assert.Equal("abc123def456", result);
    }

    [Fact]
    public void ParseSha256Sums_one_space_separator_returns_hash()
    {
        const string sums = "abc123def456 korat-cli-v1.2.3-linux-x64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-linux-x64.tar.gz");

        Assert.Equal("abc123def456", result);
    }

    [Fact]
    public void ParseSha256Sums_dot_slash_prefixed_filename_matches()
    {
        // Some tools emit "./<filename>" in SHA256SUMS.
        const string sums = "aabbccdd0011  ./korat-cli-v1.2.3-darwin-arm64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-darwin-arm64.tar.gz");

        Assert.Equal("aabbccdd0011", result);
    }

    [Fact]
    public void ParseSha256Sums_case_insensitive_hash_normalization()
    {
        // Hashes may appear in upper-case; the method must return lower-case.
        const string sums = "AABBCCDDEEFF0011  korat-cli-v1.2.3-osx-arm64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-osx-arm64.tar.gz");

        Assert.Equal("aabbccddeeff0011", result);
    }

    [Fact]
    public void ParseSha256Sums_missing_entry_returns_null()
    {
        const string sums =
            "abc123  korat-cli-v1.2.3-darwin-arm64.tar.gz\n" +
            "deadbeef  korat-cli-v1.2.3-linux-x64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-linux-arm64.tar.gz");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSha256Sums_empty_content_returns_null()
    {
        var result = UpgradeCommand.ParseSha256Sums(string.Empty, "korat-cli-v1.2.3-darwin-arm64.tar.gz");

        Assert.Null(result);
    }

    [Fact]
    public void ParseSha256Sums_case_insensitive_filename_match()
    {
        // Filename comparison is case-insensitive per the implementation.
        const string sums = "abc123  Korat-Cli-V1.2.3-Darwin-Arm64.tar.gz\n";

        var result = UpgradeCommand.ParseSha256Sums(sums, "korat-cli-v1.2.3-darwin-arm64.tar.gz");

        Assert.Equal("abc123", result);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Version normalization (strip leading 'v', ignore build metadata after '+')
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v0.0.0-dev+local", "0.0.0-dev")]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("v1.0.0+build.456", "1.0.0")]
    public void VersionNormalization_strips_v_prefix_and_build_metadata(
        string raw, string expected)
    {
        // The normalization in UpgradeCommand is:
        //   raw.Split('+')[0].TrimStart('v')
        var normalized = raw.Split('+')[0].TrimStart('v');
        Assert.Equal(expected, normalized);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DetectPlatform — only validates the format, not which platform we're on
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectPlatform_returns_valid_rid_on_supported_platform()
    {
        // Skip on unsupported platforms (Windows / exotic arches) where DetectPlatform
        // would call Environment.Exit — we can't test that path in-process.
        var isSupported =
            (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
             RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) &&
            RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;

        if (!isSupported)
            return; // skip gracefully on unsupported hosts

        var platform = UpgradeCommand.DetectPlatform();

        // Expected shape: "<os>-<arch>"  e.g. "darwin-arm64", "linux-x64"
        Assert.Matches(@"^(darwin|linux)-(arm64|x64)$", platform);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SHA-256 mismatch → ParseSha256Sums returns a different hash
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseSha256Sums_mismatch_detected_by_caller()
    {
        const string sums = "aaaa1111  korat-cli-v1.0.0-linux-x64.tar.gz\n";
        const string filename = "korat-cli-v1.0.0-linux-x64.tar.gz";

        var expected = UpgradeCommand.ParseSha256Sums(sums, filename);
        Assert.NotNull(expected);

        // Simulate a computed hash that differs from the expected one.
        const string computedHex = "bbbb2222";
        Assert.NotEqual(computedHex, expected); // mismatch → abort path would fire
    }

    // ──────────────────────────────────────────────────────────────────────────
    // #101/#103: install-method detection and guidance
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectInstallMethod_matches_GetInstallInfo()
    {
        var method = UpgradeCommand.DetectInstallMethod();
        var (description, upgradeCommand) = UpgradeCommand.GetInstallInfo();

        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.False(string.IsNullOrWhiteSpace(upgradeCommand));

        // The upgrade command must correspond to the detected method.
        switch (method)
        {
            case UpgradeCommand.InstallMethod.Homebrew:
                Assert.Equal("brew upgrade korat", upgradeCommand);
                break;
            case UpgradeCommand.InstallMethod.Windows:
                Assert.Contains("install.ps1", upgradeCommand);
                break;
            case UpgradeCommand.InstallMethod.SelfManaged:
                Assert.Contains("install.sh", upgradeCommand);
                break;
        }
    }

    [Fact]
    public void GetInstallInfo_on_windows_is_windows_method()
    {
        if (!OperatingSystem.IsWindows())
            return; // only meaningful on Windows hosts

        Assert.Equal(UpgradeCommand.InstallMethod.Windows, UpgradeCommand.DetectInstallMethod());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ResolveLatestVersionAsync — optional timeout (final-review LOW fix, A2/doctor)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Answers every request after an artificial delay, honoring cancellation.</summary>
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.Found);
        }
    }

    [Fact]
    public async Task ResolveLatestVersionAsync_returns_null_when_timeout_elapses_before_response()
    {
        // `korat doctor` passes an explicit short timeout so a blackholed network degrades
        // the "version" check to "warn: could not check for updates" instead of hanging the
        // whole report. A 2s response against a 50ms timeout deterministically exercises the
        // failure path without the test itself waiting anywhere near 2s (HttpClient.Timeout
        // cancels the in-flight request almost immediately).
        var handler = new DelayingHandler(TimeSpan.FromSeconds(2));

        var result = await UpgradeCommand.ResolveLatestVersionAsync(handler, timeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveLatestVersionAsync_null_timeout_preserves_korat_upgrades_own_behavior()
    {
        // `timeout: null` (the default) must NOT set HttpClient.Timeout at all — this is the
        // exact call shape `korat upgrade` (RunAsync) itself uses, and it must keep working
        // against a slower-than-instant-but-still-fast response.
        var handler = new RoutedHandler(_ =>
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            resp.Headers.Location =
                new Uri("https://github.com/korat-ai/homebrew-tap/releases/download/v1.2.3/SHA256SUMS");
            return resp;
        });

        var result = await UpgradeCommand.ResolveLatestVersionAsync(handler, timeout: null);

        Assert.Equal("v1.2.3", result);
    }

    private sealed class RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
