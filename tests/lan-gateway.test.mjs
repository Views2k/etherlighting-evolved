import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {once} from 'node:events';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {createPrivateGateway} from '../preview/private-gateway.mjs';
import {createLanAccessManager} from '../preview/lan-access.mjs';
import {ROUTE_ACCESS} from '../preview/remote-routes.mjs';

// The LAN gate runs on a loopback listener. Each accepted connection is given the addresses it would have on the LAN,
// this PC at 192.168.1.2 and a phone at 10.0.0.1, before the gate's own connection filter reads them.
const PC='192.168.1.2',PHONE='10.0.0.1';
const PRIVATE_STATUS={available:true,enabled:true,ready:true,url:'https://ee.example.ts.net:8444',error:null,setup_action:'approve',
  action_url:'https://login.tailscale.com/a/fixture',audience:'owner',https_port:8444,can_change_port:true};
const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));
const filled=route=>route.replace(/\{[a-z_]+\}/g,'1');

// answerUpgrade(accept) decides when the upstream answers a WebSocket upgrade; by default it answers at once.
async function fixture(t,{answerUpgrade=accept=>accept(),serverOptions}={}){
  const received=[],upstreamSockets=new Set();
  const upstream=http.createServer((req,res)=>{
    received.push({method:req.method,url:req.url,headers:req.headers});
    res.setHeader('Set-Cookie','ee_admin_4180=fixture; Path=/api/private-access; HttpOnly; SameSite=Strict');req.resume();req.on('end',()=>res.end('ok'));
  });
  upstream.on('upgrade',(req,socket)=>{
    received.push({method:'WS',url:req.url,headers:req.headers});upstreamSockets.add(socket);
    socket.on('error',()=>{});socket.on('close',()=>upstreamSockets.delete(socket));
    answerUpgrade(()=>{socket.write('HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n');socket.pipe(socket);});
  });
  const targetPort=await listen(upstream);
  const gateway=createPrivateGateway({targetPort,listenPort:0,status:()=>({...PRIVATE_STATUS}),serverOptions,maxConnections:64,maxConnectionsPerAddress:16});
  const directory=await mkdtemp(path.join(tmpdir(),'ee-lan-gateway-'));
  const state={port:0,remote:PHONE};
  const lan=createLanAccessManager({configPath:path.join(directory,'lan-access.json'),interfaces:()=>({Ethernet:[{family:'IPv4',internal:false,address:PC}]}),
    startListener:async gate=>{
      ({port:state.port}=await gateway.start({...gate,acceptConnection:socket=>{
        Object.defineProperty(socket,'localAddress',{value:PC});Object.defineProperty(socket,'remoteAddress',{value:state.remote});
        return gate.acceptConnection(socket);
      }},0));
    },
    stopListener:()=>gateway.stop(),recheck:()=>gateway.recheck()});
  t.after(async()=>{
    await gateway.stop();for(const socket of upstreamSockets)socket.destroy();
    upstream.closeAllConnections();await new Promise(done=>upstream.close(done));await rm(directory,{recursive:true,force:true});
  });
  await lan.configure(true);
  const host=()=>`${PC}:${state.port}`,origin=()=>`http://${host()}`;
  function send(url,{method='GET',headers={},body}={}){
    return new Promise((resolve,reject)=>{
      const sent=Object.fromEntries(Object.entries({host:host(),origin:origin(),...headers}).filter(([,value])=>value!==undefined));
      const req=http.request({host:'127.0.0.1',port:state.port,path:url,method,agent:false,headers:sent},response=>{
        let text='';response.setEncoding('utf8');response.on('data',data=>text+=data);
        response.on('end',()=>resolve({status:response.statusCode,headers:response.headers,text}));
      });
      req.on('error',reject);req.end(body);
    });
  }
  function upgrade(headers={},url='/api/ws'){
    return new Promise((resolve,reject)=>{
      const req=http.request({host:'127.0.0.1',port:state.port,path:url,agent:false,
        headers:{host:host(),origin:origin(),Connection:'Upgrade',Upgrade:'websocket',...headers}});
      req.once('upgrade',(response,socket)=>{socket.on('error',()=>{});resolve({status:response.statusCode,socket});});
      req.once('response',response=>{response.resume();resolve({status:response.statusCode});});
      req.once('error',reject);req.end();
    });
  }
  async function pairDevice(name='Kitchen iPad'){
    await lan.issueCode();
    const code=lan.status({admin:true}).pairing.code;
    const paired=await send('/api/lan-access/pair',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({code,name})});
    assert.equal(paired.status,200);
    return {cookie:paired.headers['set-cookie'][0].split(';')[0],id:JSON.parse(paired.text).device.id};
  }
  return {lan,state,received,send,upgrade,pairDevice,origin};
}
const body=response=>JSON.parse(response.text);
const PAIRING_REQUIRED={detail:{code:'lan_pairing_required',message:'Pair this device first. Enter the pairing code shown on the computer running EE.'}};
const LOCAL_ONLY={detail:{code:'local_access_required',message:'This action is available only on the computer running EE.'}};

