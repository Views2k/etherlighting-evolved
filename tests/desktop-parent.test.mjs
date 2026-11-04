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


test('parent cleanup has a finite retry budget and records exhaustion without claiming success',async()=>{
  const input=new PassThrough(),timers=[],reports=[],exhausted=[];let closes=0;
  const stop=watchDesktopParent({input,close:async()=>{closes++;throw Error('unconfirmed');},
    report:value=>reports.push(value),exhausted:value=>exhausted.push(value),setTimer:(fn,delay)=>timers.push({fn,delay})});
  input.end();await new Promise(resolve=>setImmediate(resolve));
  for(let i=0;i<3;i++){
    assert.equal(timers.length,1);const next=timers.shift();assert.equal(next.delay,5000);next.fn();
    await new Promise(resolve=>setImmediate(resolve));
  }
  assert.equal(closes,4);assert.equal(reports.length,4);assert.deepEqual(exhausted,[{attempts:4}]);assert.equal(timers.length,0);
  stop();stop();await new Promise(resolve=>setImmediate(resolve));assert.equal(closes,4);
});

test('successful retry stops scheduling and duplicate parent requests cannot overlap cleanup',async()=>{
  const input=new PassThrough(),timers=[];let closes=0,finish;
  const stop=watchDesktopParent({input,close:()=>{closes++;return new Promise(resolve=>finish=resolve);},setTimer:fn=>timers.push(fn)});
  stop();stop();input.end('quit');await new Promise(resolve=>setImmediate(resolve));
  assert.equal(closes,1);finish();await new Promise(resolve=>setImmediate(resolve));stop();
  assert.equal(timers.length,0);assert.equal(closes,1);
});
