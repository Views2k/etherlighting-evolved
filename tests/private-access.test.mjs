import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdir,mkdtemp,readFile,writeFile,rm} from 'node:fs/promises';
import path from 'node:path';
import {createPrivateAccessManager} from '../preview/private-access.mjs';

const ORIGIN='https://fixture.fixture-tailnet.ts.net:8444';
const AUTHORITY='fixture.fixture-tailnet.ts.net:8444';
const TARGET='http://127.0.0.1:4184';
const fields={setup_action:null,action_url:null,audience:'owner',https_port:8444,can_change_port:true};
const existing={TCP:{8443:{HTTPS:true}},Web:{'existing.fixture-tailnet.ts.net:8443':{Handlers:{'/':{Proxy:'http://127.0.0.1:4321'}}}}};

async function fixture(t){
  const parent=path.resolve('outputs');
  await mkdir(parent,{recursive:true});
  const directory=await mkdtemp(path.join(parent,'private-access-test-'));
  t.after(async()=>{assert.equal(path.dirname(directory),parent);await rm(directory,{recursive:true,force:true});});
  const f={directory,configPath:path.join(directory,'private-access.json'),calls:[],route:structuredClone(existing),
    identity:{BackendState:'Running',Self:{Online:true,ID:'fixture-device',UserID:42,DNSName:'fixture.fixture-tailnet.ts.net.'},
      User:{42:{ID:42,LoginName:'owner@example.invalid'}}},active:null,started:0,stopped:0};
  f.runTailscale=async args=>{
    f.calls.push([...args]);
    if(args[0]==='status'){
      if(f.failIdentity)throw new Error('private identity must never appear');
      return JSON.stringify(f.identity);
    }
    if(args.join(' ')==='serve status --json'){
      if(f.failRouteRead)throw new Error('private route must never appear');
      return JSON.stringify(f.route);
    }
    if(f.failBeforeMutation)throw new Error('private mutation must never appear');
    const port=Number(args.find(arg=>arg.startsWith('--https='))?.slice(8));
    const authority=`fixture.fixture-tailnet.ts.net:${port}`;
    if(args.includes('off')){
      delete f.route.TCP?.[port];delete f.route.Web?.[authority];
    }else{
      f.route.TCP??={};f.route.TCP[port]={HTTPS:true};
      f.route.Web??={};f.route.Web[authority]={Handlers:{'/':{Proxy:args.at(-1)}}};
    }
    if(f.alterUnrelated)f.route.TCP[8443]={TCPForward:'foreign'};
    if(f.failAfterMutation)throw new Error('private mutation must never appear');
    return 'private URL must never be returned directly';
  };
  f.options={configPath:f.configPath,runTailscale:f.runTailscale,
    startListener:async gate=>{f.started++;if(f.failStart)throw new Error('private listener');f.active=gate;},
    stopListener:async()=>{f.stopped++;if(f.failStop)throw new Error('private shutdown');f.active=null;}};
  f.manager=createPrivateAccessManager(f.options);
  return f;
}
const writes=f=>f.calls.filter(args=>args[0]==='serve'&&args[1]!=='status');

test('initial status is read-only, cached, and contains no account details',async t=>{
  const f=await fixture(t);
  assert.deepEqual(await f.manager.initialize(),{...fields,available:true,enabled:false,ready:false,url:null,error:null});
  const calls=f.calls.length;
  for(let i=0;i<4;i++)f.manager.status();
  assert.equal(f.calls.length,calls);assert.deepEqual(writes(f),[]);
  assert.equal(JSON.stringify(f.manager.status()).includes('owner@example.invalid'),false);
});

test('explicit enable and disable preserve the existing service and persist no login or node ID',async t=>{
  const f=await fixture(t);
  assert.deepEqual(await f.manager.configure(true),{...fields,can_change_port:false,available:true,enabled:true,ready:true,url:ORIGIN,error:null});
  assert.equal(f.active.origin,ORIGIN);
  const saved=await readFile(f.configPath,'utf8');
  assert.equal(saved.includes('owner@example.invalid'),false);assert.equal(saved.includes('fixture-device'),false);
  assert.deepEqual(f.route.TCP[8443],existing.TCP[8443]);
  assert.deepEqual(await f.manager.configure(false),{...fields,available:true,enabled:false,ready:false,url:null,error:null});
  assert.equal(f.active,null);assert.deepEqual(f.route,existing);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET],['serve','--https=8444','off']]);
});

