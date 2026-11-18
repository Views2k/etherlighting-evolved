import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createWorkspaceServer,startupLines} from '../preview/server.mjs';
import {ADMIN_MESSAGES,createAdminAccess} from '../preview/admin-access.mjs';
import {ROUTE_ACCESS} from '../preview/remote-routes.mjs';

const KEY='a'.repeat(64),keyed={'x-ee-admin-key':KEY};
const managerStatus={available:true,enabled:true,ready:true,url:'https://fixture.fixture-tailnet.ts.net:8444',error:null,
  setup_action:'approve',action_url:'https://login.tailscale.com/a/fixture',audience:'owner',https_port:8444,can_change_port:true};

// The LAN access manager's answer with administration access, as status() builds it.
const lanStatus=({admin,closing})=>({available:true,enabled:true,ready:true,port:4280,addresses:[{url:'http://192.168.1.2:4280',interface:'Ethernet'}],
  error:null,pairing:admin?{code:'482913',expires_at:'2026-11-16T20:15:00.000Z',attempts_left:5,ended:null}:null,devices:admin?[]:null,max_devices:16,
  can_configure:admin&&!closing,can_change_port:false,admin_required:!admin});

function fixture({live=true,route={route:'removed',https_port:8444},admin=createAdminAccess({key:KEY,port:4180}),lanFailure}={}){
  let finishClose,failClose,active,closes=0;
  const calls=[],retries=[],configured=[],exits=[],events=[],lan=[];
  const child={exitCode:null,stdin:{writableEnded:false,end:line=>calls.push(`engine ${line.trim()}`)}};
  const lanStep=name=>async(...args)=>{lan.push([name,...args]);if(lanFailure)throw lanFailure;};
  const workspace=createWorkspaceServer({root:'.',port:4180,live,backendPort:1,token:'fixture',child,
    diagnostics:{record:event=>events.push(event),summary:()=>({})},
    privateAccess:{status:()=>({...managerStatus}),initialize:async()=>calls.push('refresh'),login:async()=>calls.push('login'),
      close:async()=>calls.push('private closed'),shutdown:async()=>{calls.push('private shutdown');return route;},
      configure:async(...args)=>{configured.push(JSON.stringify(args));calls.push('configure');}},
    lanAccess:{status:lanStatus,configure:lanStep('configure'),issueCode:lanStep('code'),revoke:lanStep('revoke'),close:async()=>lan.push(['close'])},
    admin,
    closeWorkspace:()=>{closes++;return new Promise((resolve,reject)=>{finishClose=resolve;failClose=reject;});},
    keepAvailable:options=>{active=options.active;return {observe:()=>retries.push('observe'),stop:()=>retries.push('stop')};},
    exit:code=>exits.push(code)});
  workspace.server.close=()=>calls.push('close');
  const [handler]=workspace.server.listeners('request');
  // Requests carry the administration key unless a test passes other headers.
  async function request(url,method,body,headers=keyed){
    const response={writeHead(status,sent={}){this.status=status;this.headers=sent;},end(text){this.body=text;}};
    const req={url,method,headers:{host:'127.0.0.1:4180',...headers},async *[Symbol.asyncIterator](){if(body)yield await body;}};
    await handler(req,response);return response;
  }
  return {request,calls,retries,configured,exits,events,lan,child,workspace,closes:()=>closes,active:()=>active(),
    finish:()=>finishClose(),fail:()=>failClose(new Error('unconfirmed'))};
}
const detail=response=>JSON.parse(response.body).detail;
async function linkCode(f){return JSON.parse((await f.request('/api/workspace/admin-link','POST')).body).code;}

test('an engine that stops on its own stops the gateway with exit code 1',async()=>{
  const f=fixture();
  f.workspace.engineExited();
  assert.deepEqual(f.exits,[1]);assert.deepEqual(f.calls,['close']);assert.deepEqual(f.retries,['stop']);
  assert.equal(f.active(),false);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,503);
});

test('an engine that stops during a workspace close leaves the exit to that close',async()=>{
  const f=fixture(),closing=f.request('/api/workspace/close','POST');
  f.workspace.engineExited();
  f.finish();assert.equal((await closing).status,200);
  assert.deepEqual(f.exits,[]);
});