test('an unpaired device gets workspace pages and paired false, and nothing else reaches the upstream',async t=>{
  const f=await fixture(t);
  for(const url of ['/','/preview/app.mjs'])assert.equal((await f.send(url)).status,200,url);
  assert.equal((await f.send('/assets/logo.png',{method:'HEAD'})).status,200);
  assert.deepEqual(body(await f.send('/api/workspace')),{live:true,remote:true,lan:true,paired:false});
  for(const [url,method] of [['/api/state','GET'],['/api/scene','PUT'],['/api/private-access','GET'],['/api/session/restore','POST'],['/','POST'],
    ['/api/lan-access','GET'],['/api/workspace/close','POST']]){
    const refused=await f.send(url,{method});
    assert.equal(refused.status,403,url);assert.deepEqual(body(refused),PAIRING_REQUIRED,url);
  }
  assert.deepEqual(f.received.map(item=>`${item.method} ${item.url}`),['GET /','GET /preview/app.mjs','HEAD /assets/logo.png']);
});

test('an administration link from an unpaired device is forwarded with an Origin and redeems nothing',async t=>{
  const f=await fixture(t);
  const opened=await f.send(`/?admin_code=${'a'.repeat(64)}`,{headers:{origin:undefined}});
  assert.equal(opened.status,200);assert.equal(opened.headers['set-cookie'],undefined);
  const [forwarded]=f.received;
  assert.equal(forwarded.url,`/?admin_code=${'a'.repeat(64)}`);assert.match(forwarded.headers.origin,/^http:\/\/127\.0\.0\.1:\d+$/);
});

test('an unpaired device cannot open the live stream',async t=>{
  const f=await fixture(t);
  assert.equal((await f.upgrade()).status,403);
  assert.deepEqual(f.received,[]);
});

test('a paired device follows every route entry exactly as a device on Tailscale does',async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  for(const {method,path:route,remote} of ROUTE_ACCESS.routes.filter(entry=>entry.method!=='WS')){
    const url=filled(route),before=f.received.length,response=await f.send(url,{method,headers:{cookie}});
    if(url==='/api/lan-access/pair'){
      // The LAN gateway answers pairing itself, before the route table.
      assert.equal(response.status,400);assert.equal(body(response).detail.code,'invalid');assert.equal(f.received.length,before);continue;
    }
    if(!remote){assert.equal(response.status,403,`${method} ${url}`);assert.deepEqual(body(response),LOCAL_ONLY,`${method} ${url}`);assert.equal(f.received.length,before,url);continue;}
    assert.equal(response.status,200,`${method} ${url}`);
    if(url==='/api/workspace')assert.deepEqual(body(response),{live:true,remote:true,lan:true,paired:true});
    if(['/api/private-access','/api/workspace'].includes(url))assert.equal(f.received.length,before,url);
    else assert.deepEqual([f.received.at(-1).method,f.received.at(-1).url],[method,url]);
  }
  const socket=await f.upgrade({cookie});
  assert.equal(socket.status,101);socket.socket.destroy();
});