test('startup with EE\'s route still in place reuses it without Serve writes',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.close();f.calls=[];
  const next=createPrivateAccessManager(f.options);
  assert.equal((await next.initialize()).ready,true);assert.deepEqual(writes(f),[]);
});

test('startup after a clean stop adds EE\'s route back on the saved local port without saving',async t=>{
  const f=await fixture(t);await f.manager.configure(true);
  assert.deepEqual(await f.manager.shutdown(),{route:'removed',https_port:8444});
  const file=await readFile(f.configPath,'utf8');f.calls=[];
  const status=await createPrivateAccessManager(f.options).initialize();
  assert.equal(status.ready,true);assert.equal(status.url,ORIGIN);assert.equal(status.error,null);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET]]);
  assert.deepEqual(f.route.TCP[8443],existing.TCP[8443]);
  assert.deepEqual(f.route.Web['existing.fixture-tailnet.ts.net:8443'],existing.Web['existing.fixture-tailnet.ts.net:8443']);
  assert.equal(await readFile(f.configPath,'utf8'),file);
});

test('another service or unowned matching route cannot be adopted or overwritten',async t=>{
  for(const proxy of ['http://127.0.0.1:5555',TARGET]){
    const f=await fixture(t);f.route.TCP[8444]={HTTPS:true};f.route.Web[AUTHORITY]={Handlers:{'/':{Proxy:proxy}}};
    const before=structuredClone(f.route),result=await f.manager.configure(true);
    assert.equal(result.error.code,'private_access_port_in_use');assert.equal(result.enabled,false);
    assert.deepEqual(f.route,before);assert.deepEqual(writes(f),[]);assert.equal(f.started,0);
  }
});

test('Funnel, extra paths, and foreground routes are not overwritten',async t=>{
  for(const variant of ['funnel','path','foreground']){
    const f=await fixture(t);
    if(variant==='funnel')f.route.AllowFunnel={[AUTHORITY]:true};
    if(variant==='path'){f.route.TCP[8444]={HTTPS:true};f.route.Web[AUTHORITY]={Handlers:{'/other':{Proxy:TARGET}}};}
    if(variant==='foreground')f.route.Foreground={fixture:{TCP:{8444:{HTTPS:true}}}};
    const before=structuredClone(f.route),result=await f.manager.configure(true);
    assert.equal(result.ready,false);assert.deepEqual(f.route,before);assert.deepEqual(writes(f),[]);
    assert.equal(result.error.code,variant==='funnel'?'private_access_funnel_enabled':'private_access_port_in_use');
  }
});

test('changed account or node identity blocks automatic restore',async t=>{
  for(const variant of ['account','node','hostname']){
    const f=await fixture(t);await f.manager.configure(true);f.calls=[];
    if(variant==='account')f.identity.User[42].LoginName='other@example.invalid';
    if(variant==='node')f.identity.Self.ID='changed-device';
    if(variant==='hostname')f.identity.Self.DNSName='changed.fixture-tailnet.ts.net.';
    const result=await f.manager.initialize();
    assert.equal(result.error.code,'private_access_identity_changed');assert.equal(result.ready,false);
    assert.equal(f.active,null);assert.deepEqual(writes(f),[]);
  }
});

test('unavailable CLI, signed-out, tagged, and incomplete identities fail closed',async t=>{
  for(const variant of ['missing','signedout','tagged','incomplete']){
    const f=await fixture(t);
    if(variant==='missing')f.failIdentity=true;
    if(variant==='signedout')f.identity.BackendState='NeedsLogin';
    if(variant==='tagged')f.identity.Self.Tags=['tag:fixture'];
    if(variant==='incomplete')delete f.identity.User;
    const result=await f.manager.configure(true);
    assert.equal(result.available,false);assert.equal(result.ready,false);assert.equal(f.started,0);
    assert.equal(result.error.code,variant==='missing'?'tailscale_unavailable':variant==='tagged'?'tailscale_device_identity_required':'tailscale_signin_required');
    assert.equal(result.error.message.includes('private identity'),false);assert.deepEqual(writes(f),[]);
  }
});

