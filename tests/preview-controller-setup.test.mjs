import test from 'node:test';
import assert from 'node:assert/strict';
import {renderControllerConnection,controllerConnectionProblem,controllerSupportedMethods,controllerError} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
const disconnected={configured:false,connected:false,controller_kind:null,sites:[],preferences_supported:true};
const credentials={controller_kind:'self_hosted',url:'https://fixture-controller:8443',username:'fixture-user',password:'fixture-secret'};
const supportedMethods={self_hosted:['password'],unifi_os:['password','api_key'],cloud_connector:['api_key']};
const keySupport={...disconnected,supported_auth_methods:supportedMethods,console_discovery_supported:true};

function controllerDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement,original=Object.getOwnPropertyDescriptor(globalThis,'Option');
  Object.defineProperty(globalThis,'Option',{configurable:true,value:function(text,value){const item=create('option');item.textContent=text;item.value=value;return item;}});
  context.after(()=>original?Object.defineProperty(globalThis,'Option',original):delete globalThis.Option);
  dom.doc.createElement=tag=>{
    const element=create(tag);element.classList.contains=name=>element.className.split(' ').includes(name);
    element.reportValidity=()=>true;
    if(tag==='form')Object.defineProperty(element,'elements',{get:()=>dom.findAll(element,item=>['input','button','select'].includes(item.tagName))});
    return element;
  };
  return {...dom,field:name=>dom.findAll(dom.host,item=>item.name===name)[0],button:label=>dom.findAll(dom.host,item=>item.tagName==='button'&&item.textContent===label)[0],
    form:()=>dom.findAll(dom.host,item=>item.tagName==='form')[0],submit:()=>dom.findAll(dom.host,item=>item.tagName==='form')[0].listeners.get('submit')({preventDefault(){}})};
}
function fill(dom,values=credentials){for(const [name,value] of Object.entries(values)){const field=dom.field(name);field.value=value;field.listeners.get(field.tagName==='select'?'change':'input')?.();}dom.form().listeners.get('input')();}

test('fresh controller validation requires type, a direct HTTPS origin, and both credentials',()=>{
  assert.equal(controllerConnectionProblem(credentials),null);
  assert.equal(controllerConnectionProblem({...credentials,controller_kind:'unifi_os',url:'https://fixture-console'}),null);
  for(const url of ['http://fixture','https://fixture/network/default','https://fixture/?site=default','https://fixture/#network','https://user:secret@fixture'])
    assert.equal(controllerConnectionProblem({...credentials,url}).field,'url',url);
  assert.equal(controllerConnectionProblem({...credentials,controller_kind:'other'}).field,'controller_kind');
  assert.equal(controllerConnectionProblem({...credentials,username:'   '}).field,'username');
  assert.equal(controllerConnectionProblem({...credentials,password:''}).field,'password');
  assert.equal(controllerConnectionProblem({...credentials,password:'  '}),null,'password whitespace must be preserved');
});

test('blank credentials reuse only a configured matching controller and kind',()=>{
  const current={configured:true,url:credentials.url,controller_kind:'self_hosted'},blank={...credentials,username:'',password:''};
  assert.equal(controllerConnectionProblem(blank,current),null);
  assert.equal(controllerConnectionProblem({...blank,url:credentials.url+'/'},current),null);
  assert.equal(controllerConnectionProblem({...blank,controller_kind:'unifi_os'},current).field,'username');
  assert.equal(controllerConnectionProblem({...blank,url:'https://different-controller'},current).field,'username');
  assert.equal(controllerConnectionProblem(blank,{...current,configured:false}).field,'username');
  assert.equal(controllerConnectionProblem({...blank,username:'other-user'},current).field,'password');
});

