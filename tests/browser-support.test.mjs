import test from 'node:test';
import assert from 'node:assert/strict';
import {readdir,readFile} from 'node:fs/promises';
import {supportedBrowser,browserRecoveryMessage} from '../preview/browser-support.mjs';
import {randomId} from '../preview/dom.mjs';
const available=()=>({Object:{hasOwn(){}},Array:{prototype:{at(){}}},structuredClone(){},
  AbortSignal:Object.assign(function(){},{timeout(){}}),crypto:{getRandomValues(){}},
  HTMLDialogElement:function(){},CSS:{supports:()=>true},location:{protocol:'http:',hostname:'127.0.0.1',port:'4180'}});
const capable=()=>{const env=available();env.HTMLDialogElement.prototype.showModal=()=>{};return env;};
const INSECURE='Open EE on this computer using localhost, or use the address shown in Settings on the computer running EE.';
test('the interface loads only when required browser features are present',()=>{
  assert.equal(supportedBrowser(capable()),true);
  for(const key of ['structuredClone','AbortSignal','crypto','HTMLDialogElement','CSS']){
    const env=capable();delete env[key];assert.equal(supportedBrowser(env),false,key);
  }
  for(const feature of ['color','selector(:has(*))']){
    const env=capable();env.CSS.supports=value=>value!==feature;assert.equal(supportedBrowser(env),false);
  }
  for(const color of ['color(srgb 1 0 0)','color-mix(in srgb, black, white)']){
    const env=capable();env.CSS.supports=(property,value)=>value!==color;assert.equal(supportedBrowser(env),false,color);
  }
  const remote=capable();delete remote.crypto.getRandomValues;assert.equal(supportedBrowser(remote),false);
});

test('insecure context guidance is distinct from missing browser features',()=>{
  const env=capable();env.isSecureContext=false;delete env.crypto.getRandomValues;
  assert.equal(browserRecoveryMessage(env),INSECURE);
  env.isSecureContext=true;assert.match(browserRecoveryMessage(env),/Update/);
  assert.equal(browserRecoveryMessage({...capable(),isSecureContext:true}),null);
});

test('an insecure LAN page is supported, and other insecure pages get the address guidance',()=>{
  const lan={...capable(),isSecureContext:false,location:{protocol:'http:',hostname:'192.168.1.2',port:'4280'}};
  assert.equal(browserRecoveryMessage(lan),null);
  for(const location of [{protocol:'http:',hostname:'192.168.1.2',port:''},{protocol:'http:',hostname:'mypc.local',port:'4280'},
    {protocol:'http:',hostname:'8.8.8.8',port:'4280'},{protocol:'http:',hostname:'100.100.1.1',port:'4280'}])
    assert.equal(browserRecoveryMessage({...capable(),isSecureContext:false,location}),INSECURE,location.hostname);
});

test('the workspace uses getRandomValues for its ids, which plain HTTP pages also have',async()=>{
  const folder=new URL('../preview/',import.meta.url);
  for(const name of (await readdir(folder)).filter(name=>name.endsWith('.mjs')))
    assert.doesNotMatch(await readFile(new URL(name,folder),'utf8'),/randomUUID/,name);
  const ids=new Set(Array.from({length:20},randomId));
  assert.equal(ids.size,20);for(const id of ids)assert.match(id,/^[0-9a-f]{32}$/);
});