test('shutdown rejects new private-access enable and refresh requests',async()=>{
  const f=fixture(),closing=f.request('/api/workspace/close','POST');
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,503);
  assert.equal((await f.request('/api/private-access/refresh','POST')).status,503);
  const status=await f.request('/api/private-access','GET');
  assert.equal(JSON.parse(status.body).can_configure,false);
  assert.deepEqual(f.calls,[]);f.finish();await closing;
});

test('private access status from this gateway says it comes from the computer running EE',async()=>{
  const f=fixture();
  assert.equal(JSON.parse((await f.request('/api/private-access','GET')).body).local,true);
  const closing=f.request('/api/workspace/close','POST');
  assert.equal(JSON.parse((await f.request('/api/private-access','GET')).body).local,true);
  f.finish();await closing;
});

test('a request whose body arrives after shutdown cannot reopen private access',async()=>{
  const f=fixture();let sendBody;
  const updating=f.request('/api/private-access','PUT',new Promise(resolve=>{sendBody=resolve;}));
  const closing=f.request('/api/workspace/close','POST');
  sendBody('{"enabled":true}');
  assert.equal((await updating).status,503);assert.deepEqual(f.calls,[]);
  f.finish();await closing;
});

test('failed shutdown reopens admission only when the backend remains usable',async()=>{
  for(const writableEnded of [false,true]){
    const f=fixture();f.child.stdin.writableEnded=writableEnded;
    const closing=f.request('/api/workspace/close','POST');f.fail();assert.equal((await closing).status,409);
    assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,writableEnded?503:200);
    assert.deepEqual(f.calls,writableEnded?[]:['configure']);
  }
});

test('private access results reach the retry watcher and only a confirmed close stops it',async()=>{
  const f=fixture();
  assert.equal((await f.request('/api/private-access/refresh','POST')).status,200);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,200);
  assert.equal((await f.request('/api/private-access','GET')).status,200);
  assert.deepEqual(f.retries,['observe','observe']);
  const failed=f.request('/api/workspace/close','POST');f.fail();assert.equal((await failed).status,409);
  assert.deepEqual(f.retries,['observe','observe']);
  const closing=f.request('/api/workspace/close','POST');f.finish();assert.equal((await closing).status,200);
  assert.deepEqual(f.retries,['observe','observe','stop']);assert.deepEqual(f.calls,['refresh','configure','close']);
});

test('a desktop parent exit shares the close request and the retry watcher pauses while it runs',async()=>{
  const f=fixture();
  assert.equal(f.active(),true);
  const exiting=f.workspace.shutDown(),request=f.request('/api/workspace/close','POST');
  assert.equal(f.active(),false);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,503);
  f.fail();
  await assert.rejects(exiting,/unconfirmed/);assert.equal((await request).status,409);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,200);
  const retried=f.workspace.shutDown();f.finish();await retried;
  assert.deepEqual(f.retries,['observe','stop']);assert.deepEqual(f.calls,['configure']);
});

test('a parent whose cleanup is exhausted readmits changes only while the engine can still receive them',async()=>{
  for(const exitCode of [null,1]){
    const f=fixture();f.child.exitCode=exitCode;
    const exiting=f.workspace.shutDown();f.fail();await assert.rejects(exiting);
    assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,503);
    f.workspace.readmit();
    assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,exitCode===null?200:503);
  }
});

test('a signal stops the retry watcher, private access and the engine and refuses later changes',async()=>{
  const f=fixture();
  f.workspace.stop();
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,503);
  assert.equal(f.active(),false);assert.deepEqual(f.retries,['stop']);
  assert.deepEqual(f.calls,['private shutdown','close','engine quit']);
});

test('the local workspace reports that it is not reached through private access',async()=>{
  assert.equal((await fixture().request('/api/workspace','GET')).body,'{"live":true,"remote":false}');
});

