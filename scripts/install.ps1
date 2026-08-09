# https://get.korat.ai/install.ps1
# One-line install for Korat CLI on Windows:
#   irm https://get.korat.ai/install.ps1 | iex
#
# Or with flags (wrap in a script-block so iex can forward arguments):
#   irm https://get.korat.ai/install.ps1 | iex          # stable (default)
#   & ([scriptblock]::Create((irm https://get.korat.ai/install.ps1))) --dev  # latest pre-release
#
# Environment overrides (all optional):
#   $env:KORAT_VERSION      Tag to install, e.g. v0.1.0.  Default: latest (of the channel)
#   $env:KORAT_CHANNEL      stable | dev.                  Default: stable. --dev is shorthand.
#   $env:KORAT_INSTALL_DIR  Where to put the binary.       Default: %LOCALAPPDATA%\Programs\korat
#
# The script:
#   1. Detects architecture (x64 only; arm64 is a noted follow-up).
#   2. Resolves the exact release version.
#   3. Downloads korat-cli-<version>-win-x64.zip + SHA256SUMS.
#   4. Verifies SHA-256 (mandatory; no skip path).
#   5. Extracts Korat.Cli.exe to KORAT_INSTALL_DIR, renames to korat.exe.
#   6. Adds KORAT_INSTALL_DIR to the user-scope PATH (persisted in registry).
#   7. Prints next steps.

[CmdletBinding()]
param(
    [switch]$Dev,
    [switch]$Stable
)

# NOTE: Set-StrictMode -Version Latest is intentionally NOT used. This script is piped to
# `iex` on arbitrary Windows machines (Windows PowerShell 5.1 .. PowerShell 7+); under strict
# mode a benign missing-property access (e.g. on older .NET Framework) becomes a fatal
# PropertyNotFoundStrict, breaking the install. Critical values are guarded explicitly below.
$ErrorActionPreference = 'Stop'

# ── Channel and version resolution ───────────────────────────────────────────

$channel = if ($env:KORAT_CHANNEL) { $env:KORAT_CHANNEL } else { 'stable' }
if ($Dev)    { $channel = 'dev' }
if ($Stable) { $channel = 'stable' }

$pinnedVersion = if ($env:KORAT_VERSION -and $env:KORAT_VERSION -ne 'latest') {
    $env:KORAT_VERSION
} else { $null }

# ── Architecture detection ────────────────────────────────────────────────────

