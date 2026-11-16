import test from 'node:test';
import assert from 'node:assert/strict';
import {renderPrivateAccess,renderSwitchConnection,privateAccessUrl,privateAccessSetupUrl,privateAccessError} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
const disabled={available:true,enabled:false,ready:false,url:null,error:null,can_configure:true};
const connected={...disabled,enabled:true,ready:true,url:'https://fixture.example.ts.net:8444'};
function controls(dom){return {toggle:dom.findAll(dom.host,item=>item.name==='private_access')[0],
  button:label=>dom.findAll(dom.host,item=>item.tagName==='button'&&item.textContent===label)[0],
  link:dom.findAll(dom.host,item=>item.tagName==='a'&&item.textContent==='Open private workspace')[0],
  status:dom.byClass(dom.host,'connection-state').at(-1),notice:dom.byClass(dom.host,'connection-notice').at(-1)};}
function change(toggle,value){toggle.checked=value;toggle.listeners.get('change')();}

test('private access stays off until an explicit Apply, with duplicate requests blocked',async context=>{
  const dom=rendererDom(context),sent=[];let finish;
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return args[1]==='PUT'?new Promise(resolve=>{finish=resolve;}):disabled;});dom.after(cleanup);
  await flush();const ui=controls(dom);assert.equal(ui.toggle.checked,false);assert.equal(ui.button('Apply setting').disabled,true);
  change(ui.toggle,true);assert.equal(sent.length,1);assert.equal(cleanup.hasChanges(),true);
  const apply=ui.button('Apply setting');apply.click();apply.click();assert.equal(sent.length,2);assert.equal(ui.toggle.disabled,true);
  assert.deepEqual(sent[1],['/api/private-access','PUT',{enabled:true}]);
  let left=false;const leaving=cleanup.canLeave().then(()=>{left=true;});await flush();assert.equal(left,false);
  finish(connected);await leaving;
  assert.equal(cleanup.hasChanges(),false);assert.equal(ui.status.textContent,'Private access connected');
  assert.ok(ui.status.className.includes('online'));assert.equal(ui.link.href,connected.url);
  assert.equal(ui.link.target,'_blank');assert.equal(ui.link.rel,'noopener noreferrer');assert.equal(dom.doc.activeElement,ui.toggle);
});

test('read-only phone status prevents configuration even with a programmatic click',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...connected,can_configure:false};});dom.after(cleanup);
  await flush();const ui=controls(dom);assert.equal(ui.toggle.disabled,true);assert.equal(ui.button('Apply setting').disabled,true);
  change(ui.toggle,false);ui.button('Apply setting').click();await flush();assert.equal(sent.length,1);
  ui.button('Refresh private access').click();await flush();assert.deepEqual(sent,[['/api/private-access'],['/api/private-access']]);
  assert.equal(ui.status.textContent,'Private access connected');
});

const LOCKED='To change private access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started.';
const HOST_ONLY='Change this setting in EE on the host computer.';
function readOnlyHelp(dom){return dom.findAll(dom.host,item=>item.tagName==='p'&&[LOCKED,HOST_ONLY].includes(item.textContent))[0];}
const locked={...connected,can_configure:false,can_close_workspace:false,can_change_port:false,setup_action:null,action_url:null,admin_required:true};

test('a local tab without administration access explains where to change private access',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return locked;});dom.after(cleanup);
  await flush();const ui=controls(dom),help=readOnlyHelp(dom);
  assert.equal(help.textContent,LOCKED);assert.equal(help.hidden,false);
  assert.equal(ui.toggle.disabled,true);assert.equal(ui.button('Apply setting').disabled,true);
  assert.equal(dom.findAll(dom.host,item=>item.name==='private_audience')[0].parentNode.hidden,true);
  assert.equal(dom.byClass(dom.host,'connection-options')[0].hidden,true);assert.equal(dom.byClass(dom.host,'connection-setup')[0].hidden,true);
  change(ui.toggle,false);ui.button('Apply setting').click();await flush();
  ui.button('Refresh private access').click();await flush();
  assert.deepEqual(sent,[['/api/private-access'],['/api/private-access']]);
  assert.equal(ui.status.textContent,'Private access connected');
});

