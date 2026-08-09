#!/usr/bin/env python3
"""Extract ILLink trim warnings from dotnet publish output.

Reads dotnet publish stdout/stderr from stdin, extracts IL-prefixed trim
warnings, and emits a stable sorted JSON array suitable for baseline diffing.

Usage:
    dotnet publish ... 2>&1 | python3 scripts/extract-trim-warnings.py > baseline.json
    python3 scripts/extract-trim-warnings.py < publish.log > baseline.json
"""

import json
import re
import sys

# File paths and the csproj path inside messages are normalized to repo-relative by anchoring
# on the 'apps/' segment — NOT by stripping hardcoded prefixes. The build can run anywhere
# (local, GHA linux /home/runner/work/..., GHA macOS /Users/runner/work/..., container
# /__w/...), and the baseline must be byte-identical across all of them.
_CSPROJ_PATH_RE = re.compile(r"\[[^\]]*?(apps/[^\]]+)\]")


def _rel_file(path: str) -> str:
    """Repo-relative file path regardless of where the build ran."""
    idx = path.find("apps/")
    return path[idx:] if idx != -1 else path

# Matches: /abs/path/to/File.cs(123,45): warning IL2026: Some message here
_FILE_WARNING_RE = re.compile(
    r"^(?P<file>[^()]+)\((?P<line>\d+),(?P<col>\d+)\)"
    r"\s*:\s*warning\s+(?P<id>IL\d+)\s*:\s*(?P<msg>.+)$"
)

# Matches: ILLink : Trim analysis warning IL2026: System.Foo.Bar() ...
_ILLINK_WARNING_RE = re.compile(
    r"^ILLink\s*:.*warning\s+(?P<id>IL\d+)\s*:\s*(?P<msg>.+)$"
)


def _strip_abs(text: str) -> str:
    """Strip the absolute prefix from the bracketed csproj path in a message so the baseline
    is machine-independent (e.g. '[/abs/.../apps/Korat.Cli/Korat.Cli.csproj]' → '[apps/Korat.Cli/Korat.Cli.csproj]')."""
    return _CSPROJ_PATH_RE.sub(r"[\1]", text)


def extract_warnings(lines):
    warnings = []
    seen = set()

    for raw in lines:
        line = raw.rstrip("\n").rstrip("\r")

        m = _FILE_WARNING_RE.match(line)
        if m:
            raw_file = m.group("file").strip()
            rel_file = _rel_file(raw_file)
            msg = _strip_abs(m.group("msg").strip())
            entry = {
                "warning_id": m.group("id"),
                "file": rel_file,
                "line": int(m.group("line")),
                "column": int(m.group("col")),
                "message": msg,
            }
            key = (entry["warning_id"], entry["file"], entry["line"], entry["column"])
            if key not in seen:
                seen.add(key)
                warnings.append(entry)
            continue

        m = _ILLINK_WARNING_RE.match(line)
        if m:
            msg = _strip_abs(m.group("msg").strip())
            entry = {
                "warning_id": m.group("id"),
                "file": "",
                "line": 0,
                "column": 0,
                "message": msg,
            }
            key = (entry["warning_id"], entry["file"], entry["line"], entry["column"],
                   entry["message"])
            if key not in seen:
                seen.add(key)
                warnings.append(entry)

    # Sort for stable diff: warning_id first, then file path, then line number.
    warnings.sort(key=lambda w: (w["warning_id"], w["file"], w["line"]))
    return warnings


def main():
    warnings = extract_warnings(sys.stdin)
    print(json.dumps(warnings, indent=2))


if __name__ == "__main__":
    main()