// The private gateway refuses every route without a remote entry, so a stale gateway row would hide a route that
// server.mjs no longer answers.
test('every gateway row is answered by server.mjs',async()=>{
  for(const {method,path} of ROUTE_ACCESS.routes.filter(route=>route.source==='gateway')){
    const f=fixture();
    let response;
    if(path==='/api/workspace/close'){const closing=f.request(path,method);f.finish();response=await closing;}
    else response=await f.request(path,method,method==='PUT'?'{"enabled":true}':undefined);
    assert.equal(typeof response.status,'number',`${method} ${path}`);assert.notEqual(response.status,405,`${method} ${path}`);
  }
  // Requests that server.mjs forwards to the engine fail in this fixture, which has no engine.
  await assert.rejects(fixture().request('/api/state','GET'));
});

test('private access accepts only known audiences and passes the choice to the manager',async()=>{
  const f=fixture();
  for(const body of ['{"enabled":true,"audience":"everyone"}','{"enabled":true,"audience":["accounts"]}','{"enabled":true,"audience":null}','{"enabled":true,"owner":true}'])
    assert.equal((await f.request('/api/private-access','PUT',body)).status,400);
  assert.deepEqual(f.calls,[]);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true,"audience":"accounts"}')).status,200);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,200);
  assert.deepEqual(f.configured,['[true,{"audience":"accounts"}]','[true,{}]']);
});

test('without administration access, private access changes and close are refused before anything changes',async()=>{
  const f=fixture();
  for(const [url,method,body] of [['/api/private-access','PUT','{"enabled":true}'],['/api/private-access/refresh','POST'],['/api/private-access/login','POST']]){
    const refused=await f.request(url,method,body,{});
    assert.equal(refused.status,403,url);assert.equal(refused.headers['Cache-Control'],'no-store');
    assert.deepEqual(detail(refused),{code:'admin_required',message:ADMIN_MESSAGES.privateAccess});
  }
  const close=await f.request('/api/workspace/close','POST',undefined,{});
  assert.equal(close.status,403);assert.deepEqual(detail(close),{code:'admin_required',message:ADMIN_MESSAGES.close});
  assert.deepEqual(f.calls,[]);assert.deepEqual(f.retries,[]);assert.equal(f.closes(),0);assert.equal(f.active(),true);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,200);
  assert.deepEqual(f.calls,['configure']);
});

// Both forms keep `local`: a local tab without administration access still accepts a changed SSH key.
test('a tab without administration access reads private access in the read-only form',async()=>{
  const f=fixture();
  const plain=JSON.parse((await f.request('/api/private-access','GET',undefined,{})).body);
  assert.deepEqual(plain,{...managerStatus,setup_action:null,action_url:null,can_change_port:false,can_configure:false,
    can_close_workspace:false,admin_required:true,local:true});
  const owner=JSON.parse((await f.request('/api/private-access','GET')).body);
  assert.deepEqual(owner,{...managerStatus,can_configure:true,can_close_workspace:true,admin_required:false,local:true});
  assert.equal(JSON.parse((await f.request('/api/private-access/refresh','POST')).body).admin_required,false);
});

test('the demo gateway reports administration access in its close capability',async()=>{
  const f=fixture({live:false}),demo={available:false,enabled:false,ready:false,can_configure:false,local:true};
  assert.deepEqual(JSON.parse((await f.request('/api/private-access','GET',undefined,{})).body),{...demo,can_close_workspace:false,admin_required:true});
  assert.deepEqual(JSON.parse((await f.request('/api/private-access','GET')).body),{...demo,can_close_workspace:true,admin_required:false});
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}',{})).status,403);
});

test('only the key header requests an administration link, and never while the workspace closes',async()=>{
  const f=fixture();
  assert.equal((await f.request('/api/workspace/admin-link','GET')).status,405);
  const issued=await f.request('/api/workspace/admin-link','POST');
  assert.equal(issued.status,200);assert.equal(issued.headers['Cache-Control'],'no-store');
  assert.match(JSON.parse(issued.body).code,/^[0-9a-f]{64}$/);
  const [cookie]=(await f.request(`/?admin_code=${await linkCode(f)}`,'GET',undefined,{})).headers['Set-Cookie'];
  const withCookie=await f.request('/api/workspace/admin-link','POST',undefined,{cookie:cookie.split(';')[0]});
  assert.equal(withCookie.status,403);
  assert.deepEqual(detail(withCookie),{code:'admin_required',message:ADMIN_MESSAGES.link});
  assert.equal((await f.request('/api/workspace/admin-link','POST',undefined,{})).status,403);
  const closing=f.request('/api/workspace/close','POST');
  const refused=await f.request('/api/workspace/admin-link','POST');
  assert.equal(refused.status,503);assert.deepEqual(detail(refused),{code:'workspace_closing',message:ADMIN_MESSAGES.closing});
  f.finish();await closing;
});