# Use the PROCESSOR_ARCHITECTURE env var rather than
# [RuntimeInformation]::OSArchitecture — the latter is absent on older .NET Framework
# (Windows PowerShell 5.1) and throws under strict mode. PROCESSOR_ARCHITEW6432 is set when
# a 32-bit PowerShell runs on 64-bit Windows (WOW64), so check it first.
$arch = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITEW6432')
if (-not $arch) { $arch = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE') }
switch ("$arch".ToUpperInvariant()) {
    'AMD64' { $platform = 'win-x64' }
    'ARM64' { $platform = 'win-x64' }  # Windows on ARM runs the x64 build via emulation
    default {
        Write-Error "Unsupported architecture: $arch. Only x64 is supported on Windows at this time. See https://github.com/korat-ai/homebrew-tap/releases"
        exit 1
    }
}

# ── Install directory ─────────────────────────────────────────────────────────

$installDir = if ($env:KORAT_INSTALL_DIR) {
    $env:KORAT_INSTALL_DIR
} else {
    Join-Path $env:LOCALAPPDATA 'Programs\korat'
}

# ── Resolve concrete version ──────────────────────────────────────────────────

if ($pinnedVersion) {
    $resolvedVersion = $pinnedVersion
} elseif ($channel -eq 'dev') {
    Write-Host 'Resolving latest dev (pre-release) version...'
    $releases = Invoke-RestMethod `
        -Uri 'https://api.github.com/repos/korat-ai/homebrew-tap/releases?per_page=30' `
        -UseBasicParsing
    $devRelease = $releases | Where-Object { $_.prerelease -eq $true } | Select-Object -First 1
    if (-not $devRelease) {
        Write-Error 'Could not resolve a dev (pre-release) version (none published yet?).'
        exit 1
    }
    $resolvedVersion = $devRelease.tag_name
    Write-Host "  -> $resolvedVersion"
} else {
    Write-Host 'Resolving latest version...'
    # Follow the /releases/latest redirect and parse the Location header for the version.
    # We use a HEAD request with manual redirect handling so we see the 302 Location.
    try {
        $resp = Invoke-WebRequest `
            -Uri 'https://github.com/korat-ai/homebrew-tap/releases/latest/download/SHA256SUMS' `
            -Method Head `
            -MaximumRedirection 0 `
            -UseBasicParsing `
            -ErrorAction SilentlyContinue
        $location = $resp.Headers['Location']
    } catch [System.Net.WebException] {
        $location = $_.Exception.Response.Headers['Location']
    }
    if (-not $location) {
        Write-Error 'Could not resolve latest version (no Location header in redirect).'
        exit 1
    }
    # Location: .../releases/download/v0.1.2/SHA256SUMS
    if ($location -match '/releases/download/(v[^/]+)/SHA256SUMS') {
        $resolvedVersion = $Matches[1]
    } else {
        Write-Error "Could not parse version from Location: $location"
        exit 1
    }
    Write-Host "  -> $resolvedVersion"
}

# ── Build download URLs ───────────────────────────────────────────────────────

$assetName = "korat-cli-${resolvedVersion}-${platform}.zip"
$baseUrl = "https://github.com/korat-ai/homebrew-tap/releases/download/${resolvedVersion}"
$assetUrl = "${baseUrl}/${assetName}"
$sumsUrl  = "${baseUrl}/SHA256SUMS"

# ── Temp dir + cleanup ────────────────────────────────────────────────────────

$tmpDir = Join-Path $env:TEMP ([System.Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

try {

    # ── Download ──────────────────────────────────────────────────────────────

    Write-Host "Downloading $assetName..."
    $assetPath = Join-Path $tmpDir $assetName
    Invoke-WebRequest -Uri $assetUrl -OutFile $assetPath -UseBasicParsing

    Write-Host 'Downloading SHA256SUMS...'
    $sumsPath = Join-Path $tmpDir 'SHA256SUMS'
    Invoke-WebRequest -Uri $sumsUrl -OutFile $sumsPath -UseBasicParsing

    # ── Verify SHA-256 ────────────────────────────────────────────────────────

    Write-Host 'Verifying SHA-256...'

    $sumsContent = Get-Content $sumsPath -Raw
    $expectedLine = $sumsContent -split "`n" |
        Where-Object { $_ -match "\s${assetName}$" } |
        Select-Object -First 1

    if (-not $expectedLine) {
        Write-Error "SHA256SUMS does not contain an entry for $assetName"
        exit 1
    }

    # Format: "<hex>  <filename>" (two spaces) or "<hex> <filename>"
    $expectedHash = ($expectedLine.Trim() -split '\s+')[0].ToLowerInvariant()

    $actualHash = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualHash -ne $expectedHash) {
        Write-Error "SHA-256 mismatch!`n  expected: $expectedHash`n  actual:   $actualHash"
        exit 1
    }
    Write-Host "  OK ($expectedHash)"

    # ── Extract + install ─────────────────────────────────────────────────────

    Write-Host "Installing to $installDir..."
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null

    $extractDir = Join-Path $tmpDir 'extracted'
    Expand-Archive -Path $assetPath -DestinationPath $extractDir -Force

    $exeSrc = Join-Path $extractDir 'Korat.Cli.exe'
    if (-not (Test-Path $exeSrc)) {
        Write-Error "Archive did not contain expected 'Korat.Cli.exe' entry."
        exit 1
    }

    $exeDst = Join-Path $installDir 'korat.exe'
    Move-Item -Path $exeSrc -Destination $exeDst -Force

    Write-Host "Installed korat to $exeDst"

    # ── Add to user PATH (persisted) ──────────────────────────────────────────

    $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$installDir*") {
        $newPath = if ($userPath) { "${userPath};${installDir}" } else { $installDir }
        [System.Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Write-Host "Added $installDir to your user PATH."
        Write-Host "Open a new terminal for the change to take effect."
    } else {
        Write-Host "$installDir is already on your PATH."
    }

    # Also update the current session's PATH so 'korat version' works immediately.
    if ($env:PATH -notlike "*$installDir*") {
        $env:PATH = "${env:PATH};${installDir}"
    }

    # ── Done ──────────────────────────────────────────────────────────────────

    Write-Host ''
    Write-Host "Run: korat version"
    Write-Host "Then: korat login"
    Write-Host "And:  korat service install  (start the background node daemon)"

} finally {
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}
