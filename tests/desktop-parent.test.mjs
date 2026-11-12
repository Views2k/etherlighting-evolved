import test from 'node:test';
import assert from 'node:assert/strict';
import {PassThrough} from 'node:stream';
import {EventEmitter, once} from 'node:events';
import {watchDesktopParent, whenWorkspaceGone} from '../preview/parent-lifetime.mjs';
import {connectionLabel,switchConnectionStatus} from '../preview/workspace-session.mjs';

test('parent EOF requests one graceful close without killing children', async()=>{
  const input=new PassThrough();let closes=0;
  watchDesktopParent({input,close:async()=>{closes++;}});
  input.end();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(closes,1);
});

test('an exhausted gateway exits only when both its desktop and engine have gone',async()=>{
  for(const order of ['already gone','engine first','desktop first']){
    const input=new PassThrough(),engine=new EventEmitter();
    engine.exitCode=null;engine.signalCode=null;
    input.resume();
    async function endDesktop(){
      const ended=once(input,'end');input.end();await ended;
    }
    let exits=0;
    if(order==='already gone'){await endDesktop();engine.exitCode=1;}
    whenWorkspaceGone(input,engine,()=>exits++);
    if(order==='engine first'){
      engine.signalCode='SIGTERM';engine.emit('exit',null);assert.equal(exits,0);
      await endDesktop();
    }
    if(order==='desktop first'){
      await endDesktop();assert.equal(exits,0);
      engine.exitCode=1;engine.emit('exit',1);
    }
    assert.equal(exits,1);
  }
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
