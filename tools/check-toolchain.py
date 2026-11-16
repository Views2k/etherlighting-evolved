"""Check that the tools building the Windows package are the versions CI uses.

Usage: python tools/check-toolchain.py <node.exe>

Compares this Python with .python-version, the Node runtime with .node-version, and the Python packages that go into
the engine or build it with their pins in requirements-ci.txt. Those packages are the project's dependencies, its build
extra and everything they require; test and lint tools may differ.
"""

import importlib.metadata
import platform
import runpy
import subprocess
import sys
import tomllib
from pathlib import Path

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name

ROOT = Path(__file__).resolve().parent.parent


def package_versions():
    """Map each package the engine bundles or is built with to its installed version."""
    project = tomllib.loads((ROOT / "pyproject.toml").read_text("utf-8"))["project"]
    pending = [Requirement(spec) for spec in project["dependencies"] + project["optional-dependencies"]["build"]]
    versions, walked = {}, set()
    while pending:
        requirement = pending.pop()
        name = canonicalize_name(requirement.name)
        try:
            versions[name] = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError:
            versions[name] = "missing"
            continue
        for extra in ("", *requirement.extras):
            if (name, extra) not in walked:
                walked.add((name, extra))
                pending += [dependency for dependency in map(Requirement, importlib.metadata.requires(name) or [])
                            if dependency.marker is None or dependency.marker.evaluate({"extra": extra})]
    return versions


if __name__ == "__main__":
    problems = []
    python = (ROOT / ".python-version").read_text("utf-8").strip()
    if not (platform.python_version() + ".").startswith(python + "."):
        problems.append(f"Python {platform.python_version()} (pinned {python})")
    node = subprocess.check_output([sys.argv[1], "-p", "process.versions.node"], text=True).strip()
    expected_node = (ROOT / ".node-version").read_text("utf-8").strip()
    if node != expected_node:
        problems.append(f"Node {node} (pinned {expected_node})")
    pins = {canonicalize_name(requirement.name): str(requirement.specifier).removeprefix("==")
            for _, requirement in runpy.run_path(str(ROOT / "tools" / "hash-requirements.py"))["read_requirements"]()
            if requirement and (requirement.marker is None or requirement.marker.evaluate())}
    for name, installed in sorted(package_versions().items()):
        if installed != pins.get(name):
            problems.append(f"{name} {installed} (pinned {pins.get(name, 'nowhere')})")
    if problems:
        sys.exit("The build tools differ from the pinned versions: " + ", ".join(problems))
    print(f"Python {platform.python_version()}, Node {node} and the pinned packages match.")
