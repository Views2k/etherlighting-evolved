"""Pack a macOS or Linux build with Unix permissions, which Windows file systems do not record.

Usage: make_archive.py zip|tgz SOURCE_DIR ARCHIVE [EXECUTABLE ...]
The archive holds SOURCE_DIR itself for zip (an .app bundle) and its contents for tgz. Named executables and
every directory get mode 755; other files get 644.
"""

import sys
import tarfile
import time
import zipfile
from pathlib import Path


def main() -> None:
    kind, source, archive, *executables = sys.argv[1:]
    source, archive = Path(source).resolve(), Path(archive).resolve()
    names = set(executables)
    paths = sorted(source.rglob('*'))
    if kind == 'zip':
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
            for path in [source, *paths]:
                name = path.relative_to(source.parent).as_posix()
                if path.is_dir():
                    entry = zipfile.ZipInfo(name + '/', time.localtime(path.stat().st_mtime)[:6])
                    entry.external_attr = (0o40755 << 16) | 0x10
                    output.writestr(entry, b'')
                    continue
                entry = zipfile.ZipInfo(name, time.localtime(path.stat().st_mtime)[:6])
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = ((0o100755 if path.name in names else 0o100644) << 16)
                output.writestr(entry, path.read_bytes())
    elif kind == 'tgz':
        def mode(info: tarfile.TarInfo) -> tarfile.TarInfo:
            info.uid = info.gid = 0
            info.uname = info.gname = ''
            info.mode = 0o755 if info.isdir() or Path(info.name).name in names else 0o644
            return info
        with tarfile.open(archive, 'w:gz') as output:
            for path in paths:
                output.add(path, arcname=path.relative_to(source).as_posix(), recursive=False, filter=mode)
    else:
        raise SystemExit('Choose zip or tgz.')
    print(archive)


if __name__ == '__main__':
    main()
