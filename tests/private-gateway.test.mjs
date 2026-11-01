import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {createPrivateGateway} from '../preview/private-gateway.mjs';

const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));
const get=(port,path,options={})=>new Promise((resolve,reject)=>{
  const req=http.request({host:'127.0.0.1',port,path,...options},response=>{let text='';response.on('data',data=>text+=data);response.on('end',()=>resolve({status:response.statusCode,text}));});
  req.on('error',reject);req.end(options.body);
});

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
  const targetPort=await listen(upstream),gateway=createPrivateGateway({targetPort,listenPort:0,status:()=>({ready:true,can_configure:true,can_change_port:true,setup_action:'approve',action_url:'https://login.tailscale.com/a/fixture'})});
  try{
    const {port}=await gateway.start({authorize:()=>({ok:true})});
    assert.equal((await get(port,'/api/state',{headers:{'tailscale-user-login':'private','x-ee-workspace':'current','authorization':'must-not-forward'}})).status,200);
    assert.equal(received['tailscale-user-login'],undefined);assert.equal(received.authorization,undefined);
    assert.equal(received.host,`127.0.0.1:${targetPort}`);assert.equal(received['x-ee-workspace'],'current');
    const state=JSON.parse((await get(port,'/api/private-access')).text);assert.equal(state.can_configure,false);
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
    for(const path of ['/api/desktop/%71uit','/api/%64esktop/show','/api/private-%61ccess',
      '/api/private-access/%72efresh','/api/private-access/%6cogin','/api/%77orkspace/close','/api/desktop/quit/']){
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
      '/api/desktop%2Fquit','/api/desktop%5cquit','/api/desktop%252fquit',
      '/api/%252e%252e/desktop/quit','/api/../api/desktop/quit','/api/.%2e/desktop/quit',
      '/api//desktop/quit','/api/desktop\\quit','/api/desktop/quit%3fextra',
      '/api/desktop/quit%23extra','/api/desktop/quit%00']){
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
