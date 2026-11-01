import argparse
import hashlib
import json
import marshal
import types
from pathlib import Path

from PyInstaller.archive.readers import CArchiveReader

workspace = Path(__file__).resolve().parent.parent
backend = workspace
parser = argparse.ArgumentParser(description="Verify a packaged WPF application against this checkout.")
parser.add_argument("release", type=Path, help="The output directory passed to native/build.ps1")
release = parser.parse_args().release.resolve()
package = release / "dist/EtherlightingEvolved"
archive = CArchiveReader(str(package / "engine/EEEngine.exe"))
pyz = archive.open_embedded_archive(next(name for name in archive.toc if name.endswith(".pyz")))


def normalize(value):
    if isinstance(value, types.CodeType):
        return value.replace(co_filename="<source>", co_consts=tuple(normalize(c) for c in value.co_consts))
    return value


modules = []
for name in pyz.toc:
    if name == "etherlighting_evolved" or name.startswith("etherlighting_evolved."):
        path = backend / "src" / name.replace(".", "/")
        path = path / "__init__.py" if path.is_dir() else path.with_suffix(".py")
        assert normalize(pyz.extract(name)) == normalize(compile(path.read_bytes(), str(path), "exec")), name
        modules.append(name)
assert "etherlighting_evolved.engine" in modules and "etherlighting_evolved.transport" in modules
launcher = marshal.loads(archive.extract("native_service"))
path = backend / "packaging/native_service.py"
assert normalize(launcher) == normalize(compile(path.read_bytes(), str(path), "exec"))
private = {"login.json", "ee.toml", "scene.json", "scene-demo.json", "known_switches.json", "desktop.json", "presets.json", "handoff.md", "agents.md", "port_led_fw.bin"}
for path in package.rglob("*"):
    assert path.name.lower() not in private, "Private data in package"
    assert path.suffix.lower() != ".pdb", "Debug symbols in package"
for name in ("EtherlightingEvolved.dll", "EtherlightingEvolved.exe"):
    path = package / name
    assert path.is_file()
    if path.suffix == ".dll":
        raw = path.read_bytes()
        assert "Lucida Calligraphy".encode("utf-16le") not in raw, "Obsolete font in native assembly"
        for word in ("ChatGPT", "Co-Authored-By", "Generated with"):
            assert word.encode("utf-16le") not in raw, "Authoring trace in assembly"
proof = {"engine_modules_verified": sorted(modules), "native_sha256": hashlib.sha256((package / "EtherlightingEvolved.dll").read_bytes()).hexdigest(), "files": {p.relative_to(package).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in package.rglob("*") if p.is_file()}}
(release / "package-provenance.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
print(f"Verified {len(modules)} engine modules, service entry, native metadata, and package exclusions.")