test('controller type changes preserve typed origin and login until explicit Connect',async context=>{
  const dom=controllerDom(context),sent=[];
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return disconnected;}});dom.after(cleanup);
  await flush();assert.equal(dom.field('controller_kind').value,'self_hosted');fill(dom);
  const kind=dom.field('controller_kind');kind.value='unifi_os';kind.listeners.get('change')();
  assert.equal(dom.field('url').value,credentials.url);assert.equal(dom.field('username').value,credentials.username);assert.equal(dom.field('password').value,credentials.password);
  assert.equal(dom.field('url').placeholder,'https://console');assert.equal(sent.length,1);
  dom.submit();await cleanup.canLeave();assert.equal(sent.length,2);assert.equal(sent[1][0],'/api/controller/connect');
  assert.equal(sent[1][2].controller_kind,'unifi_os');assert.equal(sent[1][2].url,credentials.url);assert.equal(sent[1][2].password,credentials.password);
});

test('restoring controller fields to their initial values clears unsaved changes',async context=>{
  const dom=controllerDom(context),cleanup=renderControllerConnection(dom.host,{request:async()=>({...disconnected,configured:true,url:credentials.url,controller_kind:'self_hosted'})});dom.after(cleanup);
  await flush();assert.equal(cleanup.hasChanges(),false);
  const kind=dom.field('controller_kind');kind.value='unifi_os';kind.listeners.get('change')();assert.equal(cleanup.hasChanges(),true);
  kind.value='self_hosted';kind.listeners.get('change')();assert.equal(cleanup.hasChanges(),false);
  const address=dom.field('url');address.value='https://different-controller';address.listeners.get('input')();dom.form().listeners.get('input')();assert.equal(cleanup.hasChanges(),true);
  address.value=credentials.url;address.listeners.get('input')();dom.form().listeners.get('input')();assert.equal(cleanup.hasChanges(),false);
  const password=dom.field('password');password.value='typed-secret';dom.form().listeners.get('input')();assert.equal(cleanup.hasChanges(),true);
  password.value='';dom.form().listeners.get('input')();assert.equal(cleanup.hasChanges(),false);
});

test('invalid fresh details focus the field without sending a login',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return disconnected;}});dom.after(cleanup);
  await flush();fill(dom,{...credentials,password:''});dom.submit();assert.equal(sent.length,1);assert.equal(dom.doc.activeElement,dom.field('password'));
  dom.field('password').value=credentials.password;dom.field('url').value='https://fixture/network';dom.submit();
  assert.equal(sent.length,1);assert.equal(dom.doc.activeElement,dom.field('url'));assert.equal(dom.field('password').value,credentials.password);
});

test('saved controller type is restored and its same-origin update can reuse the existing session',async context=>{
  const dom=controllerDom(context),sent=[],current={...disconnected,configured:true,connected:true,controller_kind:'unifi_os',url:'https://fixture-console'};
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return current;}});dom.after(cleanup);
  await flush();assert.equal(dom.field('controller_kind').value,'unifi_os');assert.equal(dom.field('url').value,current.url);
  dom.submit();await cleanup.canLeave();assert.equal(sent[1][2].username,'');assert.equal(sent[1][2].password,'');assert.equal(sent[1][2].controller_kind,'unifi_os');
});

test('changing controller type invalidates a certificate challenge without erasing credentials',async context=>{
  const dom=controllerDom(context),sent=[];
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);if(args[1]==='POST')throw {code:'certificate_untrusted',details:{certificate_sha256:'a'.repeat(64)}};return disconnected;
  }});dom.after(cleanup);
  await flush();fill(dom);dom.submit();await cleanup.canLeave();const trust=dom.button('Trust this certificate and connect');
  assert.equal(trust.parentNode.hidden,false);dom.field('controller_kind').value='unifi_os';dom.field('controller_kind').listeners.get('change')();
  assert.equal(trust.parentNode.hidden,true);trust.click();assert.equal(sent.length,2);assert.equal(dom.field('password').value,credentials.password);
  dom.submit();await cleanup.canLeave();assert.equal(sent[2][2].certificate_sha256,null);assert.equal(sent[2][2].controller_kind,'unifi_os');
});