test('the administration text also covers a host where Tailscale is missing, and not the demo or a remote device',async context=>{
  for(const [status,text] of [
    [{available:false,enabled:false,ready:false,can_configure:false,admin_required:true,error:{code:'tailscale_not_installed'}},LOCKED],
    [{available:false,enabled:false,ready:false,can_configure:false,can_close_workspace:false,admin_required:true},HOST_ONLY],
    [{...connected,can_configure:false},HOST_ONLY]]){
    const dom=rendererDom(context),cleanup=renderPrivateAccess(dom.host,async()=>status);dom.after(cleanup);
    await flush();assert.equal(readOnlyHelp(dom).textContent,text);assert.equal(readOnlyHelp(dom).hidden,false);
  }
  assert.equal(privateAccessError('admin_required'),LOCKED);
});

test('a change refused for administration access reads the status once and keeps the draft',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{
    sent.push(args);
    if(args[1]==='PUT')throw Object.assign(new Error(LOCKED),{code:'admin_required'});
    return sent.length===1?disabled:{...disabled,can_configure:false,can_close_workspace:false,admin_required:true};
  });dom.after(cleanup);
  await flush();const ui=controls(dom);change(ui.toggle,true);ui.button('Apply setting').click();await flush();
  assert.deepEqual(sent,[['/api/private-access'],['/api/private-access','PUT',{enabled:true}],['/api/private-access']]);
  assert.equal(ui.toggle.disabled,true);assert.equal(ui.button('Apply setting').disabled,true);
  assert.equal(readOnlyHelp(dom).textContent,LOCKED);assert.equal(readOnlyHelp(dom).hidden,false);
  assert.equal(ui.notice.textContent,LOCKED);assert.equal(cleanup.hasChanges(),true);assert.equal(ui.toggle.checked,true);
});

test('local refresh checks external Tailscale state and retains an unapplied choice',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return disabled;});dom.after(cleanup);
  await flush();const ui=controls(dom);change(ui.toggle,true);ui.button('Refresh private access').click();await flush();
  assert.deepEqual(sent,[['/api/private-access'],['/api/private-access/refresh','POST']]);
  assert.equal(ui.toggle.checked,true);assert.equal(cleanup.hasChanges(),true);assert.equal(ui.button('Apply setting').disabled,false);
});

test('failed settings retain the draft and show only a known recovery message',async context=>{
  const dom=rendererDom(context);
  const cleanup=renderPrivateAccess(dom.host,async(_path,method)=>method==='PUT'
    ?{...disabled,error:{code:'private_access_save_failed',message:'sensitive CLI data'}}:disabled);dom.after(cleanup);
  await flush();const ui=controls(dom);change(ui.toggle,true);ui.button('Apply setting').click();await flush();
  assert.equal(cleanup.hasChanges(),true);assert.equal(ui.toggle.checked,true);assert.match(ui.notice.textContent,/could not be saved/);
  assert.doesNotMatch(ui.notice.textContent,/sensitive CLI data/);assert.equal(ui.button('Apply setting').disabled,false);
});

test('a confirmed enabled preference does not imply a connected private endpoint',async context=>{
  const dom=rendererDom(context);
  const cleanup=renderPrivateAccess(dom.host,async()=>({...disabled,enabled:true,error:{code:'private_access_setup_unconfirmed'}}));dom.after(cleanup);
  await flush();const ui=controls(dom);assert.equal(ui.link.parentNode.hidden,true);assert.equal(ui.status.className.includes('online'),false);
  assert.match(ui.status.textContent,/not confirmed/);change(ui.toggle,false);assert.equal(ui.button('Apply setting').disabled,false);
});

test('saved enabled access can be retried explicitly without toggling or automatic writes',async context=>{
  const dom=rendererDom(context),sent=[];let finish;
  const incomplete={...disabled,enabled:true,error:{code:'private_access_route_unavailable'}};
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{
    sent.push(args);return args[1]==='PUT'?new Promise(resolve=>{finish=resolve;}):incomplete;
  });dom.after(cleanup);
  await flush();const ui=controls(dom),retry=ui.button('Retry private access');
  assert.deepEqual(sent,[['/api/private-access']]);assert.equal(retry.disabled,false);assert.equal(cleanup.hasChanges(),false);
  retry.click();retry.click();assert.deepEqual(sent[1],['/api/private-access','PUT',{enabled:true}]);assert.equal(sent.length,2);
  finish(connected);await cleanup.canLeave();assert.equal(ui.status.textContent,'Private access connected');
  assert.equal(ui.button('Apply setting').disabled,true);
});