test('LAN settings, administration-key and Tailscale settings routes are refused for a paired device',async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  for(const [url,method] of [['/api/lan-access','GET'],['/api/lan-access','PUT'],['/api/lan-access/code','POST'],['/api/lan-access/devices/3f9c0a1b2c3d4e5f','DELETE'],
    ['/api/workspace/admin-link','POST'],['/api/workspace/close','POST'],['/api/private-access','PUT'],['/api/private-access/login','POST'],
    ['/api/private-access/refresh','POST'],['/api/session/login','POST'],['/api/controller/connect','POST']]){
    const refused=await f.send(url,{method,headers:{cookie}});
    assert.equal(refused.status,403,url);assert.deepEqual(body(refused),LOCAL_ONLY,url);
  }
  assert.deepEqual(f.received,[]);
});

test('the device cookie and administration key never reach the upstream, and Set-Cookie never comes back',async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  const response=await f.send('/api/state',{headers:{cookie:`${cookie}; ee_admin_4180=fixture`,'x-ee-admin-key':'a'.repeat(64),'x-ee-workspace':'current'}});
  assert.equal(response.status,200);assert.equal(response.headers['set-cookie'],undefined);
  const [forwarded]=f.received;
  assert.equal(forwarded.headers.cookie,undefined);assert.equal(forwarded.headers['x-ee-admin-key'],undefined);
  assert.equal(forwarded.headers['x-ee-workspace'],'current');assert.match(forwarded.headers.host,/^127\.0\.0\.1:\d+$/);
  const socket=await f.upgrade({cookie});socket.socket.destroy();
  assert.equal(f.received.at(-1).headers.cookie,undefined);
});

test('a request naming another host is refused before it reaches the upstream',async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  for(const host of ['evil.example:4280',`127.0.0.1:${f.state.port}`,`${PC}:1`]){
    const refused=await f.send('/api/state',{headers:{cookie,host,origin:`http://${host}`}});
    assert.equal(refused.status,403,host);assert.equal(body(refused).detail.code,'lan_access_denied');
  }
  const encoded=await f.send('/api/%2e%2e/state',{headers:{cookie}});
  assert.deepEqual(body(encoded),{detail:{code:'lan_access_denied',message:'This request cannot be opened through LAN access.'}});
  assert.equal((await f.upgrade({cookie,host:'evil.example:4280',origin:'http://evil.example:4280'})).status,403);
  assert.deepEqual(f.received,[]);
});

test('removing a device closes its WebSocket before the removal is confirmed',{timeout:5000},async t=>{
  const f=await fixture(t),removed=await f.pairDevice(),kept=await f.pairDevice('Office PC');
  const [gone,stays]=await Promise.all([f.upgrade({cookie:removed.cookie}),f.upgrade({cookie:kept.cookie})]);
  assert.equal(gone.status,101);assert.equal(stays.status,101);gone.socket.resume();
  // The periodic check runs every 15 seconds, so only the check that the removal runs closes it within the time limit.
  const closed=once(gone.socket,'close');
  await f.lan.revoke(removed.id);
  await closed;
  const echo=once(stays.socket,'data');stays.socket.write('still connected');
  assert.equal((await echo)[0].toString(),'still connected');
  stays.socket.destroy();
  assert.equal((await f.send('/api/state',{headers:{cookie:removed.cookie}})).status,403);
});

test('a device removed while EE has not yet answered its WebSocket upgrade does not get the socket',{timeout:5000},async t=>{
  let arrived;
  const waiting=new Promise(resolve=>{arrived=resolve;});
  const f=await fixture(t,{answerUpgrade:arrived}),{cookie,id}=await f.pairDevice();
  // The handler is attached now: the gateway may close the socket while the removal is still being saved.
  const refused=assert.rejects(f.upgrade({cookie}));
  const accept=await waiting;
  await f.lan.revoke(id);
  // EE answers only after the removal is confirmed, so the 101 would reach the device only if the removal missed the socket.
  accept();
  await refused;
});

test('turning LAN access off closes every connection',{timeout:5000},async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  const socket=await f.upgrade({cookie});
  const idle=net.connect(f.state.port,'127.0.0.1');idle.on('error',()=>{});await once(idle,'connect');
  const closed=[once(socket.socket,'close'),once(idle,'close')];
  await f.lan.configure(false);
  await Promise.all(closed);
  await assert.rejects(f.send('/api/state',{headers:{cookie}}));
});