test('certificate approval reconnects with the reviewed fingerprint and selected controller type',async context=>{
  const dom=controllerDom(context),sent=[],selected={...credentials,controller_kind:'unifi_os',url:'https://fixture-console'};
  const connected={...disconnected,configured:true,connected:true,controller_kind:selected.controller_kind,url:selected.url,pending_site_choice:null};
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);if(args[1]!=='POST')return disconnected;
    if(!args[2].certificate_sha256)throw {code:'certificate_untrusted',details:{certificate_sha256:'a'.repeat(64)}};
    return connected;
  }});dom.after(cleanup);
  await flush();fill(dom,selected);dom.submit();await cleanup.canLeave();const trust=dom.button('Trust this certificate and connect');
  assert.equal(trust.parentNode.hidden,false);trust.click();await cleanup.canLeave();
  assert.equal(sent.length,3);assert.equal(sent[2][2].certificate_sha256,'a'.repeat(64));assert.equal(sent[2][2].controller_kind,'unifi_os');
  assert.equal(dom.field('password').value,'');assert.equal(dom.field('username').value,'');assert.equal(trust.parentNode.hidden,true);
  assert.equal(cleanup.hasChanges(),false);assert.equal(dom.byClass(dom.host,'connection-state')[0].textContent,'Controller connected');
  dom.submit();await cleanup.canLeave();assert.equal(sent[3][2].username,'');assert.equal(sent[3][2].password,'');
});

const workingController={...disconnected,configured:true,connected:true,controller_kind:'self_hosted',url:'https://working-controller:8443',site:'old-site',sites:[{id:'old-site',name:'Working site'}],mapped_ports:8,can_save_login:true};
const candidate={url:'https://candidate-console',controller_kind:'unifi_os',sites:[{id:'site-one',name:'First site'},{id:'site-two',name:'Second site'}]};
const pendingController={...workingController,pending_site_choice:candidate,can_save_login:false};

test('a pending replacement keeps the active controller status and credentials until a site is explicitly selected',async context=>{
  const dom=controllerDom(context),sent=[];
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);if(args[1]!=='POST')return workingController;
    return args[2].site?{...workingController,...candidate,site:args[2].site,pending_site_choice:null}:pendingController;
  }});dom.after(cleanup);
  await flush();fill(dom,{...credentials,url:candidate.url,controller_kind:candidate.controller_kind});dom.submit();await cleanup.canLeave();
  assert.equal(dom.byClass(dom.host,'connection-state')[0].textContent,'Controller connected');
  assert.ok(dom.findAll(dom.host,item=>item.tagName==='dd'&&item.textContent==='Working site').length);
  assert.equal(dom.field('url').value,candidate.url);assert.equal(dom.field('password').value,credentials.password);assert.equal(cleanup.hasChanges(),true);
  assert.equal(dom.field('site').required,true);assert.equal(dom.field('site').value,'');assert.equal(dom.doc.activeElement,dom.field('site'));
  assert.deepEqual(dom.field('site').children.map(item=>item.value),['','site-one','site-two']);
  assert.equal(dom.button('Cancel connection change').hidden,false);assert.match(dom.byClass(dom.host,'connection-notice')[0].textContent,/finish this connection change/);
  dom.submit();assert.equal(sent.length,2,'blank candidate site must not send another request');
  dom.field('site').value='site-two';dom.submit();await cleanup.canLeave();
  assert.equal(sent[2][2].site,'site-two');assert.equal(dom.field('password').value,'');assert.equal(dom.button('Cancel connection change').hidden,true);
  assert.equal(cleanup.hasChanges(),false);
});

test('reopened pending site choice can reuse the candidate login and preserves its selection on status updates',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return pendingController;}});dom.after(cleanup);
  await flush();assert.equal(dom.field('url').value,candidate.url);assert.equal(dom.field('controller_kind').value,'unifi_os');
  assert.equal(controllerConnectionProblem({...credentials,url:candidate.url,controller_kind:'unifi_os',username:'',password:''},pendingController),null);
  dom.field('site').value='site-two';cleanup.update({...pendingController,mapped_ports:7});assert.equal(dom.field('site').value,'site-two');
  dom.submit();await cleanup.canLeave();assert.equal(sent[1][2].site,'site-two');assert.equal(sent[1][2].username,'');assert.equal(sent[1][2].password,'');
});