test('an unsuccessful retry retains its recovery message and can be retried again',async context=>{
  const dom=rendererDom(context),sent=[],incomplete={...disabled,enabled:true,error:{code:'private_access_setup_unconfirmed',message:'private details'}};
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return incomplete;});dom.after(cleanup);
  await flush();const ui=controls(dom);ui.button('Retry private access').click();await flush();
  assert.equal(sent.filter(item=>item[1]==='PUT').length,1);assert.equal(ui.button('Retry private access').disabled,false);
  assert.match(ui.notice.textContent,/not confirmed/);assert.doesNotMatch(ui.notice.textContent,/private details/);
  assert.match(privateAccessError({code:'private_access_port_in_use'}),/Keep that service unchanged/);
  assert.doesNotMatch(privateAccessError({code:'private_access_port_in_use'}),/close|stop|disable/i);
});

test('cleanup and abort ignore late status without changing a replacement component',async context=>{
  const dom=rendererDom(context),signal=new AbortController();let finish;
  const cleanup=renderPrivateAccess(dom.host,()=>new Promise(resolve=>{finish=resolve;}),{signal:signal.signal});
  const original=controls(dom).status.textContent;signal.abort();finish(connected);await flush();
  assert.equal(controls(dom).status.textContent,original);cleanup();
  let called=false;renderPrivateAccess(dom.host,async()=>{called=true;},{signal:signal.signal});assert.equal(called,false);
});

test('malformed status becomes an actionable failure instead of an indefinite spinner',async context=>{
  const dom=rendererDom(context),cleanup=renderPrivateAccess(dom.host,async()=>({enabled:true}));dom.after(cleanup);
  await flush();const ui=controls(dom);assert.equal(ui.status.textContent,'Private access unavailable');
  assert.match(ui.notice.textContent,/Refresh status/);assert.equal(ui.button('Refresh private access').disabled,false);
});

test('private addresses are restricted to the approved HTTPS Tailscale origin shape',()=>{
  assert.equal(privateAccessUrl(connected.url),connected.url);assert.equal(privateAccessUrl(connected.url+'/'),connected.url);
  for(const port of [1024,8445,65535])assert.equal(privateAccessUrl(`https://fixture.example.ts.net:${port}`),`https://fixture.example.ts.net:${port}`);
  for(const value of [null,'javascript:alert(1)','https://example.com:8444','http://fixture.example.ts.net:8444',
    'https://fixture.example.ts.net','https://fixture.example.ts.net:1023','https://fixture.example.ts.net:65536','https://user:password@fixture.example.ts.net:8444',connected.url+'/?token=secret',connected.url+'/#secret',connected.url+'/api/session'])
    assert.equal(privateAccessUrl(value),null,String(value));
  assert.doesNotMatch(privateAccessError({code:'unknown',message:'private account'}),/private account/);
  assert.match(privateAccessError({code:'private_access_cleanup_unconfirmed'}),/Keep EE open/);
});

test('setup links accept only the approved Tailscale destinations',()=>{
  for(const [action,url] of [['install','https://tailscale.com/download'],['approve','https://login.tailscale.com/a/fixture-token'],['approve','https://login.tailscale.com/f/serve?node=fixture'],['approve','https://login.tailscale.com/admin/machines']])
    assert.equal(privateAccessSetupUrl(url,action),url);
  for(const url of ['https://login.tailscale.com.evil.test/a/token','https://login.tailscale.com:8444/a/token','https://login.tailscale.com/a/%74oken','https://login.tailscale.com/other/../a/token','https://login.tailscale.com/a/token#fragment','https://user:secret@login.tailscale.com/a/token','https://login.tailscale.com/a/token?redirect=elsewhere','https://login.tailscale.com/other','http://login.tailscale.com/a/token','https://login.tailscale.com/a/token\n'])
    assert.equal(privateAccessSetupUrl(url,'approve'),null,url);
  assert.equal(privateAccessSetupUrl('https://tailscale.com/download?other=1','install'),null);
  assert.equal(privateAccessSetupUrl('https://tailscale.com/download','approve'),null);
});

