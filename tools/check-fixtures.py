"""Check that the generated files the browser and desktop tests compare against match the current engine.

Each generator writes into a temporary copy of the checkout layout, and every file it writes must equal the
checked-in one byte for byte:

    tools/preview_fixtures.py <checkout> tests/fixtures
    tools/live_traffic_fixtures.py <checkout> tests/fixtures
    tools/profile_parity_fixtures.py <checkout> tests/fixtures
    tools/extract_catalog.py src/etherlighting_evolved/effects/library.py src/catalog.mjs

Run the same commands from the checkout, with . as <checkout>, to refresh the files after an engine change. Rounding
of some floating-point results can differ between platforms, and the checked-in files are generated on Windows.
tests/fixtures/fixed_effects.json and pre_color_brightness.json have no generator: they record earlier engine output
on purpose and must not be regenerated.
"""

import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def regenerate(folder):
    def run(script, *arguments):
        subprocess.run([sys.executable, str(ROOT / "tools" / script), *map(str, arguments)], check=True)

    for script in ("preview_fixtures.py", "live_traffic_fixtures.py", "profile_parity_fixtures.py"):
        run(script, ROOT, folder / "tests/fixtures")
    (folder / "src").mkdir()
    run("extract_catalog.py", ROOT / "src/etherlighting_evolved/effects/library.py", folder / "src/catalog.mjs")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as temporary:
        folder = Path(temporary)
        regenerate(folder)
        generated = sorted(path.relative_to(folder).as_posix() for path in folder.rglob("*") if path.is_file())
        stale = [name for name in generated if (folder / name).read_bytes() != (ROOT / name).read_bytes()]
    if stale:
        sys.exit("Regenerate these files with their generators (see tools/check-fixtures.py): " + ", ".join(stale))
    print(f"{len(generated)} generated files match the current engine.")
