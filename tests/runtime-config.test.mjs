import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, copyFile, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {resolveRuntimeConfig} from '../preview/runtime-config.mjs';

const exec = promisify(execFile);
async function fixture(context) {
  const base = await mkdtemp(path.join(tmpdir(), 'ee-runtime-config-'));
  context.after(() => rm(base, {recursive: true, force: true}));
  const root = path.join(base, 'App with spaces'), home = path.join(base, 'Fresh user');
  async function file(relative, content = '') {
    const destination = path.join(root, relative);
    await mkdir(path.dirname(destination), {recursive: true});
    await writeFile(destination, content);
    return destination;
  }
  await file('preview/index.html', '<title>Workspace fixture</title>');
  const sourceRoot = root;
  async function source() {
    await file('packaging/native_service.py');
    await file('src/etherlighting_evolved/__init__.py');
    return file('.venv/Scripts/python.exe');
  }
  return {base, root, home, file, sourceRoot, source, config: env => resolveRuntimeConfig({root, home, platform: 'win32', env})};
}

test('development retains preview storage and uses only its adjacent environment', async context => {
  const f = await fixture(context), python = await f.source();
  const result = f.config({EE_PREVIEW_LIVE: '1', USERPROFILE: path.join(f.base, 'Other user')});
  assert.equal(result.command, python);
  assert.deepEqual(result.args, [path.join(f.sourceRoot, 'packaging', 'native_service.py')]);
  assert.equal(result.cwd, f.sourceRoot);
  assert.equal(result.env.PYTHONPATH, path.join(f.sourceRoot, 'src'));
  assert.equal(result.directory, path.join(f.root, 'outputs', 'browser-preview-data'));
  assert.equal(result.live, true);
  assert.equal(result.packaged, false);
  assert.equal(result.port, 4180);
  assert.deepEqual(result.serviceOptions, {demo: false, browser_live: true, data_dir: result.directory});
});

test('packaged engine uses per-user writable storage without a source tree or Python', async context => {
  const f = await fixture(context), engine = await f.file('engine/EEEngine.exe');
  const localData = path.join(f.base, 'App data');
  const result = f.config({LOCALAPPDATA: localData});
  assert.equal(result.command, engine);
  assert.deepEqual(result.args, []);
  assert.equal(result.cwd, f.root);
  assert.equal(result.sourceRoot, null);
  assert.equal(result.packaged, true);
  assert.equal(result.live, true);
  assert.equal(result.directory, path.join(localData, 'Etherlighting Evolved', 'workspace'));
  assert.equal(result.env.PYTHONPATH, undefined);
});

test('explicit engine and data paths do not require a source checkout', async context => {
  const f = await fixture(context), engine = await f.file('custom engine.exe');
  const data = path.join(f.base, 'State');
  const result = f.config({EE_ENGINE_EXE: engine, EE_WORKSPACE_DATA_DIR: data, EE_PREVIEW_PORT: '4186', EE_PREVIEW_LIVE: '0'});
  assert.equal(result.command, engine);
  assert.equal(result.directory, data);
  assert.equal(result.port, 4186);
  assert.deepEqual(result.serviceOptions, {demo: true, browser_live: false, data_dir: data});
});

test('development source and Python overrides remain explicit and validated', async context => {
  const f = await fixture(context);
  const sourceRoot = path.join(f.root, 'custom source');
  await f.file('custom source/packaging/native_service.py');
  await f.file('custom source/src/etherlighting_evolved/__init__.py');
  const python = await f.file('tools/python.exe');
  const result = f.config({EE_ENGINE_SOURCE_ROOT: sourceRoot, EE_PREVIEW_PYTHON: python});
  assert.equal(result.command, python);
  assert.equal(result.cwd, sourceRoot);
  assert.equal(result.live, false);
  assert.equal(result.env.PYTHONPATH, path.join(sourceRoot, 'src'));
  assert.throws(() => f.config({EE_ENGINE_SOURCE_ROOT: sourceRoot, EE_PREVIEW_PYTHON: path.join(f.root, 'missing.exe')}), {code: 'runtime_python_missing'});
});

test('development may use an installed Python only from an absolute PATH entry', async context => {
  const f = await fixture(context);
  await f.file('packaging/native_service.py');
  await f.file('src/etherlighting_evolved/__init__.py');
  const python = await f.file('installed tools/python.exe');
  assert.equal(f.config({Path: `.;relative;"${path.dirname(python)}"`}).command, python);
  assert.throws(() => f.config({Path: '.;relative'}), {code: 'runtime_python_missing'});
});

