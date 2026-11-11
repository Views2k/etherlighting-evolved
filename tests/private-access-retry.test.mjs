import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdir,mkdtemp,rm} from 'node:fs/promises';
import path from 'node:path';
import {createPrivateAccessManager} from '../preview/private-access.mjs';
import {keepPrivateAccessAvailable,shouldRetryPrivateAccess,RETRY_CODES,RETRY_DELAYS} from '../preview/private-access-retry.mjs';
import {createServiceDiagnostics} from '../preview/service-diagnostics.mjs';

const settle=()=>new Promise(resolve=>setImmediate(resolve));
const waiting=code=>({enabled:true,ready:false,error:{code}});

function timers(){
  const pending=[],cleared=[];
  return {pending,cleared,
    setTimer:(fn,delay)=>{const entry={fn,delay,unref(){entry.unreferenced=true;}};pending.push(entry);return entry;},
    clearTimer:entry=>{cleared.push(entry);const index=pending.indexOf(entry);if(index>=0)pending.splice(index,1);},
    async fire(){const entry=pending.shift();await entry.fn();await settle();return entry;}};
}

function scripted(results){
  let current=results.shift(),calls=0;
  return {manager:{status:()=>current,initialize:async()=>{calls++;if(results.length)current=results.shift();return current;}},calls:()=>calls};
}

test('only an enabled setting waiting on a state that clears by itself is retried',()=>{
  for(const code of RETRY_CODES)assert.equal(shouldRetryPrivateAccess(waiting(code)),true);
  assert.equal(shouldRetryPrivateAccess({enabled:false,ready:false,error:{code:'tailscale_not_running'}}),false);
  assert.equal(shouldRetryPrivateAccess({enabled:true,ready:true,error:null}),false);
  assert.equal(shouldRetryPrivateAccess({enabled:true,ready:false,error:null}),false);
  for(const code of ['private_access_identity_changed','private_access_port_in_use','private_access_funnel_enabled',
    'private_access_invalid_configuration','tailscale_not_installed','tailscale_device_identity_required','private_access_cleanup_unconfirmed'])
    assert.equal(shouldRetryPrivateAccess(waiting(code)),false);
  assert.equal(shouldRetryPrivateAccess(null),false);
});

test('a check that ran before Tailscale was up is repeated until private access is ready',async()=>{
  const clock=timers(),restored=[];
  const f=scripted([waiting('tailscale_not_running'),waiting('tailscale_not_running'),{enabled:true,ready:true,error:null}]);
  const retry=keepPrivateAccessAvailable({manager:f.manager,restored:value=>restored.push(value),setTimer:clock.setTimer,clearTimer:clock.clearTimer});
  retry.observe();
  assert.equal(clock.pending.length,1);assert.equal(clock.pending[0].delay,RETRY_DELAYS[0]);assert.equal(clock.pending[0].unreferenced,true);
  await clock.fire();
  assert.equal(f.calls(),1);assert.deepEqual(restored,[]);assert.equal(clock.pending[0].delay,RETRY_DELAYS[1]);
  await clock.fire();
  assert.equal(f.calls(),2);assert.deepEqual(restored,[{attempts:2}]);assert.equal(clock.pending.length,0);
});

test('retries back off to a ceiling and keep going while the setting stays enabled',async()=>{
  const clock=timers(),f=scripted([waiting('tailscale_unavailable')]);
  keepPrivateAccessAvailable({manager:f.manager,setTimer:clock.setTimer,clearTimer:clock.clearTimer}).observe();
  const seen=[];
  for(let i=0;i<RETRY_DELAYS.length+3;i++)seen.push((await clock.fire()).delay);
  assert.deepEqual(seen,[...RETRY_DELAYS,...Array(3).fill(RETRY_DELAYS.at(-1))]);
  assert.equal(f.calls(),RETRY_DELAYS.length+3);assert.equal(clock.pending.length,1);
});

test('a manual result replaces the pending retry and a later failure starts from the first delay',async()=>{
  const clock=timers(),f=scripted([waiting('tailscale_not_running'),waiting('tailscale_not_running')]);
  const retry=keepPrivateAccessAvailable({manager:f.manager,setTimer:clock.setTimer,clearTimer:clock.clearTimer});
  retry.observe();await clock.fire();
  assert.equal(clock.pending[0].delay,RETRY_DELAYS[1]);
  retry.observe({enabled:true,ready:true,error:null});
  assert.equal(clock.pending.length,0);assert.equal(clock.cleared.length,1);
  retry.observe({enabled:false,ready:false,error:null});
  assert.equal(clock.pending.length,0);
  retry.observe(waiting('tailscale_not_running'));
  assert.equal(clock.pending.length,1);assert.equal(clock.pending[0].delay,RETRY_DELAYS[0]);
});

