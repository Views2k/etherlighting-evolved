import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {once} from 'node:events';
import {fileURLToPath} from 'node:url';
import {createWorkspaceServer} from '../preview/server.mjs';
import {createAdminAccess} from '../preview/admin-access.mjs';

const root=fileURLToPath(new URL('../',import.meta.url));
const host='127.0.0.1:4180';
const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));
const forbidden={detail:{code:'forbidden',message:'Requests from other websites are not allowed.'}};
const tooLarge={detail:{code:'too_large',message:'The request is too large.'}};

async function gateway(t){
  const received=[];
  const backend=http.createServer((req,res)=>{
    const item={url:req.url,method:req.method,headers:req.headers,bytes:0};received.push(item);
    req.on('data',chunk=>item.bytes+=chunk.length);req.on('end',()=>res.end('{}'));
  });
  const backendPort=await listen(backend);
  const {server}=createWorkspaceServer({root,port:4180,live:false,backendPort,token:'fixture-token',
    child:{exitCode:null,stdin:{writableEnded:false,end(){}}},diagnostics:{record(){}},
    privateAccess:{status:()=>({}),close:async()=>{}},admin:createAdminAccess({key:'a'.repeat(64),port:4180}),
    closeWorkspace:async()=>{},keepAvailable:()=>({observe(){},stop(){}})});
  const port=await listen(server);
  t.after(()=>Promise.all([server,backend].map(item=>{item.closeAllConnections();return new Promise(done=>item.close(done));})));
  return {port,backend,backendPort,received};
}

function request(port,path,{method='GET',headers={},body,agent=false}={}){
  return new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path,method,agent,headers:{host,...headers}},response=>{
      let text='';response.setEncoding('utf8');response.on('data',data=>text+=data);
      response.on('end',()=>resolve({status:response.statusCode,headers:response.headers,text}));
    });
    req.on('error',reject);req.end(body);
  });
}

function openSocket(port,headers={}){
  const client=net.connect(port,'127.0.0.1');
  client.on('error',()=>{});
  const lines=Object.entries({Host:host,Connection:'Upgrade',Upgrade:'websocket','Sec-WebSocket-Version':'13',
    'Sec-WebSocket-Key':'dGhlIHNhbXBsZSBub25jZQ==',...headers}).map(([name,value])=>`${name}: ${value}\r\n`);
  client.write(`GET /api/ws HTTP/1.1\r\n${lines.join('')}\r\n`);
  return client;
}

test('the gateway answers only its own host names and origins',async t=>{
  const f=await gateway(t);
  for(const headers of [{host:'evil.test:4180'},{host:'127.0.0.1:4181'},{host:'evil.test:4180',origin:`http://${host}`},
    {origin:'http://evil.test'},{origin:'http://127.0.0.1:4181'},{origin:'null'},{origin:`https://${host}`}]){
    const refused=await request(f.port,'/api/state',{method:'PUT',headers,body:'{}'});
    assert.equal(refused.status,403,JSON.stringify(headers));
    assert.deepEqual(JSON.parse(refused.text),forbidden,JSON.stringify(headers));
    assert.equal((await request(f.port,'/',{headers})).status,403,JSON.stringify(headers));
  }
  assert.equal(f.received.length,0);
  for(const headers of [{},{origin:`http://${host}`},{host:'localhost:4180',origin:'http://localhost:4180'},{origin:'http://localhost:4180'}])
    assert.equal((await request(f.port,'/api/state',{method:'PUT',headers,body:'{}'})).status,200,JSON.stringify(headers));
  assert.equal(f.received.length,4);
});

test('API requests reach the engine with its token and without the browser origin',async t=>{
  const f=await gateway(t);
  await request(f.port,'/api/state?view=full',{headers:{origin:`http://${host}`,authorization:'Bearer forged'}});
  const [{url,headers}]=f.received;
  assert.equal(url,'/api/state?view=full');
  assert.equal(headers.authorization,'Bearer fixture-token');
  assert.equal(headers.origin,undefined);
  assert.equal(headers.host,`127.0.0.1:${f.backendPort}`);
});

test('the administration key and browser cookies never reach the engine',async t=>{
  const f=await gateway(t),secrets={cookie:'ee_admin_4180=fixture; theme=dark','x-ee-admin-key':'a'.repeat(64)};
  await request(f.port,'/api/session',{headers:secrets});
  const [{headers}]=f.received;
  assert.equal(headers.cookie,undefined);assert.equal(headers['x-ee-admin-key'],undefined);
  assert.equal(headers.authorization,'Bearer fixture-token');
  const arrived=new Promise(resolve=>f.backend.once('upgrade',(req,socket)=>{socket.destroy();resolve(req.headers);}));
  const client=openSocket(f.port,{Cookie:secrets.cookie,'X-EE-Admin-Key':secrets['x-ee-admin-key']});
  const upgraded=await arrived;client.destroy();
  assert.equal(upgraded.cookie,undefined);assert.equal(upgraded['x-ee-admin-key'],undefined);
  assert.equal(upgraded.authorization,'Bearer fixture-token');
});