test('a lost mutation reply is reconciled through the exact resulting configuration',async t=>{
  const f=await fixture(t);f.failAfterMutation=true;
  assert.equal((await f.manager.configure(true)).ready,true);
  assert.equal((await f.manager.configure(false)).error,null);
  assert.deepEqual(f.route,existing);
});

test('an unconfirmed enable leaves its listener closed and can recover from the saved intent',async t=>{
  const f=await fixture(t);f.failBeforeMutation=true;
  const failed=await f.manager.configure(true);
  assert.equal(failed.error.code,'private_access_setup_unconfirmed');assert.equal(failed.enabled,true);
  assert.equal(failed.ready,false);assert.equal(failed.url,null);assert.equal(f.active,null);
  f.failBeforeMutation=false;
  assert.equal((await f.manager.configure(true)).ready,true);
});

test('unconfirmed route removal still closes private access and preserves other routes',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.failBeforeMutation=true;
  const result=await f.manager.configure(false);
  assert.equal(result.error.code,'private_access_cleanup_unconfirmed');assert.equal(result.enabled,false);
  assert.equal(result.ready,false);assert.equal(f.active,null);
  assert.deepEqual(f.route.TCP[8443],existing.TCP[8443]);
  assert.equal((await f.manager.initialize()).error.code,'private_access_cleanup_unconfirmed');
});

test('disable refuses a route changed by another owner',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.calls=[];
  f.route.Web[AUTHORITY].Handlers['/'].Proxy='http://127.0.0.1:5555';
  const before=structuredClone(f.route),result=await f.manager.configure(false);
  assert.equal(result.error.code,'private_access_cleanup_unconfirmed');assert.equal(f.active,null);
  assert.deepEqual(f.route,before);assert.deepEqual(writes(f),[]);
});

test('listener and save failures cannot create a route or claim ready',async t=>{
  const f=await fixture(t);f.failStart=true;
  assert.equal((await f.manager.configure(true)).error.code,'private_access_listener_failed');
  assert.deepEqual(writes(f),[]);
  const blocked=path.join(f.directory,'not-a-directory');
  const manager=createPrivateAccessManager({...f.options,configPath:path.join(blocked,'settings.json'),
    startListener:async gate=>{f.active=gate;await writeFile(blocked,'fixture');}});
  f.failStart=false;
  const result=await manager.configure(true);
  assert.equal(result.error.code,'private_access_save_failed');
  assert.equal(result.ready,false);assert.equal(f.active,null);assert.deepEqual(writes(f),[]);
});

test('shutdown callback failure is reported without claiming access was stopped',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.failStop=true;
  const result=await f.manager.configure(false);
  assert.equal(result.error.code,'private_access_shutdown_unconfirmed');assert.equal(result.ready,false);
  assert.equal(result.enabled,true);assert.ok(f.active);
});

test('unrelated route changes during configuration cause a closed, unconfirmed result',async t=>{
  const f=await fixture(t);f.alterUnrelated=true;
  const result=await f.manager.configure(true);
  assert.equal(result.error.code,'private_access_setup_unconfirmed');assert.equal(f.active,null);
});

test('concurrent enable and disable are serialized and final state is disabled',async t=>{
  const f=await fixture(t);
  const [enabled,disabled]=await Promise.all([f.manager.configure(true),f.manager.configure(false)]);
  assert.equal(enabled.ready,true);assert.equal(disabled.enabled,false);assert.equal(f.active,null);
  assert.deepEqual(f.route,existing);
});

test('malformed persisted ownership and invalid toggles cannot mutate Serve',async t=>{
  const f=await fixture(t);await writeFile(f.configPath,JSON.stringify({version:1,enabled:true,origin:ORIGIN,ownerHash:'bad'}));
  assert.equal((await f.manager.initialize()).error.code,'private_access_invalid_configuration');
  await assert.rejects(f.manager.configure('true'));assert.deepEqual(f.calls,[]);
});

test('new users choose another HTTPS port without changing existing services',async t=>{
  const f=await fixture(t);
  const enabled=await f.manager.configure(true,{httpsPort:9444});
  assert.equal(enabled.ready,true);assert.equal(enabled.https_port,9444);
  assert.equal(enabled.url,'https://fixture.fixture-tailnet.ts.net:9444');
  assert.deepEqual(f.route.TCP[8443],existing.TCP[8443]);
  assert.deepEqual(writes(f),[['serve','--bg','--https=9444',TARGET]]);
  const restored=createPrivateAccessManager(f.options);await f.manager.close();
  assert.equal((await restored.initialize()).ready,true);
  assert.equal((await restored.configure(false)).enabled,false);
  assert.deepEqual(f.route,existing);
});