test('canceling the pending candidate preserves active status and returns only its matching form to that connection',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);return args[0].endsWith('/cancel')?{...workingController,pending_site_choice:null}:pendingController;
  }});dom.after(cleanup);
  await flush();dom.field('password').value='candidate-secret';dom.form().listeners.get('input')();
  dom.button('Cancel connection change').click();await cleanup.canLeave();
  assert.deepEqual(sent[1],['/api/controller/cancel','POST']);assert.equal(dom.field('url').value,workingController.url);assert.equal(dom.field('controller_kind').value,'self_hosted');
  assert.equal(dom.field('password').value,'');assert.equal(dom.button('Cancel connection change').hidden,true);assert.equal(cleanup.hasChanges(),false);
  assert.equal(dom.byClass(dom.host,'connection-state')[0].textContent,'Controller connected');assert.equal(dom.doc.activeElement,dom.button('Update connection'));
});

test('canceling an older candidate keeps a newly typed unrelated connection draft',async context=>{
  const dom=controllerDom(context),cleanup=renderControllerConnection(dom.host,{request:async path=>path.endsWith('/cancel')?{...workingController,pending_site_choice:null}:pendingController});dom.after(cleanup);
  await flush();fill(dom,{...credentials,url:'https://third-controller:9443'});assert.equal(dom.field('site').parentNode.hidden,true);
  cleanup.update({...pendingController,mapped_ports:7});assert.equal(dom.field('site').parentNode.hidden,true,'old candidate choices must not reappear for another form origin');
  dom.button('Cancel connection change').click();await cleanup.canLeave();
  assert.equal(dom.field('url').value,'https://third-controller:9443');assert.equal(dom.field('password').value,credentials.password);assert.equal(cleanup.hasChanges(),true);
});

test('failed candidate cancellation retains the site choice and form for a retry',async context=>{
  const dom=controllerDom(context),cleanup=renderControllerConnection(dom.host,{request:async path=>{if(path.endsWith('/cancel'))throw {code:'unreachable'};return pendingController;}});dom.after(cleanup);
  await flush();dom.field('site').value='site-two';dom.field('password').value='candidate-secret';dom.form().listeners.get('input')();
  dom.button('Cancel connection change').click();await cleanup.canLeave();
  assert.equal(dom.field('site').value,'site-two');assert.equal(dom.field('password').value,'candidate-secret');assert.equal(dom.button('Cancel connection change').hidden,false);
  assert.equal(dom.byClass(dom.host,'connection-state')[0].textContent,'Controller connected');assert.match(dom.byClass(dom.host,'connection-notice')[0].textContent,/could not be reached/);
});

test('controller login failures keep the form and provide fixed local-account guidance',async context=>{
  const dom=controllerDom(context),cleanup=renderControllerConnection(dom.host,{request:async(_path,method)=>{
    if(method==='POST')throw {code:'controller_local_account_required',message:'private details'};return disconnected;
  }});dom.after(cleanup);
  await flush();fill(dom);dom.submit();await cleanup.canLeave();assert.equal(dom.field('password').value,credentials.password);
  const text=dom.byClass(dom.host,'connection-notice')[0].textContent;assert.equal(text,'Use a local UniFi account with Network access.');
  assert.doesNotMatch(text,/private details/);assert.match(controllerError({code:'unsupported_controller'}),/controller type/);
});

test('new authentication options require explicit backend support',async context=>{
  assert.deepEqual(controllerSupportedMethods(),{self_hosted:['password'],unifi_os:['password'],cloud_connector:[]});
  assert.deepEqual(controllerSupportedMethods({supported_auth_methods:{self_hosted:['api_key'],unifi_os:['password','api_key','other'],cloud_connector:['api_key']}}),{self_hosted:[],unifi_os:['password','api_key'],cloud_connector:['api_key']});
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return disconnected;}});dom.after(cleanup);
  await flush();assert.deepEqual(dom.field('controller_kind').children.map(item=>item.value),['self_hosted','unifi_os']);
  fill(dom,{...credentials,controller_kind:'unifi_os',auth_method:'api_key',api_key:'fixture-key'});dom.submit();assert.equal(sent.length,1);
  cleanup.update(keySupport);assert.equal(dom.field('auth_method').parentNode.hidden,true,'a dirty unsupported draft is not silently replaced');
});

