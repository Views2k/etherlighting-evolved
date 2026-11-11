import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const source=readFileSync(new URL('../preview/server.mjs',import.meta.url),'utf8');
function fixture(){
  let handler,finishClose,failClose;
  const calls=[],retries=[],configured=[];
  const context={live:true,closing:undefined,workspaceClosing:false,permitted:()=>true,
    privateRetry:{observe:()=>retries.push('observe'),stop:()=>retries.push('stop')},
    diagnostics:{record(){}},
    child:{exitCode:null,stdin:{writableEnded:false}},
    privateAccess:{status:()=>({ready:true}),initialize:async()=>calls.push('refresh'),configure:async(...args)=>{configured.push(JSON.stringify(args));calls.push('configure');}},
    closeWorkspace:()=>new Promise((resolve,reject)=>{finishClose=resolve;failClose=reject;}),
    http:{createServer:callback=>{handler=callback;return {close:()=>calls.push('close')};}}};
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf('const server=http.createServer'),source.indexOf("server.on('upgrade'")),context);
  async function request(url,method,body){
    const response={writeHead(status){this.status=status;},end(text){this.body=text;}};
    const req={url,method,async *[Symbol.asyncIterator](){if(body)yield await body;}};
    await handler(req,response);return response;
  }
  return {request,calls,retries,configured,context,finish:()=>finishClose(),fail:()=>failClose(new Error('unconfirmed'))};
}

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
    const f=fixture();f.context.child.stdin.writableEnded=writableEnded;
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

test('private access accepts only known audiences and passes the choice to the manager',async()=>{
  const f=fixture();
  for(const body of ['{"enabled":true,"audience":"everyone"}','{"enabled":true,"audience":["accounts"]}','{"enabled":true,"audience":null}','{"enabled":true,"owner":true}'])
    assert.equal((await f.request('/api/private-access','PUT',body)).status,400);
  assert.deepEqual(f.calls,[]);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true,"audience":"accounts"}')).status,200);
  assert.equal((await f.request('/api/private-access','PUT','{"enabled":true}')).status,200);
  assert.deepEqual(f.configured,['[true,{"audience":"accounts"}]','[true,{}]']);
});