test('a browser trades a code for the cookies once, and only on a navigation without Origin',async()=>{
  const f=fixture();
  const code=await linkCode(f);
  assert.notEqual((await f.request(`/?admin_code=${code}`,'HEAD',undefined,{})).status,303);
  assert.notEqual((await f.request(`/?admin_code=${code}`,'GET',undefined,{origin:'http://127.0.0.1:4180'})).status,303);
  const opened=await f.request(`/?admin_code=${code}&view=lighting`,'GET',undefined,{});
  assert.equal(opened.status,303);assert.equal(opened.body,undefined);
  assert.equal(opened.headers.Location,'/');assert.equal(opened.headers['Cache-Control'],'no-store');assert.equal(opened.headers['Referrer-Policy'],'no-referrer');
  const cookies=opened.headers['Set-Cookie'];
  assert.equal(cookies.length,3);assert.match(cookies[0],/^ee_admin_4180=[0-9a-f]{64}; Path=\/api\/private-access; HttpOnly; SameSite=Strict$/);
  const again=await f.request(`/?admin_code=${code}`,'GET',undefined,{});
  assert.equal(again.status,303);assert.equal(again.headers['Set-Cookie'],undefined);
  const browser={cookie:cookies[0].split(';')[0]};
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}',browser)).status,200);
  assert.equal(JSON.parse((await f.request('/api/private-access','GET',undefined,browser)).body).admin_required,false);
});

test('a source run prints an administration link that works once, even a day later',async()=>{
  let time=0;
  const admin=createAdminAccess({key:KEY,port:4180,now:()=>time}),f=fixture({admin});
  const [preview,printed,...rest]=startupLines({port:4180,desktopHost:undefined,admin});
  assert.equal(preview,'Browser preview: http://127.0.0.1:4180');assert.deepEqual(rest,[]);
  assert.match(printed,/^Administration link \(works once\): http:\/\/127\.0\.0\.1:4180\/\?admin_code=[0-9a-f]{64}$/);
  time+=86400000;
  const url=new URL(printed.slice(printed.indexOf('http://'))),opened=await f.request(url.pathname+url.search,'GET',undefined,{});
  assert.equal(opened.status,303);assert.equal(opened.headers['Set-Cookie'].length,3);
  assert.equal((await f.request(url.pathname+url.search,'GET',undefined,{})).headers['Set-Cookie'],undefined);
});

test('a gateway started by the desktop app prints only the preview address and issues no code',()=>{
  const admin={issueCode:()=>assert.fail('The desktop app requests its own administration links.')};
  assert.deepEqual(startupLines({port:4180,desktopHost:'1',admin}),['Browser preview: http://127.0.0.1:4180']);
});

test('a repeated code redeems nothing, and each code still works on its own afterward',async()=>{
  const f=fixture(),first=await linkCode(f),second=await linkCode(f);
  const both=await f.request(`/?admin_code=${first}&admin_code=${second}`,'GET',undefined,{});
  assert.equal(both.status,303);assert.equal(both.headers['Set-Cookie'],undefined);
  for(const code of [first,second])assert.equal((await f.request(`/?admin_code=${code}`,'GET',undefined,{})).headers['Set-Cookie'].length,3);
  assert.equal((await f.request('/?admin_code=bad','GET',undefined,{})).headers['Set-Cookie'],undefined);
});

