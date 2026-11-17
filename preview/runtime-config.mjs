import {statSync} from 'node:fs';
import {homedir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

function fail(code, message) {
  throw Object.assign(new Error(message), {code});
}

function isFile(value) {
  try { return statSync(value).isFile(); } catch { return false; }
}

function absolute(value, label) {
  if (typeof value !== 'string' || !value.trim() || value.includes('\0') || !path.isAbsolute(value)) {
    fail('runtime_path_invalid', `${label} must be an absolute path.`);
  }
  return path.resolve(value);
}

function installedPython(env, platform) {
  const names = platform === 'win32' ? ['python.exe'] : ['python3', 'python'];
  const searchPath = env.PATH ?? env.Path ?? '';
  for (const entry of searchPath.split(platform === 'win32' ? ';' : ':')) {
    const directory = entry.replace(/^"(.*)"$/, '$1');
    if (!path.isAbsolute(directory)) continue;
    for (const name of names) {
      const executable = path.join(directory, name);
      if (isFile(executable)) return executable;
    }
  }
  return null;
}

function userWorkspace(env, platform, home) {
  const userHome = absolute(home, 'User directory');
  if (platform === 'win32') {
    const base = env.LOCALAPPDATA ? absolute(env.LOCALAPPDATA, 'Application data directory')
      : path.join(userHome, 'AppData', 'Local');
    return path.join(base, 'Etherlighting Evolved', 'workspace');
  }
  if (platform === 'darwin') return path.join(userHome, 'Library', 'Application Support', 'Etherlighting Evolved', 'workspace');
  const base = env.XDG_DATA_HOME ? absolute(env.XDG_DATA_HOME, 'Application data directory') : path.join(userHome, '.local', 'share');
  return path.join(base, 'etherlighting-evolved', 'workspace');
}

export function resolveRuntimeConfig({
  root = fileURLToPath(new URL('../', import.meta.url)), env = process.env,
  platform = process.platform, home = homedir(),
} = {}) {
  root = absolute(root, 'Application directory');
  if (!isFile(path.join(root, 'preview', 'index.html'))) {
    fail('runtime_assets_missing', 'The EE workspace files are missing.');
  }
  const mode = env.EE_RUNTIME_MODE;
  if (mode !== undefined && mode !== 'packaged' && mode !== 'development') {
    fail('runtime_mode_invalid', 'Choose packaged or development runtime mode.');
  }
  const explicitEngine = env.EE_ENGINE_EXE === undefined ? null : absolute(env.EE_ENGINE_EXE, 'Engine executable');
  const bundledEngine = path.join(root, 'engine', platform === 'win32' ? 'EEEngine.exe' : 'EEEngine');
  if (explicitEngine && !isFile(explicitEngine)) fail('runtime_engine_missing', 'The configured EE engine is missing.');
  const engine = explicitEngine ?? (isFile(bundledEngine) ? bundledEngine : null);
  const packaged = mode ? mode === 'packaged' : engine !== null;
  if (packaged && !engine) fail('runtime_engine_missing', 'The packaged EE engine is missing.');

  const sourceRoot = env.EE_ENGINE_SOURCE_ROOT === undefined
    ? root : absolute(env.EE_ENGINE_SOURCE_ROOT, 'Engine source directory');
  let command = engine, args = [], cwd = root;
  if (!engine) {
    const entry = path.join(sourceRoot, 'packaging', 'native_service.py');
    if (!isFile(entry) || !isFile(path.join(sourceRoot, 'src', 'etherlighting_evolved', '__init__.py'))) {
      fail('runtime_source_missing', 'The EE engine source files are missing.');
    }
    const relativePython = platform === 'win32' ? ['Scripts', 'python.exe'] : ['bin', 'python'];
    const adjacent = [path.join(root, '.venv', ...relativePython), path.join(sourceRoot, '.venv', ...relativePython)];
    command = env.EE_PREVIEW_PYTHON === undefined ? adjacent.find(isFile) ?? installedPython(env, platform)
      : absolute(env.EE_PREVIEW_PYTHON, 'Python executable');
    if (!command || !isFile(command)) fail('runtime_python_missing', 'Choose an existing Python environment with EE dependencies.');
    args = [entry];
    cwd = sourceRoot;
  }
  const portValue = env.EE_PREVIEW_PORT ?? '4180';
  if (!/^\d+$/.test(String(portValue)) || Number(portValue) < 1 || Number(portValue) > 65535) {
    fail('runtime_port_invalid', 'Choose a local port between 1 and 65535.');
  }
  const liveValue = env.EE_PREVIEW_LIVE;
  if (liveValue !== undefined && liveValue !== '0' && liveValue !== '1') fail('runtime_live_invalid', 'Choose live or demo mode explicitly.');
  const live = liveValue === undefined ? packaged : liveValue === '1';
  const adminKey = env.EE_ADMIN_KEY ?? null;
  if (adminKey !== null && !/^[0-9a-f]{64}$/.test(adminKey)) fail('runtime_admin_key_invalid', 'The local administration key is invalid.');
  const directory = env.EE_WORKSPACE_DATA_DIR === undefined
    ? packaged ? userWorkspace(env, platform, home) : path.join(root, 'outputs', 'browser-preview-data')
    : absolute(env.EE_WORKSPACE_DATA_DIR, 'Workspace data directory');
  const childEnv = {...env};
  for (const name of ['EE_HOST', 'EE_USERNAME', 'EE_PASSWORD', 'EE_KEY_FILE', 'EE_API_TOKEN', 'EE_ADMIN_KEY', 'EE_DATA_DIR', 'PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP']) {
    delete childEnv[name];
  }
  if (!engine) childEnv.PYTHONPATH = path.join(sourceRoot, 'src');
  childEnv.PYTHONUNBUFFERED = '1';
  return {root, sourceRoot: engine ? null : sourceRoot, packaged, live, port: Number(portValue), directory, adminKey,
    command, args, cwd, env: childEnv, serviceOptions: {demo: !live, browser_live: live, data_dir: directory}};
}
