import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {once} from 'node:events';
import {createPrivateGateway,createRouteMatcher,remoteRoutes} from '../preview/private-gateway.mjs';
import {ROUTE_ACCESS} from '../preview/remote-routes.mjs';

const LOCAL_ONLY='This action is available only on the computer running EE.';
const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));
const get=(port,path,options={})=>new Promise((resolve,reject)=>{
  const req=http.request({host:'127.0.0.1',port,path,...options},response=>{let text='';response.on('data',data=>text+=data);response.on('end',()=>resolve({status:response.statusCode,text}));});
  req.on('error',reject);req.end(options.body);
});
const filled=path=>path.replace(/\{[a-z_]+\}/g,'1');

function deferred(){
  let resolve;
  const promise=new Promise(done=>{resolve=done;});
  return {promise,resolve};
}
function upgrade(port,headers={},path='/api/ws'){
  return new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path,headers:{Connection:'Upgrade',Upgrade:'websocket',...headers}});
    req.once('upgrade',(response,socket)=>resolve({status:response.statusCode,socket}));
    req.once('response',response=>{response.resume();resolve({status:response.statusCode});});
    req.once('error',reject);
    req.end();
  });
}

test('private gateway denies unapproved access before touching its upstream',async()=>{
  let calls=0;
  const upstream=http.createServer((_req,res)=>{calls++;res.end('unexpected');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:false})});
    for(const path of ['/','/api/state','/api/session/restore'])assert.equal((await get(port,path,{method:'POST'})).status,403);
    assert.equal(calls,0);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('private gateway strips identity headers and keeps host-management actions local',async()=>{
  let received;
  const upstream=http.createServer((req,res)=>{received=req.headers;res.end('ok');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0,status:()=>({ready:true,can_configure:true,can_close_workspace:true,can_change_port:true,setup_action:'approve',action_url:'https://login.tailscale.com/a/fixture'})});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:true})});
    assert.equal((await get(port,'/api/state',{headers:{'tailscale-user-login':'private','x-ee-workspace':'current','authorization':'must-not-forward'}})).status,200);
    assert.equal(received['tailscale-user-login'],undefined);assert.equal(received.authorization,undefined);
    assert.equal(received.host,`127.0.0.1:${targetPort}`);assert.equal(received['x-ee-workspace'],'current');
    const state=JSON.parse((await get(port,'/api/private-access')).text);assert.equal(state.can_configure,false);assert.equal(state.can_close_workspace,false);
    assert.equal(state.action_url,null);assert.equal(state.setup_action,null);assert.equal(state.can_change_port,false);
    assert.equal(state.local,undefined);
    const refused=await get(port,'/api/private-access',{method:'PUT',body:'{}'});
    assert.equal(refused.status,403);assert.equal(JSON.parse(refused.text).detail.code,'local_access_required');
    assert.equal((await get(port,'/api/private-access/login',{method:'POST'})).status,403);
    assert.equal((await get(port,'/api/workspace/close',{method:'POST'})).status,403);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('administration secrets never pass the private gateway in either direction',async context=>{
  let received;
  const upstream=http.createServer((req,res)=>{
    received=req.headers;
    res.setHeader('Set-Cookie',['ee_admin_4180=fixture; Path=/api/private-access; HttpOnly; SameSite=Strict']);
    res.setHeader('X-Fixture','kept');res.end('ok');
  });
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  context.after(async()=>{await gateway.stop();await new Promise(done=>upstream.close(done));});
  const {port}=await gateway.start({authorize:()=>({ok:true})});
  const response=await new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path:'/api/state',headers:{cookie:'ee_admin_4180=fixture','x-ee-admin-key':'a'.repeat(64)}},resolve);
    req.on('error',reject);req.end();
  });
  response.resume();
  assert.equal(received.cookie,undefined);assert.equal(received['x-ee-admin-key'],undefined);
  assert.equal(response.headers['set-cookie'],undefined);assert.equal(response.headers['x-fixture'],'kept');
});

test('a device on Tailscale cannot request an administration link',async context=>{
  const {port,received}=await recordingGateway(context);
  for(const path of ['/api/workspace/admin-link','/api/workspace/admin%2Dlink'])refusedLocally(await get(port,path,{method:'POST'}));
  assert.deepEqual(received,[]);
});

