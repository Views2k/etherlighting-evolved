"""Check the source inventory and record the exact inputs used for verification."""

import argparse
import hashlib
import json
import re
import subprocess
import tomllib
from pathlib import Path


def inspect(root):
    def git(*args):
        return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()

    files = sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard").splitlines()))
    private_names = {
        "AGENTS.md",
        "CLAUDE.md",
        "handoff.md",
        "login.json",
        "controller-login.json",
        "private-access.json",
        "approved-device.json",
        "known_hosts",
        "ee.toml",
    }
    private_roots = {"outputs", "dist", "build", ".venv", "node_modules", "device-scenes", "output-ownership"}
    for name in files:
        path = Path(name)
        if path.parts[0] in private_roots or path.name in private_names or path.name.startswith(".env"):
            raise ValueError("Private or generated file is included in the source inventory: " + name)
        if not (root / path).is_file():
            raise ValueError("Missing source file: " + name)
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
    version = json.loads((root / "package.json").read_text("utf-8"))["version"]
    python = tomllib.loads((root / "pyproject.toml").read_text("utf-8"))["project"]["version"]
    module = (root / "src/etherlighting_evolved/__init__.py").read_text("utf-8")
    desktop = (root / "desktop/EtherlightingEvolved.Native/EtherlightingEvolved.Native.csproj").read_text("utf-8")
    if (python != version or f'__version__ = "{version}"' not in module
            or f"<Version>{version}</Version>" not in desktop):
        raise ValueError("Application version values must agree.")
    for name in files:
        if Path(name).suffix not in {".py", ".mjs", ".ps1", ".json", ".md", ".yml", ".toml"}:
            continue
        text = (root / name).read_text("utf-8")
        if re.search(r"C:[\\/]Users[\\/]|ChatGPT[\\/]Ether|outputs[\\/]audit-20261030", text, re.I):
            raise ValueError("A source file depends on a private workspace path: " + name)
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
