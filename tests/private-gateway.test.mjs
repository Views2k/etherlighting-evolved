import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {once} from 'node:events';
import {createPrivateGateway} from '../preview/private-gateway.mjs';

const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));
const get=(port,path,options={})=>new Promise((resolve,reject)=>{
  const req=http.request({host:'127.0.0.1',port,path,...options},response=>{let text='';response.on('data',data=>text+=data);response.on('end',()=>resolve({status:response.statusCode,text}));});
  req.on('error',reject);req.end(options.body);
});

function deferred(){
  let resolve;
  const promise=new Promise(done=>{resolve=done;});
  return {promise,resolve};
}
function upgrade(port,headers={}){
  return new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path:'/api/ws',headers:{Connection:'Upgrade',Upgrade:'websocket',...headers}});
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
    assert.equal((await get(port,'/api/private-access',{method:'PUT',body:'{}'})).status,403);
    assert.equal((await get(port,'/api/private-access/login',{method:'POST'})).status,403);
    assert.equal((await get(port,'/api/workspace/close',{method:'POST'})).status,403);
  }finally{await gateway.stop();await new Promise(done=>upstream.close(done));}
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
    const path='/api/presets/Evening%20lights?filter=a%2Fb';
    assert.equal((await get(port,path)).status,200);
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
    const apply=get(port,'/api/presets/Evening',{method:'PUT',body:'scene'});
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
    assert.equal((await get(port,'/api/presets/Evening',{method:'PUT',headers:{'x-fail':'1'}})).status,403);
    assert.equal((await upgrade(port,{'x-fail':'1'})).status,403);
    assert.deepEqual(received,[['PUT','/api/presets/Evening','scene']]);
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
    const writeDone=disconnected(get(first.port,'/api/presets/Evening',{method:'PUT',body:'stale'}));
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
