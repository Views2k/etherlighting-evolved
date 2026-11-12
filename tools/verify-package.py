"""Compare a Windows workspace package with this checkout."""

import argparse
import hashlib
import json
import marshal
import re
import runpy
import subprocess
import sys
import tomllib
from pathlib import Path

from PyInstaller.archive.readers import CArchiveReader


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_contents(app):
    private = {'private-access.json', 'ee.toml', 'known_hosts', '.env', 'desktop.json', 'handoff.md',
               'agents.md', 'claude.md', 'login.json', 'scene.json', 'scene-demo.json', 'presets.json',
               'known_switches.json', 'port_led_fw.bin'}
    for item in app.rglob('*'):
        if item.name.lower() in private:
            raise ValueError('Private state or analysis artifact is included in the package.')
    for name in ['LICENSE.txt', 'licenses/Node-LICENSE.txt', 'licenses/Python-LICENSE.txt', 'licenses/COMPONENTS.txt',
                 'licenses/DotNet-LICENSE.txt', 'licenses/DotNet-THIRD-PARTY-NOTICES.txt']:
        if not (app / name).is_file() or not (app / name).stat().st_size:
            raise ValueError(f'Missing notice: {name}')
    for name in ['EtherlightingEvolved.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll',
                 'engine/EEEngine.exe', 'runtime/node.exe', 'preview/index.html']:
        path = app / name
        if not path.is_file() or not path.stat().st_size:
            raise ValueError(f'Missing application file: {name}')


def verify(root, app, node, installer=None):
    version = runpy.run_path(str(root / 'src/etherlighting_evolved/__init__.py'))['__version__']
    if json.loads((root / 'package.json').read_text('utf-8'))['version'] != version:
        raise ValueError('JavaScript and Python versions differ.')
    if tomllib.loads((root / 'pyproject.toml').read_text('utf-8'))['project']['version'] != version:
        raise ValueError('Python package versions differ.')
    native = (root / 'desktop/EtherlightingEvolved.Native/EtherlightingEvolved.Native.csproj').read_text('utf-8')
    if re.search(r'<Version>([^<]+)</Version>', native).group(1) != version:
        raise ValueError('Desktop program and Python versions differ.')
    if b'PYZ-00.pyz' in (app / 'EtherlightingEvolved.exe').read_bytes()[-4_000_000:]:
        raise ValueError('The desktop program is the earlier Python build.')
    modules, sources = {}, {}
    for relative, entry in [('engine/EEEngine.exe', 'native_service')]:
        executable = CArchiveReader(str(app / relative))
        archive = executable.open_embedded_archive('PYZ.pyz')
        checked = []
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
            checked.append(name)
        required = 'etherlighting_evolved.engine'
        if required not in checked:
            raise ValueError(f'Required module missing: {required}')
        modules[relative] = sorted(checked)
        source = root / 'packaging' / f'{entry}.py'
        packaged = marshal.loads(executable.extract(entry))
        if packaged != compile(source.read_bytes(), packaged.co_filename, 'exec'):
            raise ValueError(f'Packaged entry differs: {entry}')
        sources[source.relative_to(root).as_posix()] = digest(source)

    assets = 0
    for folder in ['preview', 'src', 'assets']:
        for source in (root / folder).iterdir():
            extensions = {'.mjs', '.css', '.html', '.png', '.svg', '.ico', '.webmanifest'}
            if not source.is_file() or source.suffix not in extensions:
                continue
            relative = source.relative_to(root)
            packaged = app / relative
            if digest(source) != digest(packaged):
                raise ValueError(f'Packaged asset differs: {relative.as_posix()}')
            markers = r'ChatGPT|OpenAI|Codex|Claude|Generated with|Co-Authored-By|C:\\Users'
            if (source.suffix in {'.mjs', '.css', '.html', '.svg', '.webmanifest'}
                    and re.search(markers, packaged.read_text('utf-8'), re.I)):
                raise ValueError(f'Unexpected authoring metadata: {relative.as_posix()}')
            sources[relative.as_posix()] = digest(source)
            assets += 1
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
              'modules_match_source': modules, 'web_assets_match_source': assets,
              'desktop_sha256': digest(app / 'EtherlightingEvolved.exe'),
              'engine_sha256': digest(app / 'engine/EEEngine.exe'), 'node_sha256': digest(node),
              'source_hashes': dict(sorted(sources.items()))}
    if installer:
        result.update(installer_sha256=digest(installer), installer_bytes=installer.stat().st_size)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--node', type=Path)
    parser.add_argument('--installer', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--check-contents', action='store_true')
    args = parser.parse_args()
    if args.check_contents:
        check_contents(args.app.resolve())
        sys.exit(0)
    if not args.node or not args.output:
        parser.error('--node and --output are required for release verification')
    result = verify(Path(__file__).resolve().parents[1], args.app.resolve(), args.node.resolve(), args.installer)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key != 'source_hashes'}, indent=2))