test('a 17th connection from one address is closed',{timeout:5000},async t=>{
  const f=await fixture(t);
  const open=async()=>{const socket=net.connect(f.state.port,'127.0.0.1');socket.on('error',()=>{});await once(socket,'connect');return socket;};
  const sockets=[];
  t.after(()=>{for(const socket of sockets)socket.destroy();});
  for(let index=0;index<16;index++)sockets.push(await open());
  const extra=await open();sockets.push(extra);
  await once(extra,'close');
  assert.equal(sockets.slice(0,16).every(socket=>!socket.destroyed),true);
  f.state.remote=[10,0,0,9].join('.');
  assert.equal((await f.send('/')).status,200);
});

test('bodies over 1 MiB and pairing bodies over 1 KB stop at the LAN gateway',{timeout:5000},async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  const agent=new http.Agent({keepAlive:true});t.after(()=>agent.destroy());
  const large=await new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port:f.state.port,path:'/api/rules',method:'PUT',agent,
      headers:{host:`${PC}:${f.state.port}`,origin:f.origin(),cookie,'content-length':String(1048577)}},response=>{
      let text='';response.on('data',data=>text+=data);response.on('end',()=>resolve({status:response.statusCode,text}));
    });
    req.on('error',reject);req.end(Buffer.alloc(1048577,'a'));
  });
  assert.equal(large.status,413);assert.deepEqual(JSON.parse(large.text),{detail:{code:'too_large',message:'The request is too large.'}});
  const streamed=await new Promise(resolve=>{
    const req=http.request({host:'127.0.0.1',port:f.state.port,path:'/api/rules',method:'PUT',agent:false,
      headers:{host:`${PC}:${f.state.port}`,origin:f.origin(),cookie}},response=>{response.resume();response.on('end',()=>resolve(response.statusCode));});
    req.on('error',error=>resolve(error.code));
    const chunk=Buffer.alloc(600*1024,'a');req.write(chunk);req.end(chunk);
  });
  assert.equal(typeof streamed,'string');
  const pairing=await f.send('/api/lan-access/pair',{method:'POST',headers:{'content-length':'1025'},body:'a'.repeat(1025)});
  assert.equal(pairing.status,413);
  assert.equal(f.received.some(item=>item.url==='/api/rules'&&item.method==='PUT'&&item.headers['content-length']==='1048577'),false);
});

// A pairing body that promises 100 bytes and sends 11. The socket stays open unless close is true.
async function partialPairing(f,close){
  const socket=net.connect(f.state.port,'127.0.0.1');socket.on('error',()=>{});await once(socket,'connect');
  const closed=once(socket,'close');socket.resume();
  socket.write(`POST /api/lan-access/pair HTTP/1.1\r\nHost: ${PC}:${f.state.port}\r\nOrigin: ${f.origin()}\r\n`
    +'Content-Type: application/json\r\nContent-Length: 100\r\n\r\n{"code":"12');
  if(close)setTimeout(()=>socket.destroy(),50);
  await closed;
}

test('a pairing request that closes before its body ends leaves the gateway running',{timeout:5000},async t=>{
  const f=await fixture(t);
  await partialPairing(f,true);
  assert.deepEqual(body(await f.send('/api/workspace')),{live:true,remote:true,lan:true,paired:false});
  await f.pairDevice();
});

test('a pairing request that stalls past the request timeout leaves the gateway running',{timeout:5000},async t=>{
  const f=await fixture(t,{serverOptions:{requestTimeout:300,headersTimeout:200,connectionsCheckingInterval:50}});
  await partialPairing(f,false);
  assert.deepEqual(body(await f.send('/api/workspace')),{live:true,remote:true,lan:true,paired:false});
  await f.pairDevice();
});

test('the read-only private access answer has no local field',async t=>{
  const f=await fixture(t),{cookie}=await f.pairDevice();
  const status=body(await f.send('/api/private-access',{headers:{cookie}}));
  assert.deepEqual(status,{...PRIVATE_STATUS,can_configure:false,can_close_workspace:false,can_change_port:false,setup_action:null,action_url:null});
  assert.equal(Object.hasOwn(status,'local'),false);
  assert.deepEqual(f.received,[]);
});
