import test from 'node:test';
import assert from 'node:assert/strict';
import {EventEmitter} from 'node:events';
import {createWorkspaceCloser} from '../preview/workspace-close.mjs';

function fixture({delayed = false, privateFailure = false, releaseFailure = false, exitCode = 0} = {}) {
  const child = new EventEmitter();
  child.exitCode = null; child.signalCode = null; child.stdin = new EventEmitter();
  const calls = {release:0, quit:0, private:0};
  const finish = (code = exitCode) => { child.exitCode = code; child.emit('exit', code); };
  child.stdin.end = () => { calls.quit++; if (!delayed) queueMicrotask(finish); };
  const close = createWorkspaceCloser({child, timeoutMs:15,
    release:async () => { calls.release++; if (releaseFailure && calls.release === 1) throw new Error('release_timeout'); },
    closePrivate:async () => { calls.private++; if (privateFailure && calls.private === 1) throw new Error('private_failed'); }});
  return {child, calls, finish, close};
}

test('workspace close completes once and preserves confirmed phases', async () => {
  const f = fixture(); await f.close(); await f.close();
  assert.deepEqual(f.calls, {release:1, quit:1, private:1});
});
test('a late successful child exit can finish cleanup on retry', async () => {
  const f = fixture({delayed:true});
  await assert.rejects(f.close(), /shutdown_timeout/);
  assert.equal(f.child.listenerCount('exit'), 0);
  assert.equal(f.child.stdin.listenerCount('error'), 0);
  f.finish(); await f.close();
  assert.deepEqual(f.calls, {release:1, quit:1, private:1});
});
test('retry can keep waiting for an already requested child shutdown', async () => {
  const f = fixture({delayed:true});
  await assert.rejects(f.close(), /shutdown_timeout/);
  const retry = f.close(); f.finish(); await retry;
  assert.deepEqual(f.calls, {release:1, quit:1, private:1});
});
test('private cleanup failure retries without calling the stopped backend', async () => {
  const f = fixture({privateFailure:true});
  await assert.rejects(f.close(), /private_failed/); await f.close();
  assert.deepEqual(f.calls, {release:1, quit:1, private:2});
});
test('an unconfirmed release does not stop the child and is retried', async () => {
  const f = fixture({releaseFailure:true});
  await assert.rejects(f.close(), /release_timeout/);
  assert.deepEqual(f.calls, {release:1, quit:0, private:0});
  await f.close(); assert.deepEqual(f.calls, {release:2, quit:1, private:1});
});
test('nonzero and signal exits never count as confirmed handoff', async () => {
  const f = fixture({exitCode:1});
  await assert.rejects(f.close(), /shutdown_unconfirmed/);
  await assert.rejects(f.close(), /shutdown_unconfirmed/);
  assert.equal(f.calls.private, 0);
  const signal = fixture({delayed:true});
  const pending = signal.close(); await Promise.resolve();
  signal.child.signalCode = 'SIGTERM'; signal.finish(null);
  await assert.rejects(pending, /shutdown_unconfirmed/);
  await assert.rejects(signal.close(), /shutdown_unconfirmed/);
  assert.equal(signal.calls.private, 0);
});
