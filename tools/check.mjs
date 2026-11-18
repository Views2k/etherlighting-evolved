import {readdir} from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const files = [];
async function collect(directory) {
  for (const entry of await readdir(path.join(root, directory), {withFileTypes: true})) {
    const relative = path.join(directory, entry.name);
    if (entry.isDirectory() && !['node_modules', '__pycache__'].includes(entry.name)) await collect(relative);
    else if (entry.isFile() && entry.name.endsWith('.mjs')) files.push(relative);
  }
}
for (const directory of ['src', 'preview', 'tools', 'tests']) await collect(directory);
for (const file of files) {
  const result = spawnSync(process.execPath, ['--check', path.join(root, file)], {stdio: 'inherit'});
  if (result.error || result.status !== 0) process.exit(1);
}
console.log(`Checked ${files.length} JavaScript modules.`);
