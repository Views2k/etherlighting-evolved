import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {copyFile, cp, mkdir, mkdtemp, rm, symlink} from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {fileURLToPath} from 'node:url';
import {promisify} from 'node:util';

const root = fileURLToPath(new URL('../', import.meta.url));

// The link points to a copy inside the scratch folder, so removing that folder never reaches the checkout.
async function linkedCopy(t) {
  const parent = path.join(root, 'outputs');
  await mkdir(parent, {recursive: true});
  const directory = await mkdtemp(path.join(parent, 'entry-script-'));
  t.after(() => rm(directory, {recursive: true, force: true}));
  const copy = path.join(directory, 'copy');
  await cp(path.join(root, 'preview'), path.join(copy, 'preview'), {recursive: true});
  await mkdir(path.join(copy, 'tools'));
  for (const tool of ['approve-workspace-device.mjs', 'private-access-cleanup.mjs']) {
    await copyFile(path.join(root, 'tools', tool), path.join(copy, 'tools', tool));
  }
  const link = path.join(directory, 'link');
  await symlink(copy, link, 'junction');
  return link;
}

const run = (script, args) => promisify(execFile)(process.execPath, [script, ...args], {timeout: 10000, windowsHide: true})
  .then(({stderr}) => ({code: 0, stderr}), failure => ({code: failure.code, stderr: failure.stderr}));

test('a tool started through a junction or symbolic link still runs its command', async t => {
  const link = await linkedCopy(t);
  assert.deepEqual(await run(path.join(link, 'tools', 'private-access-cleanup.mjs'), ['extra']),
    {code: 2, stderr: 'Run this command without arguments as the user who runs EE on Windows.\n'});
  const approval = await run(path.join(link, 'tools', 'approve-workspace-device.mjs'), ['extra']);
  assert.equal(approval.code, 1);
  assert.match(approval.stderr, /^(Choose approve DEVICE|Run this command as the user hosting)/);
});
