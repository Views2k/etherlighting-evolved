"""Set the application version in every file that records it.

Usage: python tools/set-version.py <major.minor.patch>

The files are listed in tools/check-repository.py, which checks that they agree. The first section of CHANGELOG.md
must describe the release: a "## Unreleased" heading becomes "## <version>".
"""

import re
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

if __name__ == "__main__":
    repository = runpy.run_path(str(ROOT / "tools" / "check-repository.py"))
    if len(sys.argv) != 2 or not re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        sys.exit(__doc__)
    version = sys.argv[1]
    changelog = (ROOT / "CHANGELOG.md").read_bytes().decode("utf-8")
    heading = re.search(r"^## (.+)$", changelog, re.M)
    if heading is None or heading.group(1) not in ("Unreleased", version):
        sys.exit('Describe the release under "## Unreleased" at the top of CHANGELOG.md first.')
    edits = {"CHANGELOG.md": (changelog, heading)}
    for name, pattern in repository["VERSION_FILES"].items():
        text = (ROOT / name).read_bytes().decode("utf-8")
        edits[name] = (text, re.search(pattern, text, re.M))
    if missing := [name for name, (_, match) in edits.items() if match is None]:
        sys.exit("No version found in " + ", ".join(missing))
    for name, (text, match) in edits.items():
        # Bytes keep each file's line endings as they are.
        (ROOT / name).write_bytes((text[:match.start(1)] + version + text[match.end(1):]).encode("utf-8"))
    repository["check_versions"](ROOT)
    print(f"Set version {version} in {len(edits)} files.")