test('an active or uncertain route must be cleaned up before changing its port',async t=>{
  const f=await fixture(t);await f.manager.configure(true);const before=structuredClone(f.route);f.calls=[];
  const refused=await f.manager.configure(true,{httpsPort:9444});
  assert.equal(refused.error.code,'private_access_port_change_requires_off');
  assert.equal(refused.ready,true);assert.ok(f.active);
  assert.equal(refused.https_port,8444);assert.deepEqual(f.route,before);assert.deepEqual(writes(f),[]);
  f.failBeforeMutation=true;await f.manager.configure(false);f.calls=[];
  const uncertain=await f.manager.configure(false,{httpsPort:9444});
  assert.equal(uncertain.error.code,'private_access_cleanup_unconfirmed');
  assert.equal(uncertain.can_change_port,false);assert.equal(uncertain.https_port,8444);assert.deepEqual(writes(f),[]);
  f.failBeforeMutation=false;await f.manager.configure(false);
  assert.equal((await f.manager.configure(false,{httpsPort:9444})).https_port,9444);
});

test('version one ownership restores the original listener without a migration write',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.close();
  const saved=JSON.parse(await readFile(f.configPath));delete saved.httpsPort;delete saved.listenerPort;saved.version=1;
  await writeFile(f.configPath,JSON.stringify(saved));f.calls=[];
  const restored=createPrivateAccessManager(f.options);assert.equal((await restored.initialize()).ready,true);
  assert.equal(JSON.parse(await readFile(f.configPath)).version,1);assert.deepEqual(writes(f),[]);
});

test('fresh setup persists its dynamically allocated local listener and reuses it on restart',async t=>{
  const f=await fixture(t),requested=[];
  f.options.startListener=async(gate,port)=>{requested.push(port);f.active=gate;return {port:port||52001};};
  f.manager=createPrivateAccessManager(f.options);assert.equal((await f.manager.configure(true)).ready,true);
  assert.equal(f.route.Web[AUTHORITY].Handlers['/'].Proxy,'http://127.0.0.1:52001');
  await f.manager.close();const restored=createPrivateAccessManager(f.options);
  assert.equal((await restored.initialize()).ready,true);assert.deepEqual(requested,[0,52001]);
});

test('login is explicit, bounded and never changes an already connected account',async t=>{
  const f=await fixture(t);await f.manager.login();assert.equal(f.calls.some(args=>args[0]==='login'),false);
  f.identity.BackendState='NeedsLogin';
  const runner=f.runTailscale;
  f.options.runTailscale=async args=>{
    if(args[0]!=='login')return runner(args);
    f.calls.push(args);throw Object.assign(new Error('private output'),{actionUrl:'https://login.tailscale.com/a/fixture',code:'tailscale_unavailable'});
  };
  const manager=createPrivateAccessManager(f.options),result=await manager.login();
  assert.equal(result.setup_action,'approve');assert.equal(result.action_url,'https://login.tailscale.com/a/fixture');
  assert.deepEqual(f.calls.filter(args=>args[0]==='login'),[['login','--timeout=5s']]);
  assert.deepEqual(writes(f),[]);assert.equal(JSON.stringify(result).includes('private output'),false);
});

test('stopped or expired clients ask for local recovery without a nonfunctional sign-in button',async t=>{
  const f=await fixture(t);f.identity.BackendState='Stopped';
  assert.equal((await f.manager.login()).error.code,'tailscale_not_running');
  assert.equal(f.manager.status().setup_action,null);
  f.identity.BackendState='Running';f.identity.Self.Expired=true;
  assert.equal((await f.manager.login()).error.code,'tailscale_device_identity_required');
  assert.equal(f.manager.status().setup_action,null);assert.equal(f.calls.some(args=>args[0]==='login'),false);
  assert.ok(f.calls.filter(args=>args[0]==='status').every(args=>args.includes('--peers=false')));
});