test('fresh setup presents an official install link without starting or downloading anything',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...disabled,available:false,setup_action:'install',action_url:'https://tailscale.com/download',error:{code:'tailscale_not_installed'}};});dom.after(cleanup);
  await flush();const ui=controls(dom),install=dom.findAll(dom.host,item=>item.tagName==='a'&&item.textContent==='Install Tailscale')[0];
  assert.equal(install.hidden,false);assert.equal(install.href,'https://tailscale.com/download');assert.equal(install.rel,'noopener noreferrer');
  assert.equal(ui.toggle.disabled,true);assert.deepEqual(sent,[['/api/private-access']]);assert.equal(cleanup.hasChanges(),false);
});

test('sign-in requires one local explicit action and approval stays a user-opened link',async context=>{
  const dom=rendererDom(context),sent=[];let finish;
  const signedOut={...disabled,available:false,setup_action:'signin',action_url:null,error:{code:'tailscale_signin_required'}};
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return args[0].endsWith('/login')?new Promise(resolve=>{finish=resolve;}):signedOut;});dom.after(cleanup);
  await flush();const signIn=controls(dom).button('Sign in to Tailscale');assert.equal(signIn.hidden,false);assert.equal(signIn.disabled,false);
  signIn.click();signIn.click();assert.deepEqual(sent,[['/api/private-access'],['/api/private-access/login','POST']]);
  assert.equal(signIn.disabled,true);finish({...signedOut,setup_action:'approve',action_url:'https://login.tailscale.com/a/fixture-token'});await cleanup.canLeave();
  const approval=dom.findAll(dom.host,item=>item.tagName==='a'&&item.textContent==='Continue in Tailscale')[0];
  assert.equal(approval.hidden,false);assert.equal(approval.href,'https://login.tailscale.com/a/fixture-token');assert.equal(signIn.hidden,true);
  assert.equal(controls(dom).status.textContent,'Waiting for Tailscale approval');
  assert.equal(controls(dom).notice.className.includes('error'),false);
  assert.equal(dom.doc.activeElement,approval);assert.equal(sent.length,2);
});

test('setup errors distinguish missing installation, permission, approval, and port cleanup',()=>{
  const texts=['tailscale_not_installed','tailscale_unavailable','tailscale_permission_required','tailscale_approval_required','private_access_port_change_requires_off'].map(code=>privateAccessError({code,message:'private diagnostic'}));
  assert.match(texts[0],/Install Tailscale/);assert.doesNotMatch(texts[1],/install/i);
  assert.match(texts[2],/administrator/);assert.match(texts[3],/approval/);assert.match(texts[4],/confirm cleanup/);
  assert.match(privateAccessError({code:'tailscale_not_running'}),/Connect Tailscale/);
  assert.match(privateAccessError({code:'tailscale_device_identity_required'}),/personal Tailscale account/);
  for(const text of texts)assert.doesNotMatch(text,/private diagnostic/);
});

test('ready status must match its saved HTTPS port before displaying a workspace link',async context=>{
  const dom=rendererDom(context),cleanup=renderPrivateAccess(dom.host,async()=>({...connected,https_port:8445}));dom.after(cleanup);
  await flush();const ui=controls(dom);assert.equal(ui.link.parentNode.hidden,true);assert.equal(ui.status.className.includes('online'),false);
  cleanup.update({...connected,https_port:8445,url:'https://fixture.example.ts.net:8445'});
  assert.equal(ui.status.textContent,'Private access connected');assert.equal(ui.link.href,'https://fixture.example.ts.net:8445');
});

test('phone cannot initiate host sign-in or change a private port',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...disabled,available:false,can_configure:false,can_change_port:true,https_port:8444,setup_action:'signin'};});dom.after(cleanup);
  await flush();const ui=controls(dom),port=dom.findAll(dom.host,item=>item.name==='private_https_port')[0];
  assert.equal(ui.button('Sign in to Tailscale').disabled,true);ui.button('Sign in to Tailscale').click();
  assert.equal(port.disabled,true);port.value='8445';port.listeners.get('input')();ui.button('Apply setting').click();
  await flush();assert.equal(sent.length,1);assert.equal(cleanup.hasChanges(),false);
});

