import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_submodules

root = Path(SPECPATH).parent
sys.path.insert(0, str(root / "src"))
from etherlighting_evolved import __version__  # noqa: E402

numbers = [int(part) for part in __version__.split(".")[:3]] + [0]
version_file = Path(workpath) / "version_info.txt"
version_file.parent.mkdir(parents=True, exist_ok=True)
version_file.write_text(f"""VSVersionInfo(
  ffi=FixedFileInfo(filevers={tuple(numbers)}, prodvers={tuple(numbers)}, mask=0x3f, flags=0x0, OS=0x40004,
                    fileType=0x1, subtype=0x0, date=(0, 0)),
  kids=[
    StringFileInfo([StringTable('040904B0', [
      StringStruct('CompanyName', 'Views2k'),
      StringStruct('FileDescription', 'Etherlighting Evolved'),
      StringStruct('FileVersion', '{__version__}'),
      StringStruct('InternalName', 'EEEngine'),
      StringStruct('LegalCopyright', 'Copyright (c) 2026 Views2k. MIT License. Not affiliated with Ubiquiti Inc.'),
      StringStruct('OriginalFilename', 'EEEngine.exe'),
      StringStruct('ProductName', 'Etherlighting Evolved'),
      StringStruct('ProductVersion', '{__version__}')])]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
""", encoding="utf-8")

a = Analysis(
    [str(root / "packaging" / "native_service.py")],
    pathex=[str(root / "src")],
    datas=[],
    hiddenimports=collect_submodules("uvicorn"),
    excludes=["tkinter", "pytest", "_pytest", "IPython", "webview", "pystray"],
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz, a.scripts, [], exclude_binaries=True, name="EEEngine", console=True,
    icon=str(root / "src" / "etherlighting_evolved" / "assets" / "ee.ico"), version=str(version_file),
)
coll = COLLECT(exe, a.binaries, a.datas, name="EEEngine")
