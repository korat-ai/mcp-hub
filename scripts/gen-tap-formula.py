#!/usr/bin/env python3
"""Generate the Homebrew Formula for the Korat CLI from a release's SHA256SUMS.

Used by .github/workflows/release.yml (bump-tap job). Kept as a standalone script
(not inline in YAML) so the Ruby indentation is unambiguous and it can be tested
locally:

    python3 scripts/gen-tap-formula.py v0.2.0 /path/to/SHA256SUMS /tmp/korat.rb

Release assets are mirrored to korat-ai/homebrew-tap so Homebrew and the
installers share one public distribution source. Asset filenames are versioned
(korat-cli-<tag>-<platform>.tar.gz), matching what the release job produces.
"""
import re
import sys

PLATFORMS = ("darwin-arm64", "darwin-x64", "linux-arm64", "linux-x64")


def resolve_sha(sums: str, tag: str, platform: str) -> str:
    pattern = rf"^([0-9a-f]{{64}})\s+korat-cli-{re.escape(tag)}-{platform}\.tar\.gz$"
    match = re.search(pattern, sums, re.MULTILINE)
    if not match:
        sys.exit(f"SHA for korat-cli-{tag}-{platform}.tar.gz not found in SHA256SUMS:\n{sums}")
    return match.group(1)


def main() -> None:
    if len(sys.argv) != 4:
        sys.exit("usage: gen-tap-formula.py <tag> <sha256sums-path> <output-path>")
    tag, sums_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
    version = tag[1:] if tag.startswith("v") else tag
    sums = open(sums_path).read()
    sha = {p: resolve_sha(sums, tag, p) for p in PLATFORMS}
    base = f"https://github.com/korat-ai/homebrew-tap/releases/download/{tag}"

    formula = f'''class Korat < Formula
  desc "Korat MCP Hub CLI — local-first MCP server access through a managed relay"
  homepage "https://get.korat.ai"
  version "{version}"

  on_macos do
    on_arm do
      url "{base}/korat-cli-{tag}-darwin-arm64.tar.gz"
      sha256 "{sha['darwin-arm64']}"
    end
    on_intel do
      url "{base}/korat-cli-{tag}-darwin-x64.tar.gz"
      sha256 "{sha['darwin-x64']}"
    end
  end

  on_linux do
    on_arm do
      url "{base}/korat-cli-{tag}-linux-arm64.tar.gz"
      sha256 "{sha['linux-arm64']}"
    end
    on_intel do
      url "{base}/korat-cli-{tag}-linux-x64.tar.gz"
      sha256 "{sha['linux-x64']}"
    end
  end

  def install
    bin.install "Korat.Cli" => "korat"
  end

  test do
    assert_match "korat ", shell_output("#{{bin}}/korat version")
  end
end
'''

    # Guard against the historical breakages this script exists to prevent.
    # Both names: the source repository is korat-ai/korat-mcp-hub privately and
    # korat-ai/mcp-hub publicly, and "korat-mcp-hub" alone does not match the latter.
    for source_repo in ("korat-mcp-hub/releases", "korat-ai/mcp-hub/releases"):
        assert source_repo not in formula, "formula must use the distribution repository"
    assert "STUB" not in formula
    assert "0" * 64 not in formula
    open(out_path, "w").write(formula)


if __name__ == "__main__":
    main()
