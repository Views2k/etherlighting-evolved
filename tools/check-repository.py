"""Check the source inventory and record the exact inputs used for verification.

tools/verify-package.py and tools/set-version.py load the lists and patterns defined here.
"""

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

# Local notes, credentials and application state. .gitignore must keep every one of them out of the repository.
PRIVATE_NAMES = {
    "AGENTS.md", "CLAUDE.md", "handoff.md", ".env", "ee.toml", "login.json", "controller-login.json",
    "private-access.json", "approved-device.json", "known_hosts", "known_switches.json", "desktop.json",
    "native.json", "preferences.json", "scene.json", "scene-demo.json", "scene-history.json", "presets.json",
    "schedules.json", "switch-connection-intent.json", "switch-playback.json", "lighting-library.json",
    "fans.json", "fans-demo.json", "last-session.json", "port_led_fw.bin",
}
PRIVATE_ROOTS = {"outputs", "dist", "build", ".venv", "node_modules", "device-scenes", "device-fans",
                 "output-ownership", "port-names"}

VERSION_FILES = {
    "pyproject.toml": r'^version = "([^"]+)"$',
    "package.json": r'^  "version": "([^"]+)",$',
    "src/etherlighting_evolved/__init__.py": r'^__version__ = "([^"]+)"$',
    "desktop/EtherlightingEvolved.Native/EtherlightingEvolved.Native.csproj": r"^    <Version>([^<]+)</Version>$",
    "packaging/installer.iss": r'^  #define AppVersion "([^"]+)"$',
}
# The newest released section of the changelog. An "Unreleased" section may sit above it.
CHANGELOG_RELEASE = r"^## (\d+\.\d+\.\d+)$"

HOME_PATH = re.compile(r"[A-Za-z]:[\\/]Users[\\/]|/(?:Users|home)/[A-Za-z][\w.-]*/")
LAN_ADDRESS = re.compile(r"\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b")
EXAMPLE_ADDRESSES = {"192.168.1.2", "10.0.0.1"}
TAILNET_NAME = re.compile(r"([\w-]+)\.ts\.net\b")
EXAMPLE_TAILNETS = {"example", "fixture-tailnet"}
AI_MARKER = re.compile(
    r"\b(?:chatgpt|openai|anthropic|claude|codex|copilot)\b|co-authored-by|generated with|worktree-agent", re.I)
RELEASE_NAME = re.compile(r"\bEE (\d+\.\d+\.\d+)\b")
# Files that must name the markers, private files or allowed text to look for them.
GUARD_FILES = {"tools/check-repository.py", ".gitignore"}
# Text that is accepted where it stands. Each entry is a file and the exact matched text.
ALLOWED: set[tuple[str, str]] = set()


def read_versions(root):
    versions = {}
    for name, pattern in VERSION_FILES.items():
        match = re.search(pattern, (root / name).read_text("utf-8"), re.M)
        versions[name] = match.group(1) if match else None
    release = re.search(CHANGELOG_RELEASE, (root / "CHANGELOG.md").read_text("utf-8"), re.M)
    versions["CHANGELOG.md"] = release.group(1) if release else None
    return versions


def check_versions(root):
    versions = read_versions(root)
    if len(set(versions.values())) != 1:
        raise ValueError("Application version values must agree: "
                         + ", ".join(f"{name} {value}" for name, value in versions.items()))
    return versions["package.json"]


def find_problems(name, text, version):
    problems = []

    def report(match, description):
        if (name, match.group()) not in ALLOWED:
            line = text.count("\n", 0, match.start()) + 1
            problems.append(f"{name}:{line}: {description}: {match.group()}")

    for match in HOME_PATH.finditer(text):
        report(match, "home folder path")
    if not name.startswith("tests/fixtures/"):
        for match in LAN_ADDRESS.finditer(text):
            if match.group() not in EXAMPLE_ADDRESSES:
                report(match, "LAN address")
    for match in TAILNET_NAME.finditer(text):
        if match.group(1) not in EXAMPLE_TAILNETS:
            report(match, "tailnet name")
    if name in GUARD_FILES:
        return problems
    for match in AI_MARKER.finditer(text):
        report(match, "assistant reference")
    if name != "CHANGELOG.md":
        for match in RELEASE_NAME.finditer(text):
            if match.group(1) != version:
                report(match, "earlier version")
    return problems


def inspect(root):
    def git(*args):
        return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()

    files = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard").splitlines()))
    for name in files:
        path = Path(name)
        if path.parts[0] in PRIVATE_ROOTS or path.name in PRIVATE_NAMES or path.name.startswith(".env"):
            raise ValueError("Private or generated file is included in the source inventory: " + name)
        if not (root / path).is_file():
            raise ValueError("Missing source file: " + name)
    expected = sorted(PRIVATE_NAMES) + [folder + "/" for folder in sorted(PRIVATE_ROOTS)]
    # check-ignore lists the ignored paths and exits with 1 when there are none.
    ignored = subprocess.run(["git", "-C", str(root), "check-ignore", "--no-index", "--", *expected], text=True,
                             capture_output=True).stdout.splitlines()
    if missing := [name for name in expected if name not in ignored]:
        raise ValueError(".gitignore does not exclude: " + ", ".join(missing))
    for name in [
        "preview/server.mjs",
        "preview/app.mjs",
        "src/etherlighting_evolved/engine.py",
        "packaging/build.ps1",
        "tools/verify-package.py",
        "tools/smoke-workspace.mjs",
        "docs/DEVELOPMENT.md",
        "docs/WORKFLOW.md",
        ".github/workflows/quality.yml",
    ]:
        if name not in files:
            raise ValueError("Required repository entry is missing: " + name)
    version = check_versions(root)
    problems = []
    for name in files:
        data = (root / name).read_bytes()
        # Git's own test: a file with a NUL byte in its first 8000 bytes is binary.
        if b"\0" not in data[:8000]:
            problems += find_problems(name, data.decode("utf-8"), version)
    if problems:
        raise ValueError("Private or stale text in the source files:\n" + "\n".join(problems))
    return {
        "version": version,
        "revision": git("rev-parse", "HEAD"),
        "working_tree_dirty": bool(git("status", "--porcelain")),
        "source_hashes": {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in files},
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    manifest = inspect(Path(__file__).resolve().parent.parent)
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Verified {len(manifest['source_hashes'])} source files for {manifest['version']}.")