test('missing installation and machine approval have actionable states without mutations',async t=>{
  const f=await fixture(t);
  const missing=createPrivateAccessManager({...f.options,runTailscale:async()=>{throw Object.assign(new Error(),{code:'tailscale_not_installed'});}});
  assert.equal((await missing.initialize()).setup_action,'install');
  assert.equal(missing.status().action_url,'https://tailscale.com/download');
  f.identity.BackendState='NeedsMachineAuth';
  const result=await f.manager.login();assert.equal(result.setup_action,'approve');
  assert.equal(result.action_url,'https://login.tailscale.com/admin/machines');
  assert.deepEqual(writes(f),[]);assert.equal(f.calls.some(args=>args[0]==='login'),false);
});

test('after the HTTPS approval, a status check completes the saved setup with one Serve write',async t=>{
  const f=await fixture(t),runner=f.runTailscale;let approved=false;
  f.options.runTailscale=async args=>{
    if(!approved&&args[0]==='serve'&&args.includes('--bg'))throw Object.assign(new Error(),{actionUrl:'https://login.tailscale.com/f/serve?node=fixture'});
    return runner(args);
  };
  const manager=createPrivateAccessManager(f.options),result=await manager.configure(true);
  assert.equal(result.setup_action,'approve');assert.equal(result.ready,false);assert.equal(f.active,null);
  approved=true;f.calls=[];
  assert.equal((await manager.initialize()).ready,true);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET]]);
});

test('invalid ports and malformed persisted listener ports never reach the CLI',async t=>{
  const f=await fixture(t);
  for(const httpsPort of [0,443,65536,NaN,'9444',9444.5])await assert.rejects(f.manager.configure(true,{httpsPort}));
  assert.deepEqual(f.calls,[]);
  await f.manager.configure(true);const saved=JSON.parse(await readFile(f.configPath));saved.listenerPort='4184';
  await writeFile(f.configPath,JSON.stringify(saved));f.calls=[];
  assert.equal((await createPrivateAccessManager(f.options).initialize()).error.code,'private_access_invalid_configuration');
  assert.deepEqual(f.calls,[]);
});

function visitor(login){
  const headers={Host:AUTHORITY,'X-Forwarded-Host':AUTHORITY,'X-Forwarded-Proto':'https','X-Forwarded-For':'100.100.0.20',
    ...(login?{'Tailscale-User-Login':login}:{})};
  return {method:'GET',url:'/api/state',socket:{remoteAddress:'127.0.0.1'},rawHeaders:Object.entries(headers).flat()};
}

test('the accounts audience is explicit, saved without account details, and restored on restart',async t=>{
  const f=await fixture(t);
  assert.equal((await f.manager.configure(true)).audience,'owner');
  assert.equal(f.active.authorize(visitor('guest@example.invalid')).ok,false);
  assert.equal('audience' in JSON.parse(await readFile(f.configPath,'utf8')),false);
  const shared=await f.manager.configure(true,{audience:'accounts'});
  assert.equal(shared.ready,true);assert.equal(shared.audience,'accounts');assert.equal(shared.error,null);
  assert.equal(f.active.authorize(visitor('guest@example.invalid')).ok,true);
  assert.equal(f.active.authorize(visitor('owner@example.invalid')).ok,true);
  assert.equal((await f.active.authorize(visitor())).ok,false);
  const saved=await readFile(f.configPath,'utf8');
  assert.equal(JSON.parse(saved).audience,'accounts');
  assert.equal(saved.includes('owner@example.invalid'),false);assert.equal(saved.includes('guest@example.invalid'),false);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET]]);
  await f.manager.close();
  const next=createPrivateAccessManager(f.options),restored=await next.initialize();
  assert.equal(restored.ready,true);assert.equal(restored.audience,'accounts');
  assert.equal(f.active.authorize(visitor('guest@example.invalid')).ok,true);
  const owner=await next.configure(true,{audience:'owner'});
  assert.equal(owner.ready,true);assert.equal(owner.audience,'owner');
  assert.equal(f.active.authorize(visitor('guest@example.invalid')).ok,false);
  assert.equal('audience' in JSON.parse(await readFile(f.configPath,'utf8')),false);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET]]);
});

