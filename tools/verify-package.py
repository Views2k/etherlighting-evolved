"""Compare a Windows workspace package, or the folder it was installed to, with this checkout."""

import argparse
import hashlib
import json
import marshal
import runpy
import subprocess
from pathlib import Path

from PyInstaller.archive.readers import CArchiveReader

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = runpy.run_path(str(ROOT / 'tools' / 'check-repository.py'))
# The programs EE installs on the switch, as PyInstaller places the engine's data files.
RESIDENT = 'engine/_internal/etherlighting_evolved/resident'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_contents(app):
    private = {name.lower() for name in REPOSITORY['PRIVATE_NAMES']}
    for item in app.rglob('*'):
        if item.name.lower() in private:
            raise ValueError('Private state or analysis artifact is included in the package.')
    for name in ['LICENSE.txt', 'licenses/Node-LICENSE.txt', 'licenses/Python-LICENSE.txt', 'licenses/COMPONENTS.txt',
                 'licenses/DotNet-LICENSE.txt', 'licenses/DotNet-THIRD-PARTY-NOTICES.txt']:
        if not (app / name).is_file() or not (app / name).stat().st_size:
            raise ValueError(f'Missing notice: {name}')
    for name in ['EtherlightingEvolved.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll',
                 'engine/EEEngine.exe', 'runtime/node.exe', 'preview/index.html', 'tools/approve-workspace-device.mjs',
                 f'{RESIDENT}/fans.sh', f'{RESIDENT}/player.sh', f'{RESIDENT}/frames.awk']:
        path = app / name
        if not path.is_file() or not path.stat().st_size:
            raise ValueError(f'Missing application file: {name}')


def verify(root, app, node, installer=None):
    version = REPOSITORY['check_versions'](root)
    try:
        CArchiveReader(str(app / 'EtherlightingEvolved.exe'))
    except Exception:
        pass  # Not a PyInstaller program: the native desktop program.
    else:
        raise ValueError('The desktop program is the earlier Python build.')
    sources = {}
    executable = CArchiveReader(str(app / 'engine/EEEngine.exe'))
    archive = executable.open_embedded_archive('PYZ.pyz')
    modules = []
    for name in archive.toc:
        if name != 'etherlighting_evolved' and not name.startswith('etherlighting_evolved.'):
            continue
        source = (root / 'src').joinpath(*name.split('.')).with_suffix('.py')
        if not source.is_file():
            source = (root / 'src').joinpath(*name.split('.'), '__init__.py')
        packaged = archive.extract(name)
        if packaged != compile(source.read_bytes(), packaged.co_filename, 'exec'):
            raise ValueError(f'Packaged module differs: {name}')
        sources[source.relative_to(root).as_posix()] = digest(source)
        modules.append(name)
    required = 'etherlighting_evolved.engine'
    if required not in modules:
        raise ValueError(f'Required module missing: {required}')
    source = root / 'packaging/native_service.py'
    packaged = marshal.loads(executable.extract('native_service'))
    if packaged != compile(source.read_bytes(), packaged.co_filename, 'exec'):
        raise ValueError('Packaged entry differs: native_service')
    sources[source.relative_to(root).as_posix()] = digest(source)

    resident = root / 'src/etherlighting_evolved/resident'
    programs = sorted(path.name for path in resident.iterdir() if path.is_file())
    if sorted(path.name for path in (app / RESIDENT).iterdir()) != programs:
        raise ValueError('Packaged switch programs differ from the source folder.')
    for name in programs:
        source = resident / name
        if digest(app / RESIDENT / name) != digest(source):
            raise ValueError(f'Packaged switch program differs: {name}')
        sources[source.relative_to(root).as_posix()] = digest(source)

    assets = 0
    for folder in ['preview', 'src', 'assets']:
        for source in (root / folder).iterdir():
            extensions = {'.mjs', '.css', '.html', '.png', '.svg', '.ico', '.webmanifest'}
            if not source.is_file() or source.suffix not in extensions or source.name.endswith('.test.mjs'):
                continue
            relative = source.relative_to(root)
            packaged = app / relative
            if digest(source) != digest(packaged):
                raise ValueError(f'Packaged asset differs: {relative.as_posix()}')
            if source.suffix in {'.mjs', '.css', '.html', '.svg', '.webmanifest'}:
                text = packaged.read_text('utf-8')
                if REPOSITORY['AI_MARKER'].search(text) or REPOSITORY['HOME_PATH'].search(text):
                    raise ValueError(f'Unexpected authoring metadata: {relative.as_posix()}')
            sources[relative.as_posix()] = digest(source)
            assets += 1
    helper = 'tools/approve-workspace-device.mjs'
    sources[helper] = digest(root / helper)
    if digest(app / helper) != sources[helper]:
        raise ValueError('Bundled device approval tool differs from the checked source.')
    check_contents(app)
    if digest(app / 'runtime/node.exe') != digest(node):
        raise ValueError('Bundled Node differs from the selected runtime.')
    try:
        git = ['git', '-C', str(root)]
        revision = subprocess.check_output(git + ['rev-parse', 'HEAD'], text=True, stderr=subprocess.DEVNULL).strip()
        dirty = bool(subprocess.check_output(git + ['status', '--porcelain', '--untracked-files=all'], text=True))
    except (OSError, subprocess.CalledProcessError) as exc:
        raise ValueError('Source Git provenance could not be verified.') from exc
    result = {'passed': True, 'version': version, 'git_revision': revision, 'working_tree_dirty': dirty,
              'modules_match_source': {'engine/EEEngine.exe': sorted(modules)},
              'resident_files_match_source': programs, 'web_assets_match_source': assets,
              'desktop_sha256': digest(app / 'EtherlightingEvolved.exe'),
              'engine_sha256': digest(app / 'engine/EEEngine.exe'), 'node_sha256': digest(node),
              'source_hashes': dict(sorted(sources.items()))}
    if installer:
        result.update(installer_sha256=digest(installer), installer_bytes=installer.stat().st_size)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--node', type=Path, required=True)
    parser.add_argument('--installer', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = verify(ROOT, args.app.resolve(), args.node.resolve(), args.installer)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'source_hashes'}, indent=2))