test('closing private access closes existing connections',async()=>{
  const gateway=createPrivateGateway({targetPort:1,listenPort:0});
  const {port}=await gateway.start({authorize:()=>({ok:true})});
  const socket=net.connect(port,'127.0.0.1');
  await new Promise(resolve=>socket.once('connect',resolve));
  const closed=new Promise(resolve=>socket.once('close',resolve));
  await gateway.stop();await closed;assert.equal(socket.destroyed,true);
});

test('private gateway rejects encoded management routes before forwarding',async()=>{
  let calls=0;
  const upstream=http.createServer((_req,res)=>{calls++;res.end('unexpected');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:true})});
    for(const path of ['/api/workspace/%63lose','/api/%70rivate-access','/api/private-%61ccess',
      '/api/private-access/%72efresh','/api/private-access/%6cogin','/api/%77orkspace/close','/api/workspace/close/']){
      assert.equal((await get(port,path,{method:'POST'})).status,403,path);
    }
    assert.equal(calls,0);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('private gateway rejects malformed and ambiguous path encodings',async()=>{
  let calls=0;
  const upstream=http.createServer((_req,res)=>{calls++;res.end('unexpected');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:true})});
    for(const path of ['/api/%','/api/%GG','/api/%c0%af','/api/%ed%a0%80',
      '/api/workspace%2Fclose','/api/workspace%5cclose','/api/workspace%252fclose',
      '/api/%252e%252e/workspace/close','/api/../api/workspace/close','/api/.%2e/workspace/close',
      '/api//workspace/close','/api/workspace\\close','/api/workspace/close%3fextra',
      '/api/workspace/close%23extra','/api/workspace/close%00']){
      assert.equal((await get(port,path,{method:'POST'})).status,403,path);
    }
    assert.equal(calls,0);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('private gateway retains ordinary encoded names and query values',async()=>{
  let received;
  const upstream=http.createServer((req,res)=>{received=req.url;res.end('ok');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:true})});
    const path='/api/presets/Evening%20lights/apply?filter=a%2Fb';
    assert.equal((await get(port,path,{method:'POST'})).status,200);
    assert.equal(received,path);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('device verification gates writes and preserves host-only access restrictions',{timeout:5000},async()=>{
  const entered=deferred(),decision=deferred(),received=[];
  const upstream=http.createServer(async(req,res)=>{
    let body='';
    for await(const chunk of req)body+=chunk;
    received.push([req.method,req.url,body]);res.end('saved');
  });
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const {port}=await gateway.start({authorize:async req=>{
      if(req.headers['x-fail'])throw new Error('identity unavailable');
      entered.resolve();return decision.promise;
    }});
    const apply=get(port,'/api/presets/Evening/apply',{method:'POST',body:'scene'});
    await entered.promise;
    assert.deepEqual(received,[]);
    decision.resolve({ok:true,device:true});
    assert.deepEqual(await apply,{status:200,text:'saved'});
    const status=JSON.parse((await get(port,'/api/private-access')).text);
    assert.equal(status.can_configure,false);
    assert.equal(status.can_close_workspace,false);
    for(const path of ['/api/private-access','/api/private-access/refresh','/api/private-access/login','/api/workspace/close']){
      assert.equal((await get(port,path,{method:'POST'})).status,403);
    }
    assert.equal((await get(port,'/api/presets/Evening/apply',{method:'POST',headers:{'x-fail':'1'}})).status,403);
    assert.equal((await upgrade(port,{'x-fail':'1'})).status,403);
    assert.deepEqual(received,[['POST','/api/presets/Evening/apply','scene']]);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('replacing the listener cancels writes and upgrades awaiting a device decision',{timeout:5000},async()=>{
  const httpEntered=deferred(),wsEntered=deferred(),decision=deferred(),received=[];
  const upstream=http.createServer((req,res)=>{received.push(req.url);res.end('ok');});
  upstream.on('upgrade',(_req,socket)=>{received.push('upgrade');socket.destroy();});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  try{
    const first=await gateway.start({authorize:(_req,{websocket})=>{
      (websocket?wsEntered:httpEntered).resolve();return decision.promise;
    }});
    const disconnected=req=>req.then(()=>false,()=>true);
    const writeDone=disconnected(get(first.port,'/api/presets/Evening/apply',{method:'POST',body:'stale'}));
    const upgradeDone=disconnected(upgrade(first.port));
    await Promise.all([httpEntered.promise,wsEntered.promise]);
    const second=await gateway.start({authorize:()=>({ok:true})});
    decision.resolve({ok:true,device:true});
    assert.deepEqual(await Promise.all([writeDone,upgradeDone]),[true,true]);
    assert.equal((await get(second.port,'/api/state')).status,200);
    assert.deepEqual(received,['/api/state']);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
});

test('removing device approval closes the subscription and leaves an account subscription connected',{timeout:5000},async()=>{
  const deviceRecheck=deferred(),decision=deferred(),remotes=new Set();
  let accountChecks=0,deviceChecks=0;
  const upstream=http.createServer();
  upstream.on('upgrade',(_req,socket)=>{
    remotes.add(socket);
    socket.on('end',()=>socket.destroy());
    socket.on('close',()=>remotes.delete(socket));
    socket.write('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n');
    socket.pipe(socket);
  });
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0,deviceRecheckMs:10});
  let account,device;
  try{
    const {port}=await gateway.start({authorize:(request,{websocket})=>{
      assert.equal(websocket,true);
      if(request.headers['x-account']){accountChecks++;return {ok:true};}
      deviceChecks++;
      if(deviceChecks===1)return Promise.resolve({ok:true,device:true});
      deviceRecheck.resolve();return decision.promise;
    }});
    account=await upgrade(port,{'x-account':'1'});
    device=await upgrade(port);
    assert.equal(account.status,101);assert.equal(device.status,101);
    device.socket.resume();
    const closed=once(device.socket,'close');
    await deviceRecheck.promise;
    decision.resolve({ok:false});
    await closed;
    const echo=once(account.socket,'data');
    account.socket.write('still connected');
    assert.equal((await echo)[0].toString(),'still connected');
    assert.equal(accountChecks,1);assert.equal(deviceChecks,2);
  }finally{
    account?.socket?.destroy();device?.socket?.destroy();
    await gateway.stop();
    for(const remote of remotes)remote.destroy();
    await new Promise(done=>upstream.close(done));
  }
});

async function recordingGateway(context){
  const received=[];
  const upstream=http.createServer((req,res)=>{received.push([req.method,req.url]);res.end('ok');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0,status:()=>({ready:true})});
  context.after(async()=>{await gateway.stop();await new Promise(done=>upstream.close(done));});
  const {port}=await gateway.start({authorize:()=>({ok:true})});
  return {port,received};
}
const refusedLocally=response=>{
  assert.equal(response.status,403);
  const {detail}=JSON.parse(response.text);
  assert.equal(detail.code,'local_access_required');assert.equal(detail.message,LOCAL_ONLY);
};

test('every classified route follows its entry',async context=>{
  const {port,received}=await recordingGateway(context);
  for(const {method,path,remote} of ROUTE_ACCESS.routes.filter(route=>route.method!=='WS')){
    const url=filled(path),before=received.length,response=await get(port,url,{method});
    if(!remote){refusedLocally(response);assert.equal(received.length,before,url);continue;}
    assert.equal(response.status,200,`${method} ${url}`);
    if(url==='/api/workspace')assert.deepEqual(JSON.parse(response.text),{live:true,remote:true});
    if(['/api/private-access','/api/workspace'].includes(url))assert.equal(received.length,before,url);
    else assert.deepEqual(received.at(-1),[method,url]);
  }
});

test('unlisted routes are local by default',async context=>{
  const {port,received}=await recordingGateway(context);
  for(const [method,path] of [['POST','/api/new-feature'],['GET','/api/session/unlisted'],['DELETE','/api/state'],
    ['OPTIONS','/api/state'],['PATCH','/api/fans'],['GET','/api']])refusedLocally(await get(port,path,{method}));
  assert.equal((await get(port,'/api/state',{method:'HEAD'})).status,403);
  assert.deepEqual(received,[]);
});

test('a local route under a remote placeholder stays local',()=>{
  const matcher=createRouteMatcher({static:{methods:['GET','HEAD']},routes:[
    {method:'DELETE',path:'/api/x/{id}',remote:true},{method:'DELETE',path:'/api/x/history',remote:false},
    {method:'PUT',path:'/api/y/{id}',remote:false},{method:'PUT',path:'/api/y/sync',remote:true}]});
  assert.equal(matcher.allowed('DELETE','/api/x/history'),false);assert.equal(matcher.allowed('DELETE','/api/x/abc'),true);
  assert.equal(matcher.allowed('PUT','/api/y/sync'),false);assert.equal(matcher.allowed('GET','/api/x/abc'),false);
});

// A future route that overlaps a local entry shows up here. Rename the route instead of loosening the matcher.
test('the matcher agrees with every entry',()=>{
  for(const {method,path,remote} of ROUTE_ACCESS.routes){
    const result=method==='WS'?remoteRoutes.socket(path):remoteRoutes.allowed(method,filled(path));
    assert.equal(result,remote,`${method} ${path}`);
  }
});

test('static files allow only GET and HEAD',async context=>{
  const {port,received}=await recordingGateway(context);
  for(const [method,path] of [['GET','/'],['GET','/preview/app.mjs'],['HEAD','/assets/logo.png']])
    assert.equal((await get(port,path,{method})).status,200,path);
  for(const [method,path] of [['POST','/preview/app.mjs'],['PUT','/']])refusedLocally(await get(port,path,{method}));
  assert.deepEqual(received,[['GET','/'],['GET','/preview/app.mjs'],['HEAD','/assets/logo.png']]);
});

test('the query never changes the classification',async context=>{
  const {port,received}=await recordingGateway(context);
  refusedLocally(await get(port,'/api/session/logout?forget=true',{method:'POST'}));
  assert.equal((await get(port,'/api/session/restore?resume=true',{method:'POST'})).status,200);
  assert.deepEqual(received,[['POST','/api/session/restore?resume=true']]);
});

test('encoded paths are classified after decoding',async context=>{
  const {port,received}=await recordingGateway(context);
  refusedLocally(await get(port,'/api/session/%6cogin',{method:'POST'}));
  assert.equal((await get(port,'/api/zones/left%20desk/effect',{method:'PUT'})).status,200);
  assert.deepEqual(received,[['PUT','/api/zones/left%20desk/effect']]);
});

test('private gateway keeps SSH key approval local',async context=>{
  const {port,received}=await recordingGateway(context);
  for(const route of ['accept','replace']){
    for(const [method,path] of [['POST',`/api/session/host-key/${route}`],['POST',`/api/session/host-key/${route}/`],
      ['POST',`/api/session/host-key/${route}?x=1`],['POST',`/api/session/host-key/%${route.charCodeAt(0).toString(16)}${route.slice(1)}`],
      ['PUT',`/api/session/host-key/${route}`]]){
      const response=await get(port,path,{method});
      assert.equal(response.status,403,`${method} ${path}`);
      assert.equal(JSON.parse(response.text).detail.code,'local_access_required',`${method} ${path}`);
    }
  }
  refusedLocally(await get(port,'/api/session/forget-host-key',{method:'POST'}));
  assert.deepEqual(received,[]);
});

test('only listed socket paths upgrade',{timeout:5000},async context=>{
  const checked=[],sockets=new Set();
  const upstream=http.createServer();
  upstream.on('upgrade',(_req,socket)=>{sockets.add(socket);socket.write('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n');});
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0});
  context.after(async()=>{await gateway.stop();for(const socket of sockets)socket.destroy();await new Promise(done=>upstream.close(done));});
  const {port}=await gateway.start({authorize:request=>{checked.push(request.url);return {ok:true};}});
  const opened=await upgrade(port);
  assert.equal(opened.status,101);opened.socket.destroy();
  for(const path of ['/api/ws?token=x','/api/ws/','/api/other'])assert.equal((await upgrade(port,{},path)).status,403,path);
  assert.deepEqual(checked,['/api/ws']);
});