test('private access uses the local device record and the current Tailscale host identity',async t=>{
  const f=await fixture(t);
  const sourceIp='100.100.0.20', deviceStableID='fixture-peer';
  const whoisArgs=[];
  const manager=createPrivateAccessManager({...f.options,runTailscale:async args=>{
    if(args[0]!=='whois')return f.runTailscale(args);
    whoisArgs.push(args);
    return JSON.stringify({Node:{StableID:deviceStableID,Tags:['tag:fixture'],Addresses:[sourceIp+'/32']}});
  }});
  assert.equal((await manager.configure(true)).ready,true);
  const recordPath=path.join(f.directory,'approved-device.json');
  const save=hostStableID=>writeFile(recordPath,JSON.stringify({version:1,hostStableID,deviceStableID,addresses:[sourceIp]}),{mode:0o600});
  assert.equal((await f.active.authorize(visitor())).ok,false);
  await save('different-host');
  assert.equal((await f.active.authorize(visitor())).ok,false);
  assert.equal(whoisArgs.length,0);
  await save(f.identity.Self.ID);
  assert.deepEqual(await f.active.authorize(visitor()),{ok:true,device:true});
  assert.deepEqual(whoisArgs,[['whois','--json',sourceIp]]);
  await rm(recordPath);
  assert.equal((await f.active.authorize(visitor())).ok,false);
  assert.equal(f.active.authorize(visitor('owner@example.invalid')).ok,true);
});

test('turning access off keeps the chosen audience and an unknown or unsaved audience never takes effect',async t=>{
  const f=await fixture(t);
  await f.manager.configure(true,{audience:'accounts'});
  const off=await f.manager.configure(false);
  assert.equal(off.enabled,false);assert.equal(off.audience,'accounts');assert.equal(f.active,null);
  assert.equal(JSON.parse(await readFile(f.configPath,'utf8')).audience,'accounts');
  await assert.rejects(f.manager.configure(true,{audience:'everyone'}));
  await f.manager.configure(true,{audience:'owner'});
  f.identity.BackendState='Stopped';
  const failed=await f.manager.configure(true,{audience:'accounts'});
  assert.equal(failed.ready,false);assert.equal(failed.error.code,'tailscale_not_running');assert.equal(failed.audience,'owner');
  assert.equal(f.active,null);assert.equal('audience' in JSON.parse(await readFile(f.configPath,'utf8')),false);
  f.identity.BackendState='Running';
  const retried=await f.manager.initialize();
  assert.equal(retried.ready,true);assert.equal(retried.audience,'owner');
  assert.equal(f.active.authorize(visitor('guest@example.invalid')).ok,false);
});

test('a saved audience outside the known values is an invalid configuration without Serve writes',async t=>{
  const f=await fixture(t);
  await f.manager.configure(true);await f.manager.close();
  const saved=JSON.parse(await readFile(f.configPath,'utf8'));saved.audience='everyone';
  await writeFile(f.configPath,JSON.stringify(saved));f.calls=[];
  const next=createPrivateAccessManager(f.options),status=await next.initialize();
  assert.equal(status.ready,false);assert.equal(status.error.code,'private_access_invalid_configuration');
  assert.equal(f.active,null);assert.deepEqual(writes(f),[]);
});

const unavailable=()=>Object.assign(new Error('aborted'),{code:'tailscale_unavailable'});
// Like the installed CLI, a call on an aborted signal fails at once, and a running call fails when the signal aborts.
function abortable(runner,{hold=()=>false}={}){
  return async(args,{signal}={})=>{
    if(signal?.aborted)throw unavailable();
    const result=await runner(args);
    if(!hold(args))return result;
    return new Promise((_resolve,reject)=>signal.addEventListener('abort',()=>reject(unavailable()),{once:true}));
  };
}

test('shutdown removes only EE\'s route, keeps the setting, and finds nothing to remove the second time',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.calls=[];
  const file=await readFile(f.configPath,'utf8');
  assert.deepEqual(await f.manager.shutdown(),{route:'removed',https_port:8444});
  assert.equal(f.active,null);assert.deepEqual(f.route,existing);
  assert.deepEqual(writes(f),[['serve','--https=8444','off']]);
  assert.equal(await readFile(f.configPath,'utf8'),file);assert.equal(JSON.parse(file).enabled,true);
  f.calls=[];
  assert.deepEqual(await f.manager.shutdown(),{route:'absent',https_port:8444});assert.deepEqual(writes(f),[]);
});

