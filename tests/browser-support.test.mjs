import test from 'node:test';
import assert from 'node:assert/strict';
import {supportedBrowser,browserRecoveryMessage} from '../preview/browser-support.mjs';
const available=()=>({Object:{hasOwn(){}},Array:{prototype:{at(){}}},structuredClone(){},
  AbortSignal:Object.assign(function(){},{timeout(){}}),crypto:{randomUUID(){}},
  HTMLDialogElement:function(){},CSS:{supports:()=>true}});
const capable=()=>{const env=available();env.HTMLDialogElement.prototype.showModal=()=>{};return env;};
test('the interface loads only when required browser features are present',()=>{
  assert.equal(supportedBrowser(capable()),true);
  for(const key of ['structuredClone','AbortSignal','crypto','HTMLDialogElement','CSS']){
    const env=capable();delete env[key];assert.equal(supportedBrowser(env),false,key);
  }
  for(const feature of ['color','selector(:has(*))']){
    const env=capable();env.CSS.supports=value=>value!==feature;assert.equal(supportedBrowser(env),false);
  }
  const remote=capable();delete remote.crypto.randomUUID;assert.equal(supportedBrowser(remote),false);
});

test('insecure context guidance is distinct from missing browser features',()=>{
  const env=capable();env.isSecureContext=false;delete env.crypto.randomUUID;
  assert.match(browserRecoveryMessage(env),/private HTTPS address/);
  assert.doesNotMatch(browserRecoveryMessage(env),/Update/);
  env.isSecureContext=true;assert.match(browserRecoveryMessage(env),/Update/);
  assert.equal(browserRecoveryMessage({...capable(),isSecureContext:true}),null);
});
