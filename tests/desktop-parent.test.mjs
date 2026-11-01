import test from 'node:test';
import assert from 'node:assert/strict';
import {PassThrough} from 'node:stream';
import {watchDesktopParent} from '../preview/parent-lifetime.mjs';
import {connectionLabel,switchConnectionStatus} from '../preview/workspace-session.mjs';

test('parent EOF requests one graceful close without killing children', async()=>{
  const input=new PassThrough();let closes=0;
  watchDesktopParent({input,close:async()=>{closes++;}});
  input.end();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(closes,1);
});

test('an unconfirmed close can retry rather than pretending output stopped',async()=>{
  const input=new PassThrough();let closes=0,retry;
  watchDesktopParent({input,close:async()=>{if(++closes===1)throw Error('pending');},setTimer:fn=>retry=fn});
  input.end();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(closes,1);retry();await new Promise(resolve=>setImmediate(resolve));assert.equal(closes,2);
});

test('blocked output does not claim automatic reconnect is running',()=>{
  const session={logged_in:true,connected:false,reconnect_required:true};
  assert.equal(connectionLabel(session),'Switch connection needs attention');
  assert.equal(switchConnectionStatus(session).tone,'error');
});
