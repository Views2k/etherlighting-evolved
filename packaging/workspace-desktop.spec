import sys
from pathlib import Path

root = Path(SPECPATH).parent
sys.path.insert(0, str(root / "src"))
from etherlighting_evolved import __version__

version_file = Path(workpath) / "desktop-version.txt"
version_file.parent.mkdir(parents=True, exist_ok=True)
version_file.write_text(f"""VSVersionInfo(
 ffi=FixedFileInfo(filevers={tuple([int(p) for p in __version__.split('.')] + [0])},
 prodvers={tuple([int(p) for p in __version__.split('.')] + [0])}, mask=63, flags=0,
 OS=0x40004, fileType=1, subtype=0, date=(0,0)), kids=[
 StringFileInfo([StringTable('040904B0', [
 StringStruct('CompanyName','Views2k'), StringStruct('FileDescription','Etherlighting Evolved'),
 StringStruct('FileVersion','{__version__}'), StringStruct('ProductVersion','{__version__}'),
 StringStruct('ProductName','Etherlighting Evolved'),
 StringStruct('OriginalFilename','EtherlightingEvolved.exe'),
 StringStruct('LegalCopyright','Copyright (c) 2026 Views2k. MIT License.')])]),
 VarFileInfo([VarStruct('Translation',[1033,1200])])])""", encoding="utf-8")

a = Analysis([str(root / "packaging/workspace_launcher.py")], pathex=[str(root / "src")],
    datas=[(str(root / "src/etherlighting_evolved/assets"), "etherlighting_evolved/assets")],
    hiddenimports=["pystray._win32"],
    excludes=["tkinter", "pytest", "_pytest", "IPython", "etherlighting_evolved.api",
              "etherlighting_evolved.engine", "etherlighting_evolved.session"])
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="EtherlightingEvolved", console=False,
    icon=str(root / "src/etherlighting_evolved/assets/ee.ico"), version=str(version_file))
coll = COLLECT(exe, a.binaries, a.datas, name="EtherlightingEvolved")
