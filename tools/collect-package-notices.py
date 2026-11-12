"""Collect installed dependency notices for the selected desktop package.

The Windows installer also carries Python notices for its bundled engine. Runtime and desktop package
licenses come from the local restore record for the selected operating system and architecture.
"""

import ast
import importlib.metadata
import json
from pathlib import Path
import re
import shutil
import sys

NOTICE_WORDS = ('license', 'licence', 'copying', 'notice')

# The MIT license text, for packages that state "MIT" in their manifest without shipping a license file.
MIT_TEXT = '''Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'''


def collect_python(app: Path, destination: Path, index: list[str]) -> int:
    from PyInstaller.archive.readers import CArchiveReader

    mapping = importlib.metadata.packages_distributions()
    names = set()
    archive = CArchiveReader(str(app / 'engine/EEEngine.exe')).open_embedded_archive('PYZ.pyz')
    for module in archive.toc:
        if not module.startswith('etherlighting_evolved'):
            names.update(mapping.get(module.split('.')[0], []))
    for name in sorted(names):
        dist = importlib.metadata.distribution(name)
        folder = destination / name
        folder.mkdir(exist_ok=True)
        notices = [file for file in dist.files or [] if any(word in file.name.lower() for word in NOTICE_WORDS)]
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
    return len(names)


def nuspec_field(text: str, tag: str) -> str:
    match = re.search(rf'<{tag}(?:\s[^>]*)?>([^<]*)</{tag}>', text)
    return match.group(1).strip() if match else ''


def collect_dotnet(assets: Path, destination: Path, index: list[str], runtime: str) -> int:
    record = json.loads(assets.read_text('utf-8'))
    packages_root = Path(record['packageFolders'] and next(iter(record['packageFolders'])))
    target = next((value for key, value in record['targets'].items() if key.endswith('/' + runtime)), None)
    if target is None:
        raise SystemExit(f'The desktop restore record has no {runtime} build target.')
    count = 0
    for key, package in sorted(target.items(), key=lambda item: item[0].lower()):
        if package.get('type') != 'package':
            continue
        name, version = key.split('/')
        folder = packages_root / record['libraries'][key]['path']
        files = [path for path in folder.iterdir() if path.is_file() and any(word in path.name.lower() for word in NOTICE_WORDS)]
        output = destination / 'dotnet' / name
        output.mkdir(parents=True, exist_ok=True)
        for number, path in enumerate(sorted(files), 1):
            shutil.copyfile(path, output / f'{number:02d}-{path.name}')
        nuspec = next(folder.glob('*.nuspec'), None)
        text = nuspec.read_text('utf-8') if nuspec else ''
        license_name = nuspec_field(text, 'license') or nuspec_field(text, 'licenseUrl')
        copyright_line = nuspec_field(text, 'copyright').replace('&amp;', '&')
        details = [f'{name} {version}', f'License: {license_name}', f'Copyright: {copyright_line}',
                   f"Project: {nuspec_field(text, 'projectUrl').replace('&amp;', '&')}"]
        (output / 'PACKAGE-NOTICE.txt').write_text('\n'.join(details) + '\n', encoding='utf-8')
        if not files:
            if license_name != 'MIT':
                raise SystemExit(f'{name} {version} ships no license file and is not MIT licensed. Review its license.')
            (output / 'LICENSE.txt').write_text(f'MIT License\n\n{copyright_line}\n\n{MIT_TEXT}', encoding='utf-8')
        index.append(f'{name} {version} ({license_name or "see notice"})')
        count += 1
    # The .NET runtime carried by the self-contained build is a runtime pack, listed with the downloads.
    framework = next(iter(record['project']['frameworks'].values()))
    for dependency in framework.get('downloadDependencies', []):
        if dependency['name'].lower() != f'microsoft.netcore.app.runtime.{runtime}':
            continue
        version = dependency['version'].strip('[]').split(',')[0].strip()
        folder = packages_root / dependency['name'].lower() / version
        for source, target_name in [('LICENSE.TXT', 'DotNet-LICENSE.txt'), ('THIRD-PARTY-NOTICES.TXT', 'DotNet-THIRD-PARTY-NOTICES.txt')]:
            if (folder / source).is_file():
                shutil.copyfile(folder / source, destination / target_name)
        index.append(f"{dependency['name']} {version} (MIT)")
    if not (destination / 'DotNet-LICENSE.txt').is_file():
        raise SystemExit('The .NET runtime license was not found in the restored packages.')
    return count


def collect(app: Path, assets: Path, client_runtime: str | None = None):
    destination = app / 'licenses'
    destination.mkdir(exist_ok=True)
    index = ['Third-party components']
    if client_runtime is None:
        index += ['', 'Engine (Python)', '']
        collect_python(app, destination, index)
    index += ['', 'Desktop program (.NET)', '']
    dotnet = collect_dotnet(assets, destination, index, client_runtime or 'win-x64')
    (destination / 'COMPONENTS.txt').write_text('\n'.join(index) + '\n', encoding='utf-8')
    print(f'Preserved notices for {dotnet} .NET packages and the .NET runtime.')


if __name__ == '__main__':
    collect(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), *sys.argv[3:])