test('stopping private access removes the Tailscale route in live mode and records an unconfirmed removal',async()=>{
  const confirmed=fixture();await confirmed.workspace.closePrivate();
  assert.deepEqual(confirmed.calls,['private shutdown']);assert.deepEqual(confirmed.retries,['stop']);assert.deepEqual(confirmed.events,[]);
  const unconfirmed=fixture({route:{route:'unconfirmed',https_port:8444,code:'private_access_cleanup_unconfirmed'}});
  await unconfirmed.workspace.closePrivate();
  assert.deepEqual(unconfirmed.events,['private_route_cleanup_unconfirmed']);
  const demo=fixture({live:false});await demo.workspace.closePrivate();
  assert.deepEqual(demo.calls,['private closed']);assert.deepEqual(demo.events,[]);
});

test('the gateway removes the administration key from its environment before it starts the engine',async()=>{
  const source=await readFile(new URL('../preview/server.mjs',import.meta.url),'utf8');
  const removed=source.indexOf('delete process.env.EE_ADMIN_KEY');
  assert.ok(removed>0);assert.ok(removed<source.indexOf('spawn('));
});

test('ending private access when the workspace closes also closes the LAN listener',async()=>{
  for(const live of [true,false]){
    const f=fixture({live});await f.workspace.closePrivate();
    assert.deepEqual(f.lan,[['close']]);assert.deepEqual(f.calls,[live?'private shutdown':'private closed']);
  }
  const signal=fixture();signal.workspace.stop();
  await new Promise(resolve=>setImmediate(resolve));
  assert.deepEqual(signal.lan,[['close']]);
});

test('LAN access changes pass to the manager and are refused while EE closes',async()=>{
  const f=fixture();
  assert.equal((await f.request('/api/lan-access','PUT','{"enabled":true,"port":4281}')).status,200);
  assert.equal((await f.request('/api/lan-access/code','POST')).status,200);
  const removed=await f.request('/api/lan-access/devices/3f9c0a1b2c3d4e5f','DELETE');
  assert.equal(removed.status,200);assert.equal(removed.headers['X-Content-Type-Options'],'nosniff');
  assert.equal(JSON.parse(removed.body).pairing.code,'482913');
  assert.deepEqual(f.lan,[['configure',true,{port:4281}],['code'],['revoke','3f9c0a1b2c3d4e5f']]);
  for(const body of ['{"enabled":"yes"}','{"enabled":true,"port":80}','{"enabled":true,"port":4280.5}','{"enabled":true,"extra":1}','[]','x'])
    assert.deepEqual(detail(await f.request('/api/lan-access','PUT',body)),{code:'invalid',message:'Choose a valid LAN access setting and a port between 1024 and 65535.'});
  assert.equal((await f.request('/api/lan-access','PUT','{"enabled":true,"padding":"'+'a'.repeat(1024)+'"}')).status,413);
  for(const [url,method] of [['/api/lan-access','POST'],['/api/lan-access/code','GET'],['/api/lan-access/devices/3f9c0a1b2c3d4e5f','GET']]){
    const refused=await f.request(url,method);assert.equal(refused.status,405,`${method} ${url}`);assert.equal(refused.body,undefined);
  }
  const closing=f.request('/api/workspace/close','POST');
  const refused=await f.request('/api/lan-access','PUT','{"enabled":false}');
  assert.equal(refused.status,503);assert.deepEqual(detail(refused),{code:'workspace_closing',message:'EE is closing. Reopen EE to change LAN access.'});
  assert.equal(JSON.parse((await f.request('/api/lan-access','GET')).body).can_configure,false);
  assert.equal(f.lan.length,3);f.finish();await closing;
});

test('LAN access refusals from the manager keep their status, and other failures report the status',async()=>{
  const limit=fixture({lanFailure:Object.assign(new Error('16 devices are paired, the most EE allows. Remove one before pairing another.'),{code:'lan_access_device_limit',status:409})});
  const refused=await limit.request('/api/lan-access/code','POST');
  assert.equal(refused.status,409);assert.equal(detail(refused).code,'lan_access_device_limit');
  const failed=await fixture({lanFailure:new Error('unexpected')}).request('/api/lan-access','PUT','{"enabled":true}');
  assert.equal(failed.status,409);
  assert.deepEqual(detail(failed),{code:'lan_access_failed',message:'LAN access could not be updated. Refresh status and try again.'});
  assert.equal(JSON.parse(failed.body).status.enabled,true);
});
