import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {registerPwa} from '../preview/pwa.mjs';

const root = new URL('../',import.meta.url);
const source = await readFile(new URL('preview/sw.mjs',root),'utf8');

function worker(fetchResult = async () => new Response('Online workspace')) {
  const listeners = new Map(),stores = new Map(),requests = [],deleted = [];
  const caches = {
    async open(name) {
      if (!stores.has(name)) stores.set(name,new Map());
      const store=stores.get(name);
      return {async put(key,response){store.set(key,response.clone());},async match(key){return store.get(key)?.clone();}};
    },
    async keys(){return [...stores.keys()];},
    async delete(name){deleted.push(name);return stores.delete(name);}
  };
  vm.runInNewContext(source,{self:{location:{origin:'https://ee.example'},addEventListener:(name,callback)=>listeners.set(name,callback)},
    caches,URL,Response,fetch:async request=>{requests.push(request);return fetchResult(request);}});
  return {listeners,stores,requests,deleted,
    async lifecycle(name){let pending;listeners.get(name)({waitUntil:value=>{pending=value;}});await pending;},
    navigate(path='/',method='GET',mode='navigate'){
      let response;
      listeners.get('fetch')({request:{url:new URL(path,'https://ee.example').href,method,mode},respondWith:value=>{response=value;}});
      return response;
    }};
}

test('manifest references real icon dimensions and launches the scoped standalone workspace',async()=>{
  const manifest=JSON.parse(await readFile(new URL('preview/manifest.webmanifest',root),'utf8'));
  assert.equal(manifest.display,'standalone');assert.equal(manifest.start_url,'/#lighting');assert.equal(manifest.scope,'/');
  assert.deepEqual(manifest.icons.map(icon=>icon.sizes),['192x192','512x512']);
  for (const icon of [...manifest.icons,{src:'/assets/pwa-180.png',sizes:'180x180'}]) {
    const bytes=await readFile(new URL(icon.src.slice(1),root)),[width,height]=icon.sizes.split('x').map(Number);
    assert.equal(bytes.subarray(1,4).toString(),'PNG');assert.equal(bytes.readUInt32BE(16),width);assert.equal(bytes.readUInt32BE(20),height);
  }
  const html=await readFile(new URL('preview/index.html',root),'utf8');
  assert.match(html,/rel="manifest" href="\/preview\/manifest.webmanifest"/);
  assert.match(html,/rel="apple-touch-icon" sizes="180x180" href="\/assets\/pwa-180.png"/);
  assert.match(html,/src="\/preview\/pwa.mjs"/);
  const viewport = html.match(/<meta name="viewport" content="([^"]+)"/)?.[1];
  assert.match(viewport, /\bwidth=device-width\b/);
  const fit = viewport.split(',').map(item => item.trim()).find(item => item.startsWith('viewport-fit='));
  assert.ok(fit === undefined || fit === 'viewport-fit=auto', 'Use native viewport sizing for Home Screen launch');
});

test('registration requires a secure context and uses only the same-origin module worker',async()=>{
  const calls=[],register=async(...args)=>{calls.push(args);return {scope:'https://ee.example/'};};
  assert.equal(await registerPwa({isSecureContext:false,navigator:{serviceWorker:{register}}}),null);
  assert.equal(await registerPwa({isSecureContext:true,navigator:{}}),null);
  const result=await registerPwa({isSecureContext:true,navigator:{serviceWorker:{register}}});
  assert.equal(result.scope,'https://ee.example/');
  assert.deepEqual(calls,[['/preview/sw.mjs',{type:'module',scope:'/',updateViaCache:'none'}]]);
});

test('installation stores only the versioned static offline page without fetching a workspace',async()=>{
  const harness=worker();await harness.lifecycle('install');
  assert.equal(harness.requests.length,0);assert.equal(harness.stores.size,1);
  const entries=[...harness.stores.values()][0];assert.equal(entries.size,1);
  const [url,response]=[...entries][0];assert.match(url,/offline\.html\?ee-static=v1$/);
  const html=await response.text();assert.match(html,/Reconnect to your workspace/);assert.match(html,/<a href="\/">Try again<\/a>/);
  assert.doesNotMatch(html,/<script|<form|\/api\/|password/i);
  assert.equal(response.headers.get('cache-control'),'no-store');
});

test('online navigation always reads the server and never stores returned workspace content',async()=>{
  const harness=worker(async()=>new Response('Private live page'));await harness.lifecycle('install');
  for(const path of ['/','/?source=homescreen','/preview/index.html'])assert.equal(await (await harness.navigate(path)).text(),'Private live page');
  assert.equal(harness.requests.length,3);
  for(const store of harness.stores.values())for(const response of store.values())assert.doesNotMatch(await response.clone().text(),/Private live page/);
});

test('offline navigation offers a static retry page without loading stale controls',async()=>{
  for(const fetchResult of [async()=>{throw new TypeError('Offline');},async()=>new Response('Unavailable',{status:503})]){
    const harness=worker(fetchResult);await harness.lifecycle('install');
    const response=await harness.navigate();assert.equal(response.status,503);assert.match(await response.text(),/Try again/);
    assert.equal(harness.requests.length,1);
  }
  const fresh=worker(async()=>{throw new TypeError('Offline');});
  assert.match(await (await fresh.navigate()).text(),/Reconnect to your workspace/);
});

test('API, mutation, credential, telemetry, external and static requests are never intercepted',async()=>{
  const harness=worker();await harness.lifecycle('install');
  for(const [path,method,mode] of [
    ['/api/state','GET','navigate'],['/api/session','GET','cors'],['/api/session/login','POST','cors'],
    ['/api/scene','PUT','cors'],['/api/ws','GET','websocket'],['/','POST','navigate'],
    ['/preview/app.mjs','GET','cors'],['/assets/logo.png','GET','no-cors'],['https://other.example/','GET','navigate']
  ])assert.equal(harness.navigate(path,method,mode),undefined,`${method} ${path}`);
  assert.equal(harness.requests.length,0);
});

test('activation removes only old offline-cache versions and does not seize open clients',async()=>{
  const harness=worker();harness.stores.set('ee-pwa-static-v0',new Map());harness.stores.set('other-app-data',new Map());
  await harness.lifecycle('install');await harness.lifecycle('activate');
  assert.deepEqual(harness.deleted,['ee-pwa-static-v0']);assert.ok(harness.stores.has('other-app-data'));
  assert.doesNotMatch(source,/skipWaiting|clients\.claim|sync['"]|periodicsync|indexedDB/);
});

test('authentication failures are returned intact instead of being cached or hidden as offline',async()=>{
  const harness=worker(async()=>new Response('Sign in required',{status:401}));
  const response=await harness.navigate();assert.equal(response.status,401);assert.equal(await response.text(),'Sign in required');
  assert.equal(harness.stores.size,0);
});