test('a closing workspace is left alone and a stopped retry never checks again',async()=>{
  const clock=timers(),f=scripted([waiting('tailscale_not_running')]);
  let closing=true;
  const retry=keepPrivateAccessAvailable({manager:f.manager,active:()=>!closing,setTimer:clock.setTimer,clearTimer:clock.clearTimer});
  retry.observe();await clock.fire();
  assert.equal(f.calls(),0);assert.equal(clock.pending.length,1);assert.equal(clock.pending[0].delay,RETRY_DELAYS[0]);
  closing=false;await clock.fire();
  assert.equal(f.calls(),1);
  const queued=clock.pending[0];
  retry.stop();retry.stop();
  assert.equal(clock.pending.length,0);
  await queued.fn();await settle();
  assert.equal(f.calls(),1);
  retry.observe(waiting('tailscale_not_running'));
  assert.equal(clock.pending.length,0);
});

test('a failing check is treated as still waiting, and invalid options are refused',async()=>{
  const clock=timers();let calls=0;
  const manager={status:()=>waiting('tailscale_unavailable'),initialize:async()=>{calls++;throw new Error('private detail');}};
  keepPrivateAccessAvailable({manager,setTimer:clock.setTimer,clearTimer:clock.clearTimer}).observe();
  await clock.fire();
  assert.equal(calls,1);assert.equal(clock.pending.length,1);
  assert.throws(()=>keepPrivateAccessAvailable({}),TypeError);
  assert.throws(()=>keepPrivateAccessAvailable({manager,delays:[]}),RangeError);
  assert.throws(()=>keepPrivateAccessAvailable({manager,delays:[10]}),RangeError);
});

test('a host started before Tailscale restores its saved address without changing Serve',async t=>{
  const parent=path.resolve('outputs');
  await mkdir(parent,{recursive:true});
  const directory=await mkdtemp(path.join(parent,'private-access-retry-test-'));
  t.after(async()=>{assert.equal(path.dirname(directory),parent);await rm(directory,{recursive:true,force:true});});
  const identity={BackendState:'Running',Self:{Online:true,ID:'fixture-device',UserID:42,DNSName:'fixture.fixture-tailnet.ts.net.'},
    User:{42:{ID:42,LoginName:'owner@example.invalid'}}};
  const route={},calls=[];let started=0,active=null;
  const runTailscale=async args=>{
    calls.push([...args]);
    if(args[0]==='status')return JSON.stringify(identity);
    if(args.join(' ')==='serve status --json')return JSON.stringify(route);
    const port=Number(args.find(arg=>arg.startsWith('--https='))?.slice(8));
    route.TCP??={};route.TCP[port]={HTTPS:true};
    route.Web??={};route.Web[`fixture.fixture-tailnet.ts.net:${port}`]={Handlers:{'/':{Proxy:args.at(-1)}}};
    return '';
  };
  const options={configPath:path.join(directory,'private-access.json'),runTailscale,
    startListener:async gate=>{started++;active=gate;},stopListener:async()=>{active=null;}};
  const first=createPrivateAccessManager(options);
  assert.equal((await first.configure(true)).ready,true);
  await first.close();

  // The computer restarts: the gateway checks while Tailscale is still starting.
  identity.BackendState='Starting';calls.length=0;started=0;
  const manager=createPrivateAccessManager(options),clock=timers(),restored=[];
  const retry=keepPrivateAccessAvailable({manager,restored:value=>restored.push(value),setTimer:clock.setTimer,clearTimer:clock.clearTimer});
  const initial=retry.observe(await manager.initialize());
  assert.equal(initial.ready,false);assert.equal(initial.enabled,true);assert.equal(initial.error.code,'tailscale_not_running');
  assert.equal(active,null);assert.equal(clock.pending.length,1);

  await clock.fire();
  assert.equal(manager.status().ready,false);assert.equal(clock.pending.length,1);

  identity.BackendState='Running';
  await clock.fire();
  const status=manager.status();
  assert.equal(status.ready,true);assert.equal(status.error,null);assert.equal(status.url,'https://fixture.fixture-tailnet.ts.net:8444');
  assert.equal(started,1);assert.equal(active.origin,status.url);
  assert.deepEqual(restored,[{attempts:2}]);assert.equal(clock.pending.length,0);
  assert.deepEqual(calls.filter(args=>args[0]==='serve'&&args[1]!=='status'),[]);
  assert.ok(calls.every(args=>args[0]==='status'||args.join(' ')==='serve status --json'));
});

test('a restored address is recorded with its attempt count and nothing else',async t=>{
  const parent=path.resolve('outputs');
  await mkdir(parent,{recursive:true});
  const directory=await mkdtemp(path.join(parent,'private-access-retry-log-'));
  t.after(async()=>{assert.equal(path.dirname(directory),parent);await rm(directory,{recursive:true,force:true});});
  const diagnostics=createServiceDiagnostics(directory,{build:'0.4.25'});
  assert.equal(diagnostics.record('private_access_restored',{attempts:3,url:'https://private.example.invalid'}),true);
  const {readFile}=await import('node:fs/promises');
  const entry=JSON.parse((await readFile(path.join(directory,'service-diagnostics.log'),'utf8')).trim());
  assert.deepEqual(Object.keys(entry).sort(),['attempts','build','event','time']);
  assert.equal(entry.event,'private_access_restored');assert.equal(entry.attempts,3);
  assert.deepEqual(diagnostics.summary(),{available:true,last_session:null});
});