test('key validation binds stored credentials to method and cloud console identity',()=>{
  const local={url:'https://fixture-console',controller_kind:'unifi_os',auth_method:'api_key',api_key:'fixture-key'};
  assert.equal(controllerConnectionProblem(local,keySupport),null);
  for(const key of ['', 'spaces are invalid', 'x'.repeat(4097), 'key\n'])assert.equal(controllerConnectionProblem({...local,api_key:key},keySupport).field,'api_key');
  assert.equal(controllerConnectionProblem({...local,controller_kind:'self_hosted'},keySupport).field,'auth_method');
  const cloud={...local,url:'https://api.ui.com',controller_kind:'cloud_connector',console_id:'fixture-console:123'};
  assert.equal(controllerConnectionProblem(cloud,keySupport),null);
  assert.equal(controllerConnectionProblem({...cloud,url:'https://other.example'},keySupport).field,'url');
  assert.equal(controllerConnectionProblem({...cloud,console_id:'../invalid'},keySupport).field,'console_id');
  const existing={...keySupport,configured:true,...cloud};
  assert.equal(controllerConnectionProblem({...cloud,api_key:''},existing),null);
  assert.equal(controllerConnectionProblem({...cloud,api_key:'',console_id:'different-console'},existing).field,'api_key');
  assert.equal(controllerConnectionProblem({...local,api_key:''},{...keySupport,configured:true,url:local.url,controller_kind:'unifi_os',auth_method:'password'}).field,'api_key');
});

test('local key submission omits account credentials and retains the normal certificate approval flow',async context=>{
  const dom=controllerDom(context),sent=[],local={url:'https://fixture-console',controller_kind:'unifi_os',auth_method:'api_key'};
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);if(args[1]!=='POST')return keySupport;
    if(!args[2].certificate_sha256)throw {code:'certificate_untrusted',details:{certificate_sha256:'b'.repeat(64)}};
    return {...keySupport,...local,configured:true,connected:true,capabilities:{client_mapping:false,port_metadata:true}};
  }});dom.after(cleanup);
  await flush();fill(dom,credentials);fill(dom,{...local,api_key:'fixture-local-key'});assert.equal(dom.field('api_key').type,'password');
  dom.submit();await cleanup.canLeave();assert.deepEqual(Object.keys(sent[1][2]).sort(),['api_key','auth_method','certificate_sha256','controller_kind','site','url']);
  assert.equal(dom.field('password').value,credentials.password,'inactive account draft is retained until success');
  dom.button('Trust this certificate and connect').click();await cleanup.canLeave();assert.equal(sent[2][2].certificate_sha256,'b'.repeat(64));
  assert.equal(sent[2][2].api_key,'fixture-local-key');assert.equal(dom.field('api_key').value,'');assert.equal(dom.field('password').value,'');
  assert.ok(dom.findAll(dom.host,item=>item.textContent==='Connect with a controller account to show device names.'&&!item.hidden).length);
});

test('changing authentication invalidates a prior certificate challenge while preserving separate credential drafts',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);if(args[1]==='POST')throw {code:'certificate_untrusted',details:{certificate_sha256:'c'.repeat(64)}};return keySupport;}});dom.after(cleanup);
  await flush();fill(dom,{...credentials,controller_kind:'unifi_os'});dom.submit();await cleanup.canLeave();const trust=dom.button('Trust this certificate and connect');
  fill(dom,{auth_method:'api_key',api_key:'fixture-key'});trust.click();assert.equal(sent.length,2);assert.equal(trust.parentNode.hidden,true);
  assert.equal(dom.field('password').value,credentials.password);fill(dom,{auth_method:'password'});assert.equal(dom.field('api_key').value,'fixture-key');
  dom.submit();await cleanup.canLeave();assert.equal(sent[2][2].password,credentials.password);assert.equal('api_key' in sent[2][2],false);
});

