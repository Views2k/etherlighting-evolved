import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import {createHash} from 'node:crypto';
import {mkdir, mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {fileURLToPath} from 'node:url';
import {promisify} from 'node:util';
import {removePrivateAccessRoute} from '../tools/private-access-cleanup.mjs';

const AUTHORITY = 'fixture.fixture-tailnet.ts.net:8444';
const OTHER = {TCP: {8443: {HTTPS: true}}, Web: {'existing.fixture-tailnet.ts.net:8443': {Handlers: {'/': {Proxy: 'http://127.0.0.1:4321'}}}}};
const UNCONFIRMED = 'EE could not confirm that its Tailscale Serve route on port 8444 was removed. Run "tailscale serve status". If port 8444 forwards to http://127.0.0.1, remove it with "tailscale serve --https=8444 off".';

async function fixture(t, {saved = true} = {}) {
  const parent = path.resolve('outputs');
  await mkdir(parent, {recursive: true});
  const directory = await mkdtemp(path.join(parent, 'private-access-cleanup-'));
  t.after(() => rm(directory, {recursive: true, force: true}));
  const digest = value => createHash('sha256').update(value).digest('hex');
  const file = path.join(directory, 'private-access.json');
  const record = JSON.stringify({version: 2, enabled: true, origin: `https://${AUTHORITY}`, ownerHash: digest('owner@example.invalid'),
    nodeHash: digest('fixture-device'), httpsPort: 8444, listenerPort: 4184}) + '\n';
  if (saved) await writeFile(file, record);
  const route = structuredClone(OTHER);
  route.TCP[8444] = {HTTPS: true};
  route.Web[AUTHORITY] = {Handlers: {'/': {Proxy: 'http://127.0.0.1:4184'}}};
  const f = {directory, file, record, route, calls: [], failRead: false};
  f.run = async args => {
    f.calls.push([...args]);
    if (args.join(' ') === 'serve status --json') {
      if (f.failRead) throw Object.assign(new Error('private detail'), {code: 'tailscale_unavailable'});
      return JSON.stringify(f.route);
    }
    if (args.join(' ') === 'serve --https=8444 off') { delete f.route.TCP[8444]; delete f.route.Web[AUTHORITY]; return ''; }
    throw new Error(`Unexpected command ${args.join(' ')}`);
  };
  return f;
}
const writes = f => f.calls.filter(args => args[0] === 'serve' && args[1] !== 'status');

test('without a saved setting nothing is checked', async t => {
  const f = await fixture(t, {saved: false});
  assert.deepEqual(await removePrivateAccessRoute(f.directory, f.run), {exitCode: 0, message: 'EE has no private access setting for this user.'});
  assert.deepEqual(f.calls, []);
});

test('EE\'s route is removed and everything else stays as it was', async t => {
  const f = await fixture(t);
  assert.deepEqual(await removePrivateAccessRoute(f.directory, f.run), {exitCode: 0, message: 'EE removed its Tailscale Serve route on port 8444.'});
  assert.deepEqual(writes(f), [['serve', '--https=8444', 'off']]);
  assert.deepEqual(f.route, OTHER);
  assert.equal(await readFile(f.file, 'utf8'), f.record);
});

test('a port without EE\'s route needs no change', async t => {
  const f = await fixture(t);
  f.route = structuredClone(OTHER);
  assert.deepEqual(await removePrivateAccessRoute(f.directory, f.run), {exitCode: 0, message: 'EE has no Tailscale Serve route on this computer.'});
  assert.deepEqual(writes(f), []);
});

test('a route EE does not own is left alone and its port is reported', async t => {
  for (const variant of ['occupied', 'funnel']) {
    const f = await fixture(t);
    if (variant === 'occupied') f.route.Web[AUTHORITY].Handlers['/'].Proxy = 'http://127.0.0.1:5555';
    else f.route.AllowFunnel = {[AUTHORITY]: true};
    const before = structuredClone(f.route);
    assert.deepEqual(await removePrivateAccessRoute(f.directory, f.run), {exitCode: 8444, message: UNCONFIRMED}, variant);
    assert.deepEqual(writes(f), [], variant);
    assert.deepEqual(f.route, before, variant);
  }
});

test('a computer without Tailscale has no route to remove', async t => {
  const f = await fixture(t);
  const missing = async () => { throw Object.assign(new Error('private detail'), {code: 'tailscale_not_installed'}); };
  assert.deepEqual(await removePrivateAccessRoute(f.directory, missing),
    {exitCode: 0, message: 'Tailscale is not installed on this computer, so EE has no route to remove.'});
});

test('an unreadable Serve status reports the port', async t => {
  const f = await fixture(t);
  f.failRead = true;
  assert.deepEqual(await removePrivateAccessRoute(f.directory, f.run), {exitCode: 8444, message: UNCONFIRMED});
});

test('an invalid setting is reported without calling Tailscale', async t => {
  const f = await fixture(t);
  await writeFile(f.file, '{"version":2,"enabled":true}');
  const result = await removePrivateAccessRoute(f.directory, f.run);
  assert.equal(result.exitCode, 2);
  assert.match(result.message, /could not read its private access setting/);
  assert.deepEqual(f.calls, []);
});

test('a Tailscale command that does not answer is ended at the time limit', async t => {
  const f = await fixture(t);
  let received;
  const hanging = (_args, {signal} = {}) => {
    received = signal;
    return new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(Object.assign(new Error(), {code: 'tailscale_unavailable'})), {once: true}));
  };
  const started = Date.now();
  assert.deepEqual(await removePrivateAccessRoute(f.directory, hanging, {timeoutMs: 1000}), {exitCode: 8444, message: UNCONFIRMED});
  assert.ok(Date.now() - started < 1500);
  assert.equal(received.aborted, true);
});

test('the command refuses arguments before it reads anything', async () => {
  const tool = fileURLToPath(new URL('../tools/private-access-cleanup.mjs', import.meta.url));
  const error = await promisify(execFile)(process.execPath, [tool, 'extra'], {timeout: 10000, windowsHide: true}).then(() => null, failure => failure);
  assert.equal(error?.code, 2);
  assert.equal(error.stderr.trim(), 'Run this command without arguments as the user who runs EE on Windows.');
});