test('port drafts survive refresh and only Apply saves the port while access is off',async context=>{
  const dom=rendererDom(context),sent=[];const initial={...disabled,https_port:8444,can_change_port:true};
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return args[1]==='PUT'?{...initial,https_port:8445}:initial;});dom.after(cleanup);
  await flush();const ui=controls(dom),port=dom.findAll(dom.host,item=>item.name==='private_https_port')[0];
  assert.equal(port.value,'8444');assert.equal(port.disabled,false);port.value='8445';port.listeners.get('input')();
  assert.equal(cleanup.hasChanges(),true);ui.button('Refresh private access').click();await flush();assert.equal(port.value,'8445');
  ui.button('Apply setting').click();await cleanup.canLeave();assert.deepEqual(sent.at(-1),['/api/private-access','PUT',{enabled:false,https_port:8445}]);
  assert.equal(cleanup.hasChanges(),false);assert.equal(port.value,'8445');
});

test('invalid or cleanup-blocked ports cannot submit even with a programmatic click',async context=>{
  const dom=rendererDom(context),sent=[];const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...disabled,https_port:8444,can_change_port:true};});dom.after(cleanup);
  await flush();const ui=controls(dom),port=dom.findAll(dom.host,item=>item.name==='private_https_port')[0];
  for(const value of ['', '1023', '65536', '8444.5']){port.value=value;port.listeners.get('input')();assert.equal(ui.button('Apply setting').disabled,true);ui.button('Apply setting').click();}
  assert.equal(sent.length,1);port.value='8445';port.listeners.get('input')();
  cleanup.update({...disabled,https_port:8444,can_change_port:false,error:{code:'private_access_cleanup_unconfirmed'}});
  assert.equal(port.disabled,true);assert.equal(ui.button('Apply setting').disabled,true);ui.button('Apply setting').click();assert.equal(sent.length,1);
  const reset=ui.button('Reset port');assert.equal(reset.hidden,false);reset.click();assert.equal(cleanup.hasChanges(),false);assert.equal(port.value,'8444');
});

test('a port draft can be reset after another client enables access so turning it off stays reachable',async context=>{
  const dom=rendererDom(context),sent=[],cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...disabled,https_port:8444,can_change_port:true};});dom.after(cleanup);
  await flush();const ui=controls(dom),port=dom.findAll(dom.host,item=>item.name==='private_https_port')[0];
  port.value='8445';port.listeners.get('input')();cleanup.update({...connected,https_port:8444,can_change_port:false});
  assert.equal(port.disabled,true);change(ui.toggle,false);assert.equal(ui.button('Apply setting').disabled,true);
  ui.button('Reset port').click();assert.equal(ui.button('Apply setting').disabled,false);ui.button('Apply setting').click();await cleanup.canLeave();
  assert.deepEqual(sent.at(-1),['/api/private-access','PUT',{enabled:false}]);
});

test('clipboard failure offers a browser fallback without sending a request',async context=>{
  const dom=rendererDom(context),sent=[],original=Object.getOwnPropertyDescriptor(globalThis,'navigator');
  Object.defineProperty(globalThis,'navigator',{configurable:true,value:{clipboard:{writeText:async()=>{throw new Error('Denied');}}}});
  context.after(()=>{if(original)Object.defineProperty(globalThis,'navigator',original);else delete globalThis.navigator;});
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return connected;});dom.after(cleanup);
  await flush();const ui=controls(dom);ui.button('Copy private address').click();await flush();
  assert.match(ui.notice.textContent,/copy its address from your browser/);assert.equal(dom.doc.activeElement,ui.link);assert.equal(sent.length,1);
});

test('the login section keeps SSH form values and uses separate explicit private-access actions',async context=>{
  const dom=rendererDom(context),create=dom.doc.createElement,sent=[];
  dom.doc.createElement=tag=>{
    const element=create(tag);element.toggleAttribute=()=>{};element.querySelector=()=>null;
    if(tag==='form')Object.defineProperty(element,'elements',{get:()=>dom.findAll(element,item=>['input','button','select'].includes(item.tagName))});
    return element;
  };
  const cleanup=renderSwitchConnection(dom.host,{request:async(...args)=>{sent.push(args);return args[1]==='PUT'?connected:disabled;},onConnected:()=>{throw new Error('No SSH login');}});dom.after(cleanup);
  const fields=Object.fromEntries(dom.findAll(dom.host,item=>item.tagName==='input').map(item=>[item.name,item]));
  fields.host.value='fixture-switch';fields.username.value='fixture-user';fields.password.value='fixture-secret';
  await flush();const ui=controls(dom);change(ui.toggle,true);ui.button('Apply setting').click();await flush();
  assert.equal(fields.host.value,'fixture-switch');assert.equal(fields.username.value,'fixture-user');assert.equal(fields.password.value,'fixture-secret');
  assert.deepEqual(sent.map(item=>item[0]),['/api/private-access','/api/private-access']);
  const form=dom.findAll(dom.host,item=>item.tagName==='form')[0];assert.equal(form.contains(ui.toggle),false);
});

