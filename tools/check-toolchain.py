"""Check that the tools building the Windows package are the versions CI uses.

Usage: python tools/check-toolchain.py <node.exe>

Compares this Python with .python-version, the Node runtime with .node-version, and the installed Python packages
with their pins in requirements-ci.txt.
"""

import importlib.metadata
import platform
import subprocess
import sys
from pathlib import Path

from packaging.requirements import Requirement

ROOT = Path(__file__).resolve().parent.parent


def pinned(path):
    for line in path.read_text("utf-8").replace("\\\n", " ").splitlines():
        spec = line.split(" --hash=")[0].strip()
        if spec and not spec.startswith("#"):
            requirement = Requirement(spec)
            if requirement.marker is None or requirement.marker.evaluate():
                yield requirement.name, str(requirement.specifier).removeprefix("==")


if __name__ == "__main__":
    problems = []
    python = (ROOT / ".python-version").read_text("utf-8").strip()
    if not (platform.python_version() + ".").startswith(python + "."):
        problems.append(f"Python {platform.python_version()} (pinned {python})")
    node = subprocess.check_output([sys.argv[1], "-p", "process.versions.node"], text=True).strip()
    expected_node = (ROOT / ".node-version").read_text("utf-8").strip()
    if node != expected_node:
        problems.append(f"Node {node} (pinned {expected_node})")
    for name, version in pinned(ROOT / "requirements-ci.txt"):
        try:
            installed = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError:
            installed = "missing"
        if installed != version:
            problems.append(f"{name} {installed} (pinned {version})")
    if problems:
        sys.exit("The build tools differ from the pinned versions: " + ", ".join(problems))
    print(f"Python {platform.python_version()}, Node {node} and the pinned packages match.")