test('console discovery is explicit, blocks duplicate requests, and does not connect or save the key',async context=>{
  const dom=controllerDom(context),sent=[];let finish;
  const cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return args[0].endsWith('/consoles')?new Promise(resolve=>{finish=resolve;}):keySupport;}});dom.after(cleanup);
  await flush();fill(dom,credentials);fill(dom,{controller_kind:'cloud_connector',api_key:'fixture-cloud-key'});
  assert.equal(dom.field('url').value,'https://api.ui.com');assert.equal(dom.field('url').readOnly,true);assert.equal(dom.field('auth_method').value,'api_key');assert.equal(sent.length,1);
  const find=dom.button('Find consoles');find.click();find.click();assert.equal(sent.length,2);assert.deepEqual(sent[1],['/api/controller/consoles','POST',{api_key:'fixture-cloud-key'}]);
  finish({consoles:[{id:'console-one',name:'First console',type:null,blocked:false},{id:'console-blocked',name:'Blocked console',type:null,blocked:true}]});await cleanup.canLeave();
  assert.equal(dom.field('console_id').value,'');assert.equal(dom.field('console_id').children[2].disabled,true);assert.equal(dom.doc.activeElement,dom.field('console_id'));
  dom.field('console_id').value='console-blocked';dom.submit();assert.equal(sent.length,2);
  fill(dom,{console_id:'console-one'});dom.submit();await cleanup.canLeave();assert.equal(sent[2][0],'/api/controller/connect');
  assert.deepEqual(Object.keys(sent[2][2]).sort(),['api_key','auth_method','console_id','controller_kind','site','url']);
  assert.equal(sent[2][2].api_key,'fixture-cloud-key');assert.equal(sent[2][2].url,'https://api.ui.com');
  fill(dom,{controller_kind:'self_hosted'});assert.equal(dom.field('url').value,credentials.url);assert.equal(dom.field('password').value,credentials.password);
});

test('cloud certificate failures cannot offer or submit a manual trust override',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);if(args[0].endsWith('/connect'))throw {code:'certificate_untrusted',details:{certificate_sha256:'d'.repeat(64)}};return keySupport;}});dom.after(cleanup);
  await flush();fill(dom,{controller_kind:'cloud_connector',api_key:'fixture-cloud-key',console_id:'fixture-console'});dom.submit();await cleanup.canLeave();
  const trust=dom.button('Trust this certificate and connect');assert.equal(trust.parentNode.hidden,true);trust.click();assert.equal(sent.length,2);
  assert.equal('certificate_sha256' in sent[1][2],false);assert.match(dom.byClass(dom.host,'connection-notice')[0].textContent,/cloud certificate could not be verified/);
});

test('reloaded cloud candidate reuses only its selected console and auth method',async context=>{
  const cloud={url:'https://api.ui.com',controller_kind:'cloud_connector',auth_method:'api_key',console_id:'saved-console',sites:[{id:'first-site',name:'First site'}]};
  const current={...workingController,...keySupport,configured:true,connected:true,url:workingController.url,controller_kind:'self_hosted',auth_method:'password',pending_site_choice:cloud};
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);return current;}});dom.after(cleanup);
  await flush();assert.equal(dom.field('console_id').value,'saved-console');assert.equal(dom.field('auth_method').value,'api_key');assert.equal(dom.field('api_key').value,'');
  fill(dom,{site:'first-site'});dom.submit();await cleanup.canLeave();assert.equal(sent[1][2].api_key,'');assert.equal(sent[1][2].console_id,'saved-console');
  fill(dom,{console_id:'different-console'});dom.submit();assert.equal(sent.length,2);assert.equal(dom.doc.activeElement,dom.field('api_key'));
});

test('discovery failures and later key edits preserve the draft without using stale console choices',async context=>{
  const dom=controllerDom(context);let fail=false;
  const cleanup=renderControllerConnection(dom.host,{request:async path=>{if(!path.endsWith('/consoles'))return keySupport;if(fail)throw {code:'authentication_failed',message:'private key detail'};return {consoles:[{id:'console-one',name:'First console',blocked:false}]};}});dom.after(cleanup);
  await flush();fill(dom,{controller_kind:'cloud_connector',api_key:'fixture-key'});dom.button('Find consoles').click();await cleanup.canLeave();fill(dom,{console_id:'console-one'});
  fail=true;dom.button('Refresh consoles').click();await cleanup.canLeave();assert.equal(dom.field('api_key').value,'fixture-key');assert.equal(dom.field('console_id').value,'console-one');
  assert.doesNotMatch(dom.byClass(dom.host,'connection-notice')[0].textContent,/private key detail/);
  fill(dom,{api_key:'replacement-key'});assert.equal(dom.field('console_id').value,'');assert.equal(dom.field('console_id').children.length,1);assert.equal(dom.field('api_key').value,'replacement-key');
});