const REMOTE_SAVED='Reconnect to the switch saved on the computer running EE.';
const REMOTE_NONE='No switch login is saved on the computer running EE. Sign in to the switch on that computer. This page opens the workspace when the switch connects.';
function remoteConnection(context,session,restoreSaved=async()=>({logged_in:true,workspace_id:'remote'})){
  const dom=rendererDom(context),create=dom.doc.createElement,sent=[],restores=[],entered=[];
  dom.doc.createElement=tag=>{
    const element=create(tag);element.toggleAttribute=()=>{};element.querySelector=()=>null;
    if(tag==='form')Object.defineProperty(element,'elements',{get:()=>dom.findAll(element,item=>['input','button','select'].includes(item.tagName))});
    return element;
  };
  const cleanup=renderSwitchConnection(dom.host,{request:async(...args)=>{sent.push(args);return disabled;},session,remote:true,
    restoreSaved:async()=>{restores.push(true);return restoreSaved();},onConnected:async value=>entered.push(value)});dom.after(cleanup);
  return {...dom,sent,restores,entered,
    button:label=>dom.findAll(dom.host,item=>item.tagName==='button'&&item.textContent===label)[0],
    intro:()=>dom.findAll(dom.host,item=>item.tagName==='p'&&item.className==='help-text')[0].textContent,
    notice:()=>dom.byClass(dom.host,'connection-notice')[0].textContent};
}

test('a remote device sees only Connect with saved login when the host has a saved login',async context=>{
  const ui=remoteConnection(context,{has_saved_login:true});await flush();
  for(const name of ['host','username','password','port','remember'])assert.equal(ui.findAll(ui.host,item=>item.name===name).length,0,name);
  for(const label of ['Connect to switch','Forget saved login','Continue with demo'])assert.equal(ui.button(label),undefined,label);
  assert.equal(ui.intro(),REMOTE_SAVED);
  const restore=ui.button('Connect with saved login');assert.equal(restore.hidden,false);
  restore.click();await flush();assert.equal(ui.restores.length,1);assert.equal(ui.entered.length,1);
});

test('a remote device without a saved login on the host is sent to that computer',async context=>{
  const ui=remoteConnection(context,{has_saved_login:false});await flush();
  assert.equal(ui.button('Connect with saved login').hidden,true);assert.equal(ui.intro(),REMOTE_NONE);
  assert.deepEqual(ui.sent,[['/api/private-access']]);
});

test('a failed remote reconnect names the computer running EE',async context=>{
  for(const [code,text] of [['no_saved_login',REMOTE_NONE],['auth_failed','The switch rejected the saved login. Sign in to the switch on the computer running EE.']]){
    const ui=remoteConnection(context,{has_saved_login:true},async()=>{throw Object.assign(new Error('private detail'),{code});});await flush();
    ui.button('Connect with saved login').click();await flush();
    assert.equal(ui.notice(),text);assert.equal(ui.doc.activeElement,ui.button('Connect with saved login'));
  }
});

const FP_A='SHA256:'+'A'.repeat(43),FP_B='SHA256:'+'B'.repeat(42)+'A';
const CHANGED_KEY={code:'host_key_changed',message:'The switch’s SSH key changed. Check the new fingerprint before signing in.',
  host:'192.168.1.2',port:22,ssh_host_key:FP_B,ssh_host_key_type:'ssh-rsa',saved_ssh_host_key:FP_A};
const REMOTE_KEY='The switch identity has changed. Review its SSH host key on the computer running EE before reconnecting.';

