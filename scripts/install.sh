#!/usr/bin/env sh
# shellcheck shell=sh
# https://get.korat.ai/install.sh — sourced from
# https://get.korat.ai/install.sh
#
# One-line install for Korat CLI:
#   curl -fsSL https://get.korat.ai/install.sh | sh
#
# Channels:
#   curl -fsSL https://get.korat.ai/install.sh | sh                 # stable (default)
#   curl -fsSL https://get.korat.ai/install.sh | sh -s -- --dev     # latest pre-release
#
# Environment overrides (all optional):
#   KORAT_VERSION      Tag to install, e.g. v0.1.0. Default: latest (of the channel)
#   KORAT_CHANNEL      stable | dev. Default: stable. `--dev` is shorthand for dev.
#   KORAT_INSTALL_DIR  Where to put the binary. Default: ~/.korat/bin
#
# The script:
#   1. Detects OS + architecture.
#   2. Resolves the exact release version. Stable follows GitHub's well-known
#      /releases/latest redirect (no API call, no 60 req/hr anon rate limit);
#      the dev channel queries the releases API for the newest pre-release tag.
#   3. Downloads korat-cli-<version>-<platform>.tar.gz + SHA256SUMS.
#   4. Verifies SHA-256 (mandatory; no skip path).
#   5. Extracts the binary to KORAT_INSTALL_DIR and renames to "korat".
#   6. On macOS: strips the com.apple.quarantine xattr so Gatekeeper is happy.
#   7. Prints a PATH hint.

set -eu

KORAT_VERSION="${KORAT_VERSION:-latest}"
KORAT_INSTALL_DIR="${KORAT_INSTALL_DIR:-$HOME/.korat/bin}"
KORAT_CHANNEL="${KORAT_CHANNEL:-stable}"

# Parse CLI args (e.g. `curl … | sh -s -- --dev`). Env vars remain valid too.
for _arg in "$@"; do
  case "${_arg}" in
    --dev)    KORAT_CHANNEL="dev" ;;
    --stable) KORAT_CHANNEL="stable" ;;
    *)
      printf 'Unknown option: %s (supported: --dev, --stable)\n' "${_arg}" >&2
      exit 1
      ;;
  esac
done

# ── Platform detection ────────────────────────────────────────────────────────

SYS="$(uname -s)"
ARCH="$(uname -m)"

case "${SYS}/${ARCH}" in
  Darwin/arm64)   PLATFORM="darwin-arm64" ;;
  Darwin/x86_64)  PLATFORM="darwin-x64"   ;;
  Linux/x86_64)   PLATFORM="linux-x64"    ;;
  Linux/aarch64)  PLATFORM="linux-arm64"  ;;
  *)
    printf 'Unsupported platform: %s %s. See https://github.com/korat-ai/homebrew-tap/releases for available builds.\n' \
      "${SYS}" "${ARCH}" >&2
    exit 1
    ;;
esac

# ── SHA-256 helper ────────────────────────────────────────────────────────────

sha256_file() {
  # Usage: sha256_file <path>
  # Prints the hex digest of the file, or exits 1 if no sha tool is available.
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum > /dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    printf 'No SHA-256 tool found (tried sha256sum, shasum). Please install one and retry.\n' >&2
    exit 1
  fi
}

# ── Temp dir + cleanup ────────────────────────────────────────────────────────

TMPDIR_KORAT="$(mktemp -d)"
# shellcheck disable=SC2329  # invoked via trap, not a direct call
cleanup() {
  # shellcheck disable=SC2317  # reached via trap EXIT — shellcheck's reachability check can't see it
  rm -rf "${TMPDIR_KORAT}"
}
trap cleanup EXIT

# ── Resolve concrete version ──────────────────────────────────────────────────
# GitHub releases/<version>/download/<asset> requires an exact version in the
# asset filename. An explicit KORAT_VERSION wins; otherwise we resolve the
# channel's newest tag.

if [ "${KORAT_VERSION}" != "latest" ]; then
  # Explicit pin (e.g. KORAT_VERSION=v0.2.8-dev.1) — honoured for either channel.
  RESOLVED_VERSION="${KORAT_VERSION}"
elif [ "${KORAT_CHANNEL}" = "dev" ]; then
  printf 'Resolving latest dev (pre-release) version...\n'
  # Pre-releases are not pointed to by /releases/latest, so query the public tap's
  # releases API (returned newest-first) and take the first pre-release tag — one
  # whose SemVer carries a '-' suffix (e.g. v0.2.8-dev.1). The '-' filter is baked
  # into the sed pattern (v[^"]*-[^"]*) to stay portable — `grep -- '-'` breaks on
  # some grep variants (e.g. ugrep). This uses the anon GitHub API (60 req/hr) only
  # on the dev path; the stable path stays API-free.
  RESOLVED_VERSION="$(curl -fsSL \
    'https://api.github.com/repos/korat-ai/homebrew-tap/releases?per_page=30' \
    | tr -d '\r' \
    | sed -nE 's/^[[:space:]]*"tag_name":[[:space:]]*"(v[^"]*-[^"]*)".*/\1/p' \
    | head -1)"
  if [ -z "${RESOLVED_VERSION}" ]; then
    printf 'Could not resolve a dev (pre-release) version (none published yet?).\n' >&2
    exit 1
  fi
  printf '  -> %s\n' "${RESOLVED_VERSION}"