test('late console discovery cannot update a closed form and cleanup clears its API key',async context=>{
  const dom=controllerDom(context);let finish;
  const cleanup=renderControllerConnection(dom.host,{request:async path=>path.endsWith('/consoles')?new Promise(resolve=>{finish=resolve;}):keySupport});dom.after(cleanup);
  await flush();fill(dom,{controller_kind:'cloud_connector',api_key:'fixture-key'});dom.button('Find consoles').click();
  cleanup();assert.equal(dom.field('api_key').value,'');finish({consoles:[{id:'late-console',name:'Late console',blocked:false}]});await cleanup.canLeave();
  assert.equal(dom.field('console_id').children.length,1);assert.equal(dom.field('console_id').value,'');
});

test('key and cloud errors offer fixed recovery text without returning request details',()=>{
  for(const code of ['api_key_required','invalid_api_key','invalid_auth_method','invalid_console_id','invalid_cloud_origin','switch_identity_required','api_response_incomplete','integration_unavailable','no_consoles']){
    const text=controllerError({code,message:'private key account request'});assert.doesNotMatch(text,/private key account request/);
    assert.notEqual(text,'The controller connection could not be confirmed. Check the address and login, then try again.');
  }
});

test('UniFi OS account mode hides and omits the unsupported verification code',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);if(args[1]==='POST')throw {code:'controller_local_account_required'};return keySupport;}});dom.after(cleanup);
  await flush();fill(dom,{...credentials,controller_kind:'unifi_os'});const verification=dom.field('verification_code'),twoFactor=verification.parentNode.parentNode;
  assert.equal(twoFactor.hidden,true);assert.equal(verification.disabled,true);verification.value='123456';
  dom.submit();await cleanup.canLeave();assert.equal('verification_code' in sent[1][2],false);assert.equal(sent[1][2].auth_method,'password');
  assert.equal(twoFactor.hidden,true);assert.match(dom.byClass(dom.host,'connection-notice')[0].textContent,/local UniFi account/);
});

test('legacy self-hosted account mode retains its existing two-factor challenge flow',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{
    sent.push(args);if(args[1]!=='POST')return keySupport;
    if(!args[2].verification_code)throw {code:'two_factor_required'};
    return {...keySupport,configured:true,connected:true,url:credentials.url,controller_kind:'self_hosted',auth_method:'password'};
  }});dom.after(cleanup);
  await flush();fill(dom);const verification=dom.field('verification_code'),twoFactor=verification.parentNode.parentNode;
  assert.equal(twoFactor.hidden,false);assert.equal(verification.disabled,false);dom.submit();await cleanup.canLeave();
  assert.equal(twoFactor.open,true);assert.equal(verification.required,true);assert.equal(dom.doc.activeElement,verification);
  fill(dom,{verification_code:'123456'});dom.submit();await cleanup.canLeave();assert.equal(sent[2][2].verification_code,'123456');
  assert.equal(verification.value,'');assert.equal(verification.required,false);assert.equal(twoFactor.open,false);
});

test('changing a challenged legacy account to UniFi OS removes its hidden required-code constraint',async context=>{
  const dom=controllerDom(context),sent=[],cleanup=renderControllerConnection(dom.host,{request:async(...args)=>{sent.push(args);if(args[1]==='POST')throw {code:'two_factor_required'};return keySupport;}});dom.after(cleanup);
  await flush();fill(dom);dom.submit();await cleanup.canLeave();const verification=dom.field('verification_code');
  assert.equal(verification.required,true);fill(dom,{controller_kind:'unifi_os'});assert.equal(verification.required,false);assert.equal(verification.disabled,true);
  dom.submit();await cleanup.canLeave();assert.equal('verification_code' in sent[2][2],false);assert.match(dom.byClass(dom.host,'connection-notice')[0].textContent,/local UniFi account/);
});
