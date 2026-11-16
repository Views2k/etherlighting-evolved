"""Write the sha256 of every pinned wheel from PyPI into requirements-ci.txt.

Usage: python tools/hash-requirements.py

Each pin keeps its version and marker and lists the hashes of the wheels that the Python version in .python-version
can install on the CI runners: Windows x64, macOS arm64 and Linux x64. Run it after changing a pin.
"""

import json
import urllib.request
from pathlib import Path

from packaging.requirements import Requirement
from packaging.tags import compatible_tags, cpython_tags, mac_platforms
from packaging.utils import parse_wheel_filename

ROOT = Path(__file__).resolve().parent.parent
REQUIREMENTS = ROOT / "requirements-ci.txt"


def runner_tags():
    python = tuple(int(part) for part in (ROOT / ".python-version").read_text("utf-8").split("."))[:2]
    platforms = ["win_amd64", *mac_platforms((15, 0), "arm64"),
                 *(f"manylinux_2_{minor}_x86_64" for minor in range(39, 4, -1)),
                 "manylinux2014_x86_64", "manylinux2010_x86_64", "manylinux1_x86_64"]
    return {*cpython_tags(python, platforms=platforms), *compatible_tags(python, platforms=platforms)}


def wheel_hashes(requirement, tags):
    version = str(requirement.specifier).removeprefix("==")
    request = urllib.request.Request(f"https://pypi.org/pypi/{requirement.name}/{version}/json",
                                     headers={"User-Agent": "requirements-hasher"})
    with urllib.request.urlopen(request, timeout=30) as response:
        files = json.load(response)["urls"]
    hashes = sorted(item["digests"]["sha256"] for item in files
                    if item["filename"].endswith(".whl") and tags & parse_wheel_filename(item["filename"])[3])
    if not hashes:
        raise SystemExit(f"{requirement.name} {version} has no wheel for the CI runners.")
    return hashes


if __name__ == "__main__":
    tags = runner_tags()
    output, pins = [], 0
    for line in REQUIREMENTS.read_text("utf-8").replace("\\\n", " ").splitlines():
        spec = line.split(" --hash=")[0].strip()
        if not spec or spec.startswith("#"):
            output.append(line)
            continue
        hashes = wheel_hashes(Requirement(spec), tags)
        output.append(" \\\n    ".join([spec, *(f"--hash=sha256:{value}" for value in hashes)]))
        pins += 1
    REQUIREMENTS.write_text("\n".join(output) + "\n", encoding="utf-8", newline="\n")
    print(f"Hashed {pins} pins.")
