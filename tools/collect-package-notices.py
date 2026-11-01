"""Collect installed dependency notices for the modules in the Windows package."""

import ast
import importlib.metadata
from pathlib import Path
import shutil
import sys

from PyInstaller.archive.readers import CArchiveReader


def collect(app: Path):
    mapping = importlib.metadata.packages_distributions()
    names = set()
    for relative in ['EtherlightingEvolved.exe', 'engine/EEEngine.exe']:
        archive = CArchiveReader(str(app / relative)).open_embedded_archive('PYZ.pyz')
        for module in archive.toc:
            if not module.startswith('etherlighting_evolved'):
                names.update(mapping.get(module.split('.')[0], []))
    destination = app / 'licenses'
    destination.mkdir(exist_ok=True)
    index = ['Third-party components', '']
    for name in sorted(names):
        dist = importlib.metadata.distribution(name)
        folder = destination / name
        folder.mkdir(exist_ok=True)
        notices = [file for file in dist.files or []
                   if any(word in file.name.lower() for word in ('license', 'licence', 'copying', 'notice'))]
        index.append(f'{name} {dist.version}')
        for number, file in enumerate(notices, 1):
            shutil.copyfile(dist.locate_file(file), folder / f'{number:02d}-{file.name}')
        if not notices:
            details = [f'{name} {dist.version}', f"License metadata: {dist.metadata.get('License', '')}"]
            for file in dist.files or []:
                if file.suffix == '.py':
                    doc = ast.get_docstring(ast.parse(dist.locate_file(file).read_bytes()))
                    if doc and any(word in doc.lower() for word in ('copyright', 'license')):
                        details.append(doc)
            (folder / 'SOURCE-NOTICE.txt').write_text('\n\n'.join(details) + '\n', encoding='utf-8')
    shutil.copyfile(Path(sys.base_prefix) / 'LICENSE.txt', destination / 'Python-LICENSE.txt')
    (destination / 'COMPONENTS.txt').write_text('\n'.join(index) + '\n', encoding='utf-8')
    print(f'Preserved notices for {len(names)} installed dependencies and Python.')


if __name__ == '__main__':
    collect(Path(sys.argv[1]).resolve())
