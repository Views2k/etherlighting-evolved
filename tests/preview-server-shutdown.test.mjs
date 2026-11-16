import test from 'node:test';
import assert from 'node:assert/strict';
import {createWorkspaceServer} from '../preview/server.mjs';
import {ROUTE_ACCESS} from '../preview/remote-routes.mjs';

function fixture(){
  let finishClose,failClose,active;
  const calls=[],retries=[],configured=[],exits=[];
  const child={exitCode:null,stdin:{writableEnded:false,end:line=>calls.push(`engine ${line.trim()}`)}};
  const workspace=createWorkspaceServer({root:'.',port:4180,live:true,backendPort:1,token:'fixture',child,diagnostics:{record(){},summary:()=>({})},
    privateAccess:{status:()=>({ready:true}),initialize:async()=>calls.push('refresh'),login:async()=>calls.push('login'),close:async()=>calls.push('private closed'),
      configure:async(...args)=>{configured.push(JSON.stringify(args));calls.push('configure');}},
    closeWorkspace:()=>new Promise((resolve,reject)=>{finishClose=resolve;failClose=reject;}),
    keepAvailable:options=>{active=options.active;return {observe:()=>retries.push('observe'),stop:()=>retries.push('stop')};},
    exit:code=>exits.push(code)});
  workspace.server.close=()=>calls.push('close');
  const [handler]=workspace.server.listeners('request');
  async function request(url,method,body){
    const response={writeHead(status){this.status=status;},end(text){this.body=text;}};
    const req={url,method,headers:{host:'127.0.0.1:4180'},async *[Symbol.asyncIterator](){if(body)yield await body;}};
    await handler(req,response);return response;
  }
  return {request,calls,retries,configured,exits,child,workspace,active:()=>active(),finish:()=>finishClose(),fail:()=>failClose(new Error('unconfirmed'))};
}

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
  assert.deepEqual(f.calls,['private closed','close','engine quit']);
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