test('shutdown without a saved setting stops the listener and calls nothing',async t=>{
  const f=await fixture(t);
  assert.deepEqual(await f.manager.shutdown(),{route:'none',https_port:null});
  assert.deepEqual(f.calls,[]);assert.equal(f.stopped,1);
});

test('shutdown leaves a route that is no longer exactly EE\'s',async t=>{
  for(const variant of ['owner','funnel']){
    const f=await fixture(t);await f.manager.configure(true);f.calls=[];
    if(variant==='owner')f.route.Web[AUTHORITY].Handlers['/'].Proxy='http://127.0.0.1:5555';
    else f.route.AllowFunnel={[AUTHORITY]:true};
    const before=structuredClone(f.route),result=await f.manager.shutdown();
    assert.equal(result.route,'unconfirmed',variant);assert.equal(result.https_port,8444,variant);
    assert.deepEqual(writes(f),[],variant);assert.deepEqual(f.route,before,variant);assert.equal(f.active,null,variant);
  }
});

test('shutdown reports an unreadable Serve status and a missing Tailscale without rejecting',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.failRouteRead=true;
  assert.deepEqual(await f.manager.shutdown(),{route:'unconfirmed',https_port:8444,code:'private_access_cleanup_unconfirmed'});
  assert.equal(f.active,null);
  const missing=createPrivateAccessManager({...f.options,runTailscale:async()=>{throw Object.assign(new Error(),{code:'tailscale_not_installed'});}});
  assert.deepEqual(await missing.shutdown(),{route:'no_tailscale',https_port:8444});
});

test('shutdown rejects when the listener cannot be stopped and then changes nothing in Serve',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.calls=[];f.failStop=true;
  await assert.rejects(f.manager.shutdown(),{code:'private_access_shutdown_unconfirmed'});
  assert.deepEqual(f.calls,[]);
});

test('shutdown ends a CLI call that does not answer at its deadline',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.close();
  let received;
  const manager=createPrivateAccessManager({...f.options,runTailscale:(_args,{signal}={})=>{
    received=signal;return new Promise((_resolve,reject)=>signal.addEventListener('abort',()=>reject(unavailable()),{once:true}));
  }});
  const started=Date.now(),result=await manager.shutdown({timeoutMs:1000});
  assert.ok(Date.now()-started<1500);
  assert.equal(result.route,'unconfirmed');assert.equal(result.https_port,8444);assert.equal(received.aborted,true);
});

test('shutdown during a restore ends its Serve call and removes the route it added',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.shutdown();
  let entered;const waiting=new Promise(resolve=>{entered=resolve;});
  const manager=createPrivateAccessManager({...f.options,runTailscale:abortable(f.runTailscale,{hold:args=>{
    if(!args.includes('--bg'))return false;entered();return true;
  }})});
  const restoring=manager.initialize();
  await waiting;
  const stopping=manager.shutdown();
  assert.equal((await restoring).ready,false);
  assert.deepEqual(await stopping,{route:'removed',https_port:8444});
  assert.deepEqual(f.route,existing);assert.equal(f.active,null);
});

test('a shutdown whose time runs out while it waits for another operation makes no Serve call',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.close();f.calls=[];
  let release,entered;const blocked=new Promise(resolve=>{release=resolve;}),started=new Promise(resolve=>{entered=resolve;});
  const manager=createPrivateAccessManager({...f.options,runTailscale:async args=>{
    if(args[0]==='status'){entered();await blocked;}
    return f.runTailscale(args);
  }});
  const refreshing=manager.initialize();
  await started;
  const stopping=manager.shutdown({timeoutMs:1000});
  await new Promise(resolve=>setTimeout(resolve,1100));
  f.identity.BackendState='Stopped';release();
  assert.equal((await refreshing).error.code,'tailscale_not_running');
  assert.deepEqual(await stopping,{route:'unconfirmed',https_port:8444,code:'private_access_cleanup_unconfirmed'});
  assert.deepEqual(f.calls.filter(args=>args[0]==='serve'),[]);
});

test('shutdown with private access off removes a route left by an unconfirmed turn-off',async t=>{
  const f=await fixture(t);await f.manager.configure(true);
  await writeFile(f.configPath,JSON.stringify({...JSON.parse(await readFile(f.configPath,'utf8')),enabled:false}));
  assert.deepEqual(await createPrivateAccessManager(f.options).shutdown(),{route:'removed',https_port:8444});
  assert.deepEqual(f.route,existing);
});

