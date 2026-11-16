import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import {once} from 'node:events';
import {fileURLToPath} from 'node:url';
import {createWorkspaceServer} from '../preview/server.mjs';

const root=fileURLToPath(new URL('../',import.meta.url));
const host='127.0.0.1:4180';
const listen=server=>new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));

async function gateway(t){
  const received=[];
  const backend=http.createServer((req,res)=>{req.resume();received.push({url:req.url,method:req.method,headers:req.headers});res.end('{}');});
  const backendPort=await listen(backend);
  const {server}=createWorkspaceServer({root,port:4180,live:false,backendPort,token:'fixture-token',
    child:{exitCode:null,stdin:{writableEnded:false,end(){}}},diagnostics:{record(){}},
    privateAccess:{status:()=>({}),close:async()=>{}},closeWorkspace:async()=>{},keepAvailable:()=>({observe(){},stop(){}})});
  const port=await listen(server);
  t.after(()=>Promise.all([server,backend].map(item=>{item.closeAllConnections();return new Promise(done=>item.close(done));})));
  return {port,backend,backendPort,received};
}

function request(port,path,{method='GET',headers={},body}={}){
  return new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path,method,agent:false,headers:{host,...headers}},response=>{
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
    assert.equal((await request(f.port,'/api/state',{method:'PUT',headers,body:'{}'})).status,403,JSON.stringify(headers));
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

test('API calls from another site are refused while links from other sites still open the page',async t=>{
  const f=await gateway(t);
  for(const site of ['cross-site','same-site'])
    assert.equal((await request(f.port,'/api/state',{headers:{'sec-fetch-site':site,'sec-fetch-mode':'no-cors'}})).status,403,site);
  assert.equal(f.received.length,0);
  for(const site of ['same-origin','none'])assert.equal((await request(f.port,'/api/state',{headers:{'sec-fetch-site':site}})).status,200,site);
  assert.equal(f.received.length,2);
  assert.equal((await request(f.port,'/',{headers:{'sec-fetch-site':'cross-site','sec-fetch-mode':'navigate'}})).status,200);
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