test('API requests answer 503 when the engine is not listening',async t=>{
  const f=await gateway(t);
  f.backend.closeAllConnections();await new Promise(done=>f.backend.close(done));
  const response=await request(f.port,'/api/state');
  assert.equal(response.status,503);
  assert.deepEqual(JSON.parse(response.text),{detail:'The EE service is unavailable.'});
});

test('API calls from another site are refused while links from other sites still open the page',async t=>{
  const f=await gateway(t);
  for(const site of ['cross-site','same-site']){
    const refused=await request(f.port,'/api/state',{headers:{'sec-fetch-site':site,'sec-fetch-mode':'no-cors'}});
    assert.equal(refused.status,403,site);
    assert.deepEqual(JSON.parse(refused.text),forbidden,site);
  }
  assert.equal(f.received.length,0);
  for(const site of ['same-origin','none'])assert.equal((await request(f.port,'/api/state',{headers:{'sec-fetch-site':site}})).status,200,site);
  assert.equal(f.received.length,2);
  assert.equal((await request(f.port,'/',{headers:{'sec-fetch-site':'cross-site','sec-fetch-mode':'navigate'}})).status,200);
});

test('request bodies over 1 MiB stop at the gateway',async t=>{
  const f=await gateway(t);
  // Browsers and the desktop client keep connections alive. A client that asks to close the connection can see a
  // reset instead of the 413 when it sends a large body, because Node closes the socket as soon as it answers.
  const agent=new http.Agent({keepAlive:true});t.after(()=>agent.destroy());
  for(const options of [{headers:{'content-length':String(1048577)}},{agent,body:Buffer.alloc(8*1048576,'a')}]){
    const refused=await request(f.port,'/api/rules',{method:'PUT',...options});
    assert.equal(refused.status,413);
    assert.equal(refused.headers['content-type'],'application/json');
    assert.deepEqual(JSON.parse(refused.text),tooLarge);
  }
  assert.equal(f.received.length,0);
  assert.equal((await request(f.port,'/api/rules',{method:'PUT',body:Buffer.alloc(1048576,'a')})).status,200);
  assert.equal(f.received[0].bytes,1048576);
  const streamed=await new Promise(resolve=>{
    const req=http.request({host:'127.0.0.1',port:f.port,path:'/api/rules',method:'PUT',agent:false,headers:{host}},response=>resolve(response.statusCode));
    req.on('error',error=>resolve(error.code));
    const chunk=Buffer.alloc(600*1024,'a');req.write(chunk);req.end(chunk);
  });
  assert.equal(typeof streamed,'string');
  assert.equal((await request(f.port,'/api/workspace')).status,200);
});

test('the demo gateway answers private access status itself and says it is on this computer',async t=>{
  const f=await gateway(t);
  assert.equal(JSON.parse((await request(f.port,'/api/private-access')).text).local,true);
  assert.equal(f.received.length,0);
});

test('static files are served only from the published folders',async t=>{
  const f=await gateway(t);
  for(const path of ['/','/preview/app.mjs','/src/color.mjs','/assets/favicon.png'])assert.equal((await request(f.port,path)).status,200,path);
  assert.match((await request(f.port,'/')).headers['content-security-policy'],/object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'/);
  for(const path of ['/preview/..%2fpackage.json','/preview/..%2ftools%2fcheck.mjs','/src/..%5c..%5cpackage.json','/src/..%5ctools%5ccheck.mjs',
    '/assets/%00.png','/preview/%E0%A4%A.mjs','/previewx/app.mjs','/tools/check.mjs','/preview/','/preview/style.css.map'])
    assert.equal((await request(f.port,path)).status,404,path);
  assert.equal((await request(f.port,'/preview/app.mjs',{method:'POST'})).status,405);
  assert.equal(f.received.length,0);
});

test('a client that resets before the engine accepts its WebSocket leaves the gateway running',async t=>{
  const f=await gateway(t);
  const arrived=new Promise(resolve=>f.backend.once('upgrade',(req,socket)=>{socket.on('error',()=>{});socket.resume();resolve({req,socket});}));
  const client=openSocket(f.port,{Origin:`http://${host}`});
  const {req,socket}=await arrived;
  assert.equal(req.headers.authorization,'Bearer fixture-token');assert.equal(req.headers.origin,undefined);
  client.resetAndDestroy();
  await once(socket,'end');socket.destroy();
  assert.equal((await request(f.port,'/api/workspace')).status,200);
});

test('WebSocket upgrades from another host, origin or site never reach the engine',async t=>{
  const f=await gateway(t);
  let upgrades=0;f.backend.on('upgrade',(_req,socket)=>{upgrades++;socket.destroy();});
  for(const headers of [{Origin:'http://evil.test'},{Origin:`http://${host}`,Host:'evil.test:4180'},{Origin:`http://${host}`,'Sec-Fetch-Site':'cross-site'}]){
    const client=openSocket(f.port,headers);
    await once(client,'close');
  }
  assert.equal(upgrades,0);
});