else
  printf 'Resolving latest version...\n'
  # Read the FIRST redirect (latest -> versioned), which carries the tag. Do NOT
  # use -L: following all hops lands on the signed release-assets URL, which no
  # longer contains the version path. GitHub's /releases/latest deliberately
  # ignores pre-releases, so the stable channel never picks up a dev build.
  RESOLVED_VERSION="$(curl -fsSI \
    'https://github.com/korat-ai/homebrew-tap/releases/latest/download/SHA256SUMS' \
    | tr -d '\r' \
    | sed -nE 's|^[Ll]ocation:[[:space:]]*.*/releases/download/(v[^/]+)/SHA256SUMS.*|\1|p' \
    | head -1)"
  if [ -z "${RESOLVED_VERSION}" ]; then
    printf 'Could not resolve latest version.\n' >&2
    exit 1
  fi
  printf '  -> %s\n' "${RESOLVED_VERSION}"
fi

ASSET="korat-cli-${RESOLVED_VERSION}-${PLATFORM}.tar.gz"
BASE="https://github.com/korat-ai/homebrew-tap/releases/download/${RESOLVED_VERSION}"

# ── Download ──────────────────────────────────────────────────────────────────

printf 'Downloading %s...\n' "${ASSET}"
curl -fsSL "${BASE}/${ASSET}"     -o "${TMPDIR_KORAT}/${ASSET}"
curl -fsSL "${BASE}/SHA256SUMS"   -o "${TMPDIR_KORAT}/SHA256SUMS"

# ── Verify SHA-256 ────────────────────────────────────────────────────────────

printf 'Verifying SHA-256...\n'

EXPECTED="$(grep " ${ASSET}\$" "${TMPDIR_KORAT}/SHA256SUMS" | awk '{print $1}')"
if [ -z "${EXPECTED}" ]; then
  printf 'SHA256SUMS does not contain an entry for %s\n' "${ASSET}" >&2
  exit 1
fi

ACTUAL="$(sha256_file "${TMPDIR_KORAT}/${ASSET}")"

if [ "${EXPECTED}" != "${ACTUAL}" ]; then
  printf 'SHA-256 mismatch!\n  expected: %s\n  actual:   %s\n' \
    "${EXPECTED}" "${ACTUAL}" >&2
  exit 1
fi
printf '  OK (%s)\n' "${EXPECTED}"

# ── Extract + install ─────────────────────────────────────────────────────────

printf 'Installing to %s...\n' "${KORAT_INSTALL_DIR}"
mkdir -p "${KORAT_INSTALL_DIR}"
tar -xzf "${TMPDIR_KORAT}/${ASSET}" -C "${KORAT_INSTALL_DIR}"
mv "${KORAT_INSTALL_DIR}/Korat.Cli" "${KORAT_INSTALL_DIR}/korat"
chmod +x "${KORAT_INSTALL_DIR}/korat"

# On macOS, remove the quarantine xattr set by curl so Gatekeeper doesn't
# block the first run. 2>/dev/null silences "no such xattr" on clean downloads.
if [ "${SYS}" = "Darwin" ]; then
  xattr -d com.apple.quarantine "${KORAT_INSTALL_DIR}/korat" 2>/dev/null || true
fi

# ── Done — add to PATH (persist) ──────────────────────────────────────────────

printf '\nInstalled korat to %s\n' "${KORAT_INSTALL_DIR}/korat"

case ":${PATH}:" in
  *":${KORAT_INSTALL_DIR}:"*) on_path=1 ;;
  *) on_path=0 ;;
esac

if [ "${on_path}" -eq 1 ]; then
  printf 'Already on your PATH. Run: korat version\n'
else
  case "$(basename "${SHELL:-/bin/sh}")" in
    zsh)  rc="${HOME}/.zshrc" ;;
    bash) rc="${HOME}/.bashrc" ;;
    *)    rc="${HOME}/.profile" ;;
  esac
  line="export PATH=\"${KORAT_INSTALL_DIR}:\$PATH\""
  if ! grep -qF "${KORAT_INSTALL_DIR}" "${rc}" 2>/dev/null; then
    printf '\n# Added by the Korat installer\n%s\n' "${line}" >> "${rc}"
    printf 'Added to PATH in %s\n' "${rc}"
  fi
  printf 'Run this now (or open a new terminal):\n  source %s\n' "${rc}"
  printf 'Then: korat version\n'
fi

exit 0