test('shutdown accepts a time limit from 1 to 60 seconds and later operations make no CLI calls',async t=>{
  const f=await fixture(t);await f.manager.configure(true);f.calls=[];
  for(const timeoutMs of [999,60001,1.5])await assert.rejects(f.manager.shutdown({timeoutMs}),RangeError);
  assert.deepEqual(f.calls,[]);
  await f.manager.shutdown();f.calls=[];
  await f.manager.initialize();await f.manager.configure(true);await f.manager.configure(false);await f.manager.login();
  assert.deepEqual(f.calls,[]);assert.equal(f.active,null);
});

test('a restore moves to a free local port when the saved one is taken and saves it',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.shutdown();f.calls=[];
  const requested=[];
  f.options.startListener=async(gate,port)=>{requested.push(port);if(port===4184)throw new Error('in use');f.active=gate;return {port:port||52001};};
  const manager=createPrivateAccessManager(f.options);
  assert.equal((await manager.initialize()).ready,true);assert.deepEqual(requested,[4184,0]);
  const saved=JSON.parse(await readFile(f.configPath,'utf8'));
  assert.equal(saved.version,2);assert.equal(saved.listenerPort,52001);assert.equal(saved.enabled,true);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444','http://127.0.0.1:52001']]);
  assert.deepEqual(await manager.shutdown(),{route:'removed',https_port:8444});assert.deepEqual(f.route,existing);
});

test('a leftover route whose local port is taken is removed and then restored on a free port',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.close();f.calls=[];
  f.options.startListener=async(gate,port)=>{if(port===4184)throw new Error('in use');f.active=gate;return {port:port||52001};};
  const manager=createPrivateAccessManager(f.options);
  const failed=await manager.initialize();
  assert.equal(failed.error.code,'private_access_listener_failed');assert.deepEqual(f.route,existing);
  assert.deepEqual(writes(f),[['serve','--https=8444','off']]);
  assert.equal((await manager.initialize()).ready,true);
  assert.equal(f.route.Web[AUTHORITY].Handlers['/'].Proxy,'http://127.0.0.1:52001');
});

test('a restore never changes Serve or the saved file when the setting is off or the port is not EE\'s',async t=>{
  for(const variant of ['off','identity','occupied','public']){
    const f=await fixture(t);await f.manager.configure(true);await f.manager.shutdown();
    if(variant==='off')await writeFile(f.configPath,JSON.stringify({...JSON.parse(await readFile(f.configPath,'utf8')),enabled:false}));
    if(variant==='identity')f.identity.Self.ID='changed-device';
    if(variant==='occupied'){f.route.TCP[8444]={HTTPS:true};f.route.Web[AUTHORITY]={Handlers:{'/':{Proxy:'http://127.0.0.1:5555'}}};}
    if(variant==='public')f.route.AllowFunnel={[AUTHORITY]:true};
    const before=structuredClone(f.route),file=await readFile(f.configPath,'utf8');f.calls=[];
    const status=await createPrivateAccessManager(f.options).initialize();
    assert.equal(status.ready,false,variant);assert.deepEqual(writes(f),[],variant);
    assert.deepEqual(f.route,before,variant);assert.equal(await readFile(f.configPath,'utf8'),file,variant);
  }
});

test('a restore whose Serve change is not confirmed closes the listener and keeps the setting on',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.shutdown();f.failBeforeMutation=true;
  const status=await createPrivateAccessManager(f.options).initialize();
  assert.equal(status.error.code,'private_access_setup_unconfirmed');assert.equal(status.enabled,true);assert.equal(f.active,null);
  assert.equal(JSON.parse(await readFile(f.configPath,'utf8')).enabled,true);
});

test('a version one record restored after a clean stop stays version one',async t=>{
  const f=await fixture(t);await f.manager.configure(true);await f.manager.shutdown();
  const saved=JSON.parse(await readFile(f.configPath));delete saved.httpsPort;delete saved.listenerPort;saved.version=1;
  const text=JSON.stringify(saved);await writeFile(f.configPath,text);f.calls=[];
  assert.equal((await createPrivateAccessManager(f.options).initialize()).ready,true);
  assert.equal(await readFile(f.configPath,'utf8'),text);
  assert.deepEqual(writes(f),[['serve','--bg','--https=8444',TARGET]]);
});