test('a remote device shows the changed key message instead of a key review it cannot complete',async context=>{
  const session={logged_in:false,has_saved_login:true,remembered:{host:'192.168.1.2',port:22},error:CHANGED_KEY};
  const refuse=async()=>{throw Object.assign(new Error(CHANGED_KEY.message),{code:CHANGED_KEY.code,details:CHANGED_KEY});};
  // The key error arrives in the session, or as the refused reconnect.
  for(const [value,restoreSaved] of [[session,undefined],[{...session,error:null},refuse]]){
    const ui=remoteConnection(context,value,restoreSaved);await flush();
    if(restoreSaved){assert.notEqual(ui.notice(),REMOTE_KEY);ui.button('Connect with saved login').click();await flush();}
    assert.equal(ui.notice(),REMOTE_KEY);
    assert.equal(ui.byClass(ui.host,'host-key-review').length,0);assert.equal(ui.button('Accept new key'),undefined);
    // A review would read private access again to decide whether the key can be accepted here.
    assert.deepEqual(ui.sent,[['/api/private-access']]);
  }
});

test('a saved login the host cannot open is reported to a remote device',async context=>{
  const ui=remoteConnection(context,{has_saved_login:true,saved_login_error:'x'});await flush();
  assert.equal(ui.notice(),'The saved login could not be opened. Sign in to the switch on the computer running EE.');
});

test('private access says what devices on Tailscale can use',async context=>{
  const dom=rendererDom(context),cleanup=renderPrivateAccess(dom.host,async()=>disabled);dom.after(cleanup);await flush();
  const help=dom.byClass(dom.host,'help-text').map(item=>item.textContent);
  assert.equal(help[1],'Devices on Tailscale can use lighting, effects, automation, profiles, schedules and port names, can control fans in the desktop app, and can reconnect to the saved switch. Switch login, UniFi Network setup and these settings are available only on the computer running EE.');
  assert.equal(help[0],'Use Tailscale to open this workspace on your phone or another device.');
});

test('the audience is chosen explicitly, sent once with Apply, and confirmed from the reply',async context=>{
  const dom=rendererDom(context),sent=[];let reply={...connected,audience:'accounts'};
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return args[1]==='PUT'?reply:connected;});dom.after(cleanup);
  await flush();const ui=controls(dom),audience=dom.findAll(dom.host,item=>item.name==='private_audience')[0];
  assert.equal(audience.value,'owner');assert.equal(audience.disabled,false);assert.equal(audience.parentNode.hidden,false);
  assert.deepEqual(audience.children.map(option=>option.value),['owner','accounts']);
  assert.equal(ui.button('Apply setting').disabled,true);
  audience.value='accounts';audience.listeners.get('change')();
  assert.equal(cleanup.hasChanges(),true);assert.equal(sent.length,1);
  const apply=ui.button('Apply setting');assert.equal(apply.disabled,false);apply.click();await flush();
  assert.deepEqual(sent[1],['/api/private-access','PUT',{enabled:true,audience:'accounts'}]);
  assert.equal(audience.value,'accounts');assert.equal(cleanup.hasChanges(),false);
  assert.equal(ui.notice.textContent,'Private access updated.');assert.equal(ui.status.textContent,'Private access connected');
  reply={...connected,audience:'accounts'};
  audience.value='owner';audience.listeners.get('change')();ui.button('Apply setting').click();await flush();
  assert.deepEqual(sent[2],['/api/private-access','PUT',{enabled:true,audience:'owner'}]);
  assert.equal(ui.notice.textContent,'The setting could not be confirmed. Refresh status before trying again.');
  assert.equal(cleanup.hasChanges(),true);
});

test('a read-only client cannot see or change who may open the workspace',async context=>{
  const dom=rendererDom(context),sent=[];
  const cleanup=renderPrivateAccess(dom.host,async(...args)=>{sent.push(args);return {...connected,audience:'accounts',can_configure:false};});dom.after(cleanup);
  await flush();const ui=controls(dom),audience=dom.findAll(dom.host,item=>item.name==='private_audience')[0];
  assert.equal(audience.parentNode.hidden,true);assert.equal(audience.disabled,true);
  audience.value='owner';audience.listeners.get('change')();ui.button('Apply setting').click();await flush();
  assert.equal(cleanup.hasChanges(),false);assert.deepEqual(sent,[['/api/private-access']]);
});