test('explicit missing engine and packaged mode fail instead of falling back to development', async context => {
  const f = await fixture(context);
  await f.source();
  assert.throws(() => f.config({EE_RUNTIME_MODE: 'packaged'}), {code: 'runtime_engine_missing'});
  assert.throws(() => f.config({EE_ENGINE_EXE: path.join(f.root, 'missing.exe')}), {code: 'runtime_engine_missing'});
  assert.throws(() => f.config({EE_ENGINE_EXE: f.root}), {code: 'runtime_engine_missing'});
});

test('a frozen development fixture can retain isolated preview data explicitly', async context => {
  const f = await fixture(context);
  await f.file('engine/EEEngine.exe');
  const result = f.config({EE_RUNTIME_MODE: 'development'});
  assert.equal(result.packaged, false);
  assert.equal(result.live, false);
  assert.equal(result.directory, path.join(f.root, 'outputs', 'browser-preview-data'));
});

test('child environment cannot inherit switch credentials, tokens or Python injection paths', async context => {
  const f = await fixture(context);
  await f.file('engine/EEEngine.exe');
  const keys = ['EE_HOST', 'EE_USERNAME', 'EE_PASSWORD', 'EE_KEY_FILE', 'EE_API_TOKEN', 'EE_DATA_DIR', 'PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP'];
  const env = Object.fromEntries(keys.map(key => [key, 'fixture-only']));
  const result = f.config(env);
  for (const key of keys) {
    assert.equal(result.env[key], undefined, key);
    assert.equal(env[key], 'fixture-only', 'caller environment must remain unchanged');
  }
});

test('invalid modes, ports and relative override paths reject before starting anything', async context => {
  const f = await fixture(context);
  await f.file('engine/EEEngine.exe');
  for (const value of ['0', '-1', '65536', '4180x', '4.5', '']) {
    assert.throws(() => f.config({EE_PREVIEW_PORT: value}), {code: 'runtime_port_invalid'});
  }
  assert.throws(() => f.config({EE_PREVIEW_LIVE: 'yes'}), {code: 'runtime_live_invalid'});
  assert.throws(() => f.config({EE_RUNTIME_MODE: 'auto'}), {code: 'runtime_mode_invalid'});
  for (const key of ['EE_ENGINE_EXE', 'EE_ENGINE_SOURCE_ROOT', 'EE_WORKSPACE_DATA_DIR']) {
    assert.throws(() => f.config({[key]: 'relative'}), {code: 'runtime_path_invalid'});
  }
});

test('packaged workspace fallback is under the supplied user home, never the install root', async context => {
  const f = await fixture(context);
  await f.file('engine/EEEngine.exe');
  assert.equal(f.config({}).directory, path.join(f.home, 'AppData', 'Local', 'Etherlighting Evolved', 'workspace'));
});

test('missing web assets fail with a fixed error that does not reveal a local path', async context => {
  const f = await fixture(context);
  assert.throws(() => resolveRuntimeConfig({root: f.base, env: {}}), error => {
    assert.equal(error.code, 'runtime_assets_missing');
    assert.equal(error.message.includes(f.base), false);
    return true;
  });
});

test('copied distribution resolves in a fresh process without the original checkout or user profile', async context => {
  const f = await fixture(context);
  await f.file('engine/EEEngine.exe', 'This fixture is never executed.');
  await copyFile(fileURLToPath(new URL('../preview/runtime-config.mjs', import.meta.url)), path.join(f.root, 'preview', 'runtime-config.mjs'));
  const script = await f.file('resolve-fixture.mjs', `
    import {resolveRuntimeConfig} from './preview/runtime-config.mjs';
    import path from 'node:path';
    const c=resolveRuntimeConfig({env:{},platform:'win32',home:process.argv[2]});
    if(!c.packaged||c.sourceRoot!==null||c.args.length||c.env.PYTHONPATH)throw new Error('Source dependency');
    if(c.command!==path.join(c.root,'engine','EEEngine.exe'))throw new Error('Wrong engine');
    if(c.directory.startsWith(c.root))throw new Error('Read-only installation used for state');
    console.log('copied-distribution-resolved');
  `);
  const result = await exec(process.execPath, [script, f.home], {cwd: f.base, timeout: 5000, windowsHide: true});
  assert.equal(result.stdout.trim(), 'copied-distribution-resolved');
  assert.equal(result.stderr, '');
});
