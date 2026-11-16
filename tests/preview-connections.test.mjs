import test from 'node:test';
import assert from 'node:assert/strict';
import {connectionError, controllerError, controllerReconnectControls, controllerReconnectConfirmed, showSceneChoice, renderSwitchConnection,
  hostKeyProblem, hostKeyError, canAcceptHostKey, createHostKeyFlow} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

test('connection errors use known codes instead of backend exception text', () => {
  const error = Object.assign(new Error('ssh://private-user:secret@private-host'), {code:'auth_failed'});
  assert.equal(connectionError(error), 'The switch rejected the login. Check your SSH username and password.');
  assert.doesNotMatch(connectionError(error), /private|secret/);
});

test('unknown connection failures never disclose their raw message', () => {
  for (const error of [new Error('Authorization: Bearer secret'), {code:'unknown',message:'private-host'}, null,
    ...['toString','constructor','__proto__',{}].map(code=>({code}))]) {
    assert.equal(connectionError(error), 'The connection could not be confirmed. Check the details and try again.');
  }
});

test('an unusable saved scene shows the service message that names its recovery', () => {
  const message = 'The saved lighting for this switch was saved by a newer version of EE. Update EE to use it.';
  assert.equal(connectionError(Object.assign(new Error(message), {code:'invalid_saved_scene'})), message);
  assert.equal(connectionError({code:'invalid_saved_scene'}), 'The connection could not be confirmed. Check the details and try again.');
});

test('unqualified models offer a compatibility action without retrying credentials', () => {
  assert.match(connectionError({code:'compatibility_verification_required'}),/compatibility verification/);
  assert.doesNotMatch(connectionError({code:'compatibility_verification_required'}),/password|try again/i);
});

test('controller authentication and site errors offer distinct recovery actions', () => {
  assert.match(connectionError({code:'controller_auth_failed'}), /local controller account/);
  assert.equal(connectionError({code:'site_required'}), 'Choose the UniFi site that contains your switch.');
  assert.match(connectionError({code:'switch_not_found'}), /selected site/);
});

test('timeout handling remains actionable without exposing a request URL', () => {
  assert.equal(connectionError({name:'TimeoutError',message:'https://private-host/'}), 'The connection timed out. Check that the device is available and try again.');
  assert.equal(connectionError({code:'unknown'}, 'Retry controller connection.'), 'Retry controller connection.');
});

test('shared auth and reachability codes identify the relevant connection', () => {
  for (const code of ['authentication_failed','unreachable']) {
    assert.match(connectionError({code}), /switch/);
    assert.match(controllerError({code}), /UniFi Network/);
    assert.doesNotMatch(controllerError({code}), /SSH/);
  }
  assert.match(connectionError({code:'no_saved_login'}), /Save login/);
});

test('controller errors keep upstream response text and credentials out of the UI', () => {
  const error = {code:'invalid_response',message:'https://private-user:secret@private-host'};
  assert.equal(controllerError(error), 'The controller response could not be read. Check the UniFi Network address.');
  assert.doesNotMatch(controllerError({code:'unknown',message:'private-user secret'}), /private|secret/);
  for (const code of ['toString', 'constructor', '__proto__', {toString:()=>{throw new Error('Do not coerce upstream objects');}}]) {
    assert.equal(controllerError({code}, 'Retry controller connection.'), 'Retry controller connection.');
  }
});

test('controller errors identify a safe operation and status in live and saved responses', () => {
  const detail = {operation:'devices', upstream_status:503};
  for (const error of [{code:'controller_error', ...detail}, {code:'controller_error', details:detail}]) {
    assert.match(controllerError(error), /switch discovery \(HTTP 503\)/);
  }
  assert.doesNotMatch(controllerError({code:'controller_error',operation:'private',upstream_status:'secret'}), /private|secret/);
});

test('verification challenges ask for a code without blaming the password', () => {
  assert.equal(controllerError({code:'two_factor_required',message:'private-account'}), 'Enter the verification code for your UniFi account.');
  assert.equal(controllerError({code:'verification_failed',message:'code 123456'}), 'The verification code was not accepted. Enter a new code and try again.');
});

test('automatic reconnect is off by default and hidden on older controller APIs', () => {
  assert.deepEqual(controllerReconnectControls(), {supported:false, checked:false, changed:false, canApply:false});
  assert.equal(controllerReconnectControls({connected:true, can_save_login:true}, true).canApply, false);
  assert.deepEqual(controllerReconnectControls({preferences_supported:true, can_save_login:true}),
    {supported:true, checked:false, changed:false, canApply:false});
});

test('saving reconnect requires an eligible active session and an explicit unapplied choice', () => {
  const unavailable = {preferences_supported:true, auto_reconnect:false, can_save_login:false};
  assert.deepEqual(controllerReconnectControls(unavailable, true),
    {supported:true, checked:true, changed:true, canApply:false});
  const ready = {...unavailable, connected:true, can_save_login:true};
  assert.equal(controllerReconnectControls(ready, true).canApply, true);
  assert.equal(controllerReconnectControls(ready, false).changed, false);
  assert.equal(controllerReconnectControls(ready, false).canApply, false);
});

test('removing saved controller login remains available while disconnected or paused', () => {
  const current = {preferences_supported:true, connected:false, auto_reconnect:true,
    saved_login_available:true, can_save_login:false, reconnect_state:'paused'};
  assert.deepEqual(controllerReconnectControls(current, false),
    {supported:true, checked:false, changed:true, canApply:true});
  assert.equal(controllerReconnectControls(current).checked, true);
});

test('status refreshes retain the pending preference until persistence is confirmed', () => {
  const current = {preferences_supported:true, connected:true, auto_reconnect:false,
    saved_login_available:false, can_save_login:true};
  const draft = true;
  assert.equal(controllerReconnectControls({...current, stale:true}, draft).checked, true);
  const failed = {...current, saved_login_error:{code:'controller_login_save_failed'}};
  assert.equal(controllerReconnectConfirmed(failed, draft), false);
  assert.equal(controllerReconnectControls(failed, draft).canApply, true);
  const saved = {...current, auto_reconnect:true, saved_login_available:true, saved_login_matches_connection:true};
  assert.equal(controllerReconnectConfirmed(saved, draft), true);
  assert.equal(controllerReconnectControls(saved).changed, false);
});

test('a connected response alone never proves the login was securely saved or removed', () => {
  const current = {preferences_supported:true, connected:true};
  for (const result of [current,
    {...current, auto_reconnect:true, saved_login_available:false},
    {...current, auto_reconnect:true, saved_login_available:true, saved_login_matches_connection:false},
    {...current, auto_reconnect:true, saved_login_available:true, saved_login_error:{code:'controller_login_save_failed'}},
  ]) assert.equal(controllerReconnectConfirmed(result, true), false);
  assert.equal(controllerReconnectConfirmed({...current, auto_reconnect:false, saved_login_available:true}, false), false);
  assert.equal(controllerReconnectConfirmed({...current, auto_reconnect:false, saved_login_available:false}, false), true);
  assert.equal(controllerReconnectConfirmed(current, undefined), false);
});

test('a different active controller can replace the saved login only through explicit Apply', () => {
  const current = {preferences_supported:true, connected:true, auto_reconnect:true,
    saved_login_available:true, can_save_login:true, saved_login_matches_connection:false};
  assert.deepEqual(controllerReconnectControls(current),
    {supported:true, checked:true, changed:false, canApply:true});
  assert.equal(controllerReconnectConfirmed(current, true), false);
  const confirmed = {...current, saved_login_matches_connection:true};
  assert.equal(controllerReconnectConfirmed(confirmed, true), true);
  assert.equal(controllerReconnectControls(confirmed).canApply, false);
});

test('controller preference failures remain actionable without exposing storage or login details', () => {
  for (const code of ['controller_login_required','controller_login_save_failed','controller_login_remove_failed','saved_controller_login_unavailable']) {
    const text = controllerError({code,message:'private-user secret C:/private-location'});
    assert.match(text, /controller/i);
    assert.doesNotMatch(text, /private|secret|C:\//);
  }
});

function connectionDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement;
  dom.doc.createElement=tag=>{
    const element=create(tag);
    element.showModal=()=>{element.open=true;};element.close=()=>{element.open=false;};
    element.remove=()=>{const parent=element.parentNode;if(parent){parent.children.splice(parent.children.indexOf(element),1);element.parentNode=null;}};
    element.toggleAttribute=(name,present)=>{if(present)element.setAttribute(name,'');else element.attributes.delete(name);};
    element.removeAttribute=name=>{element.attributes.delete(name);};
    element.reportValidity=()=>true;
    element.querySelector=selector=>selector===':scope > button'?element.children.find(child=>child.tagName==='button'):null;
    if(tag==='form')Object.defineProperty(element,'elements',{get:()=>dom.findAll(element,node=>['input','button','select'].includes(node.tagName))});
    return element;
  };
  const lifecycle=new AbortController();dom.after(()=>lifecycle.abort());
  return {...dom,lifecycle,buttons:()=>dom.findAll(dom.doc.body,node=>node.tagName==='button'),
    button:text=>dom.findAll(dom.doc.body,node=>node.tagName==='button'&&node.textContent===text)[0],
    dialog:()=>dom.findAll(dom.doc.body,node=>node.tagName==='dialog')[0]};
}
const flush=()=>new Promise(resolve=>setImmediate(resolve));
const pendingScene=(changes={})=>({logged_in:false,workspace_id:null,pending_scene_choice:{connection_id:'candidate-a',legacy_revision:'revision-a',legacy_available:true},...changes});
const completeScene=()=>({logged_in:true,workspace_id:'workspace-new',pending_scene_choice:null});
const switchRequest=send=>(path,...args)=>path==='/api/private-access'
  ?Promise.resolve({available:false,enabled:false,ready:false,can_configure:true,url:null,error:{code:'tailscale_unavailable'}})
  :send(path,...args);

test('scene choice starts no output until an explicit selection and deduplicates live updates',async context=>{
  const dom=connectionDom(context),sent=[];let complete;
  const request=async(...args)=>{sent.push(args);return new Promise(resolve=>{complete=resolve;});};
  const session=pendingScene(),result=showSceneChoice(request,session,{signal:dom.lifecycle.signal});
  assert.equal(showSceneChoice(request,session),result);
  assert.equal(dom.findAll(dom.doc.body,node=>node.tagName==='dialog').length,1);
  assert.equal(dom.doc.activeElement,dom.button('Cancel'));assert.equal(sent.length,0);
  dom.button('Use saved lighting').click();
  assert.deepEqual(sent,[['/api/session/scene-choice','POST',{connection_id:'candidate-a',choice:'use_saved',legacy_revision:'revision-a'}]]);
  assert.ok(dom.buttons().every(button=>button.disabled));
  dom.button('Start new setup').click();assert.equal(sent.length,1);
  const connected=completeScene();complete(connected);assert.equal(await result,connected);assert.equal(dom.dialog(),undefined);
});

test('manual SSH submission rejects empty credentials and invalid ports without contacting the server',async context=>{
  const dom=connectionDom(context),sent=[];
  const cleanup=renderSwitchConnection(dom.host,{request:switchRequest(async(...args)=>{sent.push(args);return completeScene();}),onConnected:async()=>{}});
  const form=dom.findAll(dom.host,node=>node.tagName==='form')[0],field=name=>dom.findAll(form,node=>node.name===name)[0];
  field('host').value='switch.example';field('username').value='local-user';field('password').value='';
  assert.equal(field('password').required,true);
  form.listeners.get('submit')({preventDefault(){}});assert.equal(sent.length,0);assert.equal(dom.doc.activeElement,field('password'));
  field('password').value='fixture-password';field('host').value='   ';
  form.listeners.get('submit')({preventDefault(){}});assert.equal(sent.length,0);assert.equal(dom.doc.activeElement,field('host'));
  field('host').value='switch.example';field('username').value='   ';
  form.listeners.get('submit')({preventDefault(){}});assert.equal(sent.length,0);assert.equal(dom.doc.activeElement,field('username'));
  field('username').value='local-user';field('port').value='1.5';
  form.listeners.get('submit')({preventDefault(){}});assert.equal(sent.length,0);assert.equal(dom.doc.activeElement,field('port'));
  cleanup();
});

test('manual passwords preserve whitespace and saved login bypasses empty password validation',async context=>{
  const dom=connectionDom(context),sent=[];let restores=0,entered=0;
  let cleanup=renderSwitchConnection(dom.host,{request:switchRequest(async(...args)=>{sent.push(args);return completeScene();}),onConnected:async()=>{entered++;}});
  let form=dom.findAll(dom.host,node=>node.tagName==='form')[0];
  const fields=Object.fromEntries(dom.findAll(form,node=>node.tagName==='input').map(field=>[field.name,field]));
  fields.host.value='switch.example';fields.username.value='local-user';fields.password.value='  fixture password  ';
  form.listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(sent[0][2].password,'  fixture password  ');assert.equal(entered,1);cleanup();
  cleanup=renderSwitchConnection(dom.host,{request:switchRequest(async()=>{throw new Error('No manual request expected');}),session:{has_saved_login:true},restoreSaved:async()=>{restores++;return completeScene();},onConnected:async()=>{entered++;}});
  dom.button('Connect with saved login').click();await flush();assert.equal(restores,1);assert.equal(entered,2);cleanup();
});

test('optional connection help does not submit or clear the login form',context=>{
  const dom=connectionDom(context);let help=0;
  const cleanup=renderSwitchConnection(dom.host,{request:switchRequest(async()=>{throw new Error('Help must not submit');}),onConnected:async()=>{},onHelp:()=>{help++;}});
  const password=dom.findAll(dom.host,node=>node.name==='password')[0];password.value='fixture-password';
  dom.button('Connection help').click();assert.equal(help,1);assert.equal(password.value,'fixture-password');cleanup();
});

test('Escape cannot dismiss a scene choice during an in-flight activation',async context=>{
  const dom=connectionDom(context),sent=[];let finish;
  const result=showSceneChoice(async(...args)=>{sent.push(args);return new Promise(resolve=>{finish=resolve;});},pendingScene(),{signal:dom.lifecycle.signal});
  dom.button('Start new setup').click();let prevented=false;
  dom.dialog().listeners.get('cancel')({preventDefault(){prevented=true;}});
  assert.equal(prevented,true);assert.equal(sent.length,1);assert.equal(dom.dialog().open,true);
  finish(completeScene());await result;assert.equal(dom.dialog(),undefined);
});

test('scene choice hides unavailable saved lighting and preserves an active session on Cancel',async context=>{
  const dom=connectionDom(context),sent=[],original={logged_in:true,workspace_id:'existing',pending_scene_choice:null};
  const session=pendingScene({...original,pending_scene_choice:{connection_id:'candidate-b',legacy_revision:null,legacy_available:false}});
  const result=showSceneChoice(async(...args)=>{sent.push(args);return original;},session,{signal:dom.lifecycle.signal});
  assert.equal(dom.button('Use saved lighting').parentNode.hidden,true);
  dom.button('Use saved lighting').click();assert.equal(sent.length,0);
  let prevented=false;dom.dialog().listeners.get('cancel')({preventDefault(){prevented=true;}});
  assert.equal(prevented,true);assert.equal(await result,original);
  assert.equal(sent[0][2].choice,'cancel');assert.equal(sent[0][2].connection_id,'candidate-b');
});

test('stale saved lighting must be reviewed again and retry sends the fresh revision',async context=>{
  const dom=connectionDom(context),sent=[],session=pendingScene();let posts=0;
  const latest=pendingScene({pending_scene_choice:{...session.pending_scene_choice,legacy_revision:'revision-b'}});
  const result=showSceneChoice(async(path,method,body)=>{
    sent.push({path,method,body});if(!method)return latest;
    if(++posts===1)throw Object.assign(new Error('private storage path'),{code:'scene_choice_stale'});
    return completeScene();
  },session,{signal:dom.lifecycle.signal});
  dom.button('Use saved lighting').click();await flush();
  assert.ok(dom.dialog());assert.equal(posts,1);
  const notice=dom.byClass(dom.dialog(),'connection-notice')[0];
  assert.match(notice.textContent,/changed/);assert.doesNotMatch(notice.textContent,/private/);
  dom.button('Use saved lighting').click();await result;
  assert.equal(sent.filter(item=>item.method)[1].body.legacy_revision,'revision-b');
});

test('expired candidates offer a return to connection instead of silently retrying output',async context=>{
  const dom=connectionDom(context),sent=[],latest={logged_in:false,pending_scene_choice:null};
  const result=showSceneChoice(async(path,method)=>{sent.push({path,method});if(!method)return latest;throw Object.assign(new Error(),{code:'scene_choice_expired'});},pendingScene(),{signal:dom.lifecycle.signal});
  dom.button('Start new setup').click();await flush();
  assert.equal(dom.button('Start new setup').disabled,true);dom.button('Back to connection').click();
  assert.equal(await result,latest);assert.equal(sent.filter(item=>item.method).length,1);
});

test('an invalid saved scene permits only a fresh explicit new setup or cancel',async context=>{
  const dom=connectionDom(context),session=pendingScene(),sent=[];
  const result=showSceneChoice(async(path,method,body)=>{if(!method)return session;sent.push(body.choice);if(body.choice==='use_saved')throw Object.assign(new Error(),{code:'invalid_saved_scene'});return completeScene();},session,{signal:dom.lifecycle.signal});
  dom.button('Use saved lighting').click();await flush();
  assert.equal(dom.button('Use saved lighting').disabled,true);assert.equal(dom.button('Start new setup').disabled,false);
  assert.deepEqual(sent,['use_saved']);dom.button('Start new setup').click();assert.equal((await result).logged_in,true);
  assert.deepEqual(sent,['use_saved','start_new']);
});

test('scene recovery errors keep new output blocked and do not blame credentials',async context=>{
  const dom=connectionDom(context),session=pendingScene();
  const result=showSceneChoice(async(_path,method,body)=>{if(!method)return session;if(body.choice==='cancel')return {logged_in:false,pending_scene_choice:null};throw Object.assign(new Error('private details'),{code:'output_recovery_required'});},session,{signal:dom.lifecycle.signal});
  dom.button('Start new setup').click();await flush();
  assert.equal(dom.button('Start new setup').disabled,true);assert.equal(dom.button('Use saved lighting').disabled,true);
  assert.match(dom.byClass(dom.dialog(),'connection-notice')[0].textContent,/connect again to retry the recovery/);
  dom.button('Cancel').click();await result;
  assert.doesNotMatch(connectionError({code:'output_recovery_required'}),/password|reconnect/i);
  assert.match(connectionError({code:'instance_running'}),/already running/);
  assert.match(connectionError({code:'instance_unavailable'}),/another session/);
});

test('aborting a scene chooser removes its dialog without starting or canceling hardware output',async context=>{
  const dom=connectionDom(context),sent=[];
  const result=showSceneChoice(async(...args)=>{sent.push(args);},pendingScene(),{signal:dom.lifecycle.signal});
  dom.lifecycle.abort();assert.equal(await result,null);assert.equal(dom.dialog(),undefined);assert.deepEqual(sent,[]);
});

test('canceling a manual-login scene choice preserves the filled form and successful selection alone enters',async context=>{
  const dom=connectionDom(context),entered=[],sent=[];
  const request=async(path,method,body)=>{sent.push({path,method,body});if(path==='/api/session/login')return pendingScene();return body.choice==='cancel'?{logged_in:false,pending_scene_choice:null}:completeScene();};
  const cleanup=renderSwitchConnection(dom.host,{request:switchRequest(request),onConnected:value=>entered.push(value)});dom.after(cleanup);
  const form=dom.findAll(dom.host,node=>node.tagName==='form')[0],field=name=>dom.findAll(form,node=>node.name===name)[0];
  field('host').value='switch.example';field('username').value='local-user';field('password').value='fixture-password';field('remember').checked=true;
  form.listeners.get('input')();form.listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(entered.length,0);assert.equal(field('password').value,'fixture-password');
  dom.button('Cancel').click();await flush();
  assert.equal(entered.length,0);assert.equal(field('password').value,'fixture-password');assert.equal(field('host').value,'switch.example');assert.equal(field('remember').checked,true);
  form.listeners.get('submit')({preventDefault(){}});await flush();dom.button('Use saved lighting').click();await flush();
  assert.equal(entered.length,1);assert.equal(field('password').value,'');
  assert.deepEqual(sent.map(item=>item.path),['/api/session/login','/api/session/scene-choice','/api/session/login','/api/session/scene-choice']);
});

test('saved-login session updates require a lighting choice before workspace entry',async context=>{
  const dom=connectionDom(context),entered=[];
  const cleanup=renderSwitchConnection(dom.host,{request:switchRequest(async()=>completeScene()),session:{logged_in:false,connecting:true},onConnected:value=>entered.push(value)});dom.after(cleanup);
  cleanup.update(pendingScene({has_saved_login:true}));await flush();
  assert.equal(entered.length,0);assert.ok(dom.dialog());
  dom.button('Start new setup').click();await flush();assert.equal(entered.length,1);
});

test('an older successful choice cannot dismiss a newer pending connection',async context=>{
  const dom=connectionDom(context),sent=[];let resolveFirst;
  const request=async(_path,_method,body)=>{sent.push(body);if(sent.length===1)return new Promise(resolve=>{resolveFirst=resolve;});return {logged_in:false,pending_scene_choice:null};};
  const result=showSceneChoice(request,pendingScene(),{signal:dom.lifecycle.signal});
  dom.button('Use saved lighting').click();
  showSceneChoice(request,pendingScene({pending_scene_choice:{connection_id:'candidate-new',legacy_revision:'revision-new',legacy_available:true}}));
  resolveFirst(completeScene());await flush();assert.ok(dom.dialog());
  dom.button('Cancel').click();await result;
  assert.equal(sent[1].connection_id,'candidate-new');assert.equal(sent[1].choice,'cancel');
});

test('an existing active session is not mistaken for successful new output after a lost response',async context=>{
  const dom=connectionDom(context),existing={logged_in:true,workspace_id:'existing',pending_scene_choice:null};
  const result=showSceneChoice(async(_path,method)=>{if(!method)return existing;throw new Error('unavailable');},pendingScene({...existing,pending_scene_choice:pendingScene().pending_scene_choice}),{signal:dom.lifecycle.signal});
  dom.button('Start new setup').click();await flush();assert.ok(dom.dialog());assert.equal(dom.button('Start new setup').disabled,true);
  dom.button('Back to connection').click();assert.equal(await result,existing);
});

test('a recovery error already in the session blocks both lighting choices before any request',async context=>{
  const dom=connectionDom(context),sent=[];
  const result=showSceneChoice(async(...args)=>{sent.push(args);return {logged_in:false,pending_scene_choice:null};},pendingScene({error:{code:'output_recovery_required'}}),{signal:dom.lifecycle.signal});
  assert.equal(dom.button('Use saved lighting').disabled,true);assert.equal(dom.button('Start new setup').disabled,true);
  dom.button('Start new setup').click();assert.equal(sent.length,0);dom.button('Cancel').click();await result;
  assert.equal(sent[0][2].choice,'cancel');
});

// SSH host key review

const FP_A='SHA256:'+'A'.repeat(43),FP_B='SHA256:'+'B'.repeat(42)+'A',SWITCH='192.168.1.2';
const untrustedKey=(changes={})=>({code:'host_key_untrusted',message:'Check this switch’s SSH key fingerprint before signing in.',
  host:SWITCH,port:22,ssh_host_key:FP_A,ssh_host_key_type:'ssh-ed25519',...changes});
const changedKey=(changes={})=>({...untrustedKey(),code:'host_key_changed',ssh_host_key:FP_B,ssh_host_key_type:'ssh-rsa',saved_ssh_host_key:FP_A,...changes});
const refused=detail=>Object.assign(new Error(detail.message||'refused'),{code:detail.code,details:detail});
const LOCAL_ACCESS={available:false,enabled:false,ready:false,can_configure:true,url:null,can_close_workspace:true};
const UNTRUSTED_BODY='EE has no saved SSH key for 192.168.1.2. Check that this fingerprint belongs to your switch. EE sends your password only after you accept the key.';
const CHANGED_BODY='The device at 192.168.1.2 presented a different SSH key than the one EE saved. This happens after the switch is reset or replaced. If you did not reset or replace it, another device may be using its address. In that case, keep the saved key and check your network.';
const KEPT_DATA='Lighting, schedules and port names saved for the previous switch stay saved but are not used with the new key.';

// Answers private access with `access` (a value, or a function that may throw) and records every other request.
function keyRequest(send,access=LOCAL_ACCESS){
  const sent=[];
  const request=async(path,...args)=>{
    if(path==='/api/private-access'&&!args.length)return typeof access==='function'?access():access;
    sent.push([path,...args]);return send(path,...args);
  };
  return {request,sent};
}
function keyView(dom){
  const form=dom.findAll(dom.host,node=>node.tagName==='form')[0];
  const review=dom.byClass(dom.host,'host-key-review')[0];
  const visible=node=>{for(let current=node;current;current=current.parentNode)if(current.hidden)return false;return true;};
  return {form,review,field:name=>dom.findAll(form,node=>node.name===name)[0],
    heading:()=>dom.findAll(review,node=>node.tagName==='h2')[0],
    texts:()=>dom.findAll(review,node=>node.tagName==='p'&&visible(node)).map(node=>node.textContent),
    fingerprints:()=>dom.findAll(review,node=>node.tagName==='code').map(node=>node.textContent),
    shown:text=>dom.findAll(dom.host,node=>node.tagName==='button'&&node.textContent===text&&visible(node)).length===1,
    status:()=>dom.byClass(dom.host,'connection-notice')[0].textContent,
    input:name=>form.listeners.get('input')({target:dom.findAll(form,node=>node.name===name)[0]}),
    submit:()=>form.listeners.get('submit')({preventDefault(){}})};
}
function fillLogin(view,password='fixture-password'){
  view.field('host').value=SWITCH;view.field('username').value='local-user';view.field('password').value=password;
}

test('host key problems are read only from complete key errors',()=>{
  const untrusted=hostKeyProblem(untrustedKey());
  assert.deepEqual({...untrusted,body:undefined},{code:'host_key_untrusted',changed:false,host:SWITCH,port:22,fingerprint:FP_A,savedFingerprint:null,
    keyType:'ssh-ed25519',key:['host_key_untrusted',SWITCH,22,FP_A,''].join('\n'),address:SWITCH,route:'/api/session/host-key/accept',body:undefined});
  assert.deepEqual(untrusted.body,{host:SWITCH,port:22,ssh_host_key:FP_A});
  const changed=hostKeyProblem(refused(changedKey({port:2222,host:' Switch.Example '})));
  assert.deepEqual(changed.body,{host:' Switch.Example ',port:2222,ssh_host_key:FP_B,saved_ssh_host_key:FP_A});
  assert.equal(changed.route,'/api/session/host-key/replace');assert.equal(changed.address,'Switch.Example port 2222');
  assert.equal(changed.key,['host_key_changed','switch.example',2222,FP_B,FP_A].join('\n'));
  assert.equal(hostKeyProblem(untrustedKey({ssh_host_key_type:'bad type'})).keyType,null);
  assert.equal(hostKeyProblem(untrustedKey({ssh_host_key_type:undefined})).keyType,null);
  for(const invalid of [untrustedKey({code:'auth_failed'}),untrustedKey({ssh_host_key:FP_A.slice(0,-1)}),untrustedKey({port:'22'}),
    untrustedKey({port:true}),untrustedKey({port:0}),untrustedKey({port:22.5}),untrustedKey({host:''}),untrustedKey({host:'   '}),
    untrustedKey({host:'switch\u0000'}),untrustedKey({host:'a'.repeat(254)}),changedKey({saved_ssh_host_key:undefined}),
    changedKey({saved_ssh_host_key:FP_B}),untrustedKey({code:'toString'}),untrustedKey({code:'__proto__'}),untrustedKey({code:'constructor'}),
    Object.create({code:'host_key_untrusted',host:SWITCH,port:22,ssh_host_key:FP_A}),null,'host_key_untrusted'])
    assert.equal(hostKeyProblem(invalid),null);
});

test('host key errors and route failures have their own messages',()=>{
  assert.equal(connectionError({code:'host_key_untrusted'}),'Check the switch’s SSH key before EE sends your password.');
  assert.equal(connectionError({code:'host_key_changed'}),'The switch’s SSH key changed. Review the saved and new keys before connecting.');
  const changed=hostKeyProblem(changedKey()),untrusted=hostKeyProblem(untrustedKey());
  assert.equal(hostKeyError({code:'host_key_stale'},changed),'The switch’s SSH key changed again. Connect again to review the current key.');
  assert.match(hostKeyError({code:'host_key_in_use'},changed),/^EE still has an open switch connection\./);
  assert.match(hostKeyError({code:'switch_playback_reserved'},changed),/^EE is handing lighting back to UniFi/);
  assert.equal(hostKeyError({code:'host_key_save_failed'},changed),'The SSH key could not be saved. Check the EE data folder and try again.');
  assert.equal(hostKeyError({code:'invalid_credentials'},changed),'The SSH key details were not accepted. Connect again to review the key.');
  assert.equal(hostKeyError({code:'local_access_required'},changed),'A new key can be accepted only in EE on the computer that runs it.');
  assert.equal(hostKeyError({code:'local_access_required'},untrusted),'This key can be accepted only in EE on the computer that runs it.');
  for(const error of [{code:'toString'},new Error('timeout'),undefined])
    assert.equal(hostKeyError(error,changed),'The SSH key could not be saved. Connect again to review the key.');
  assert.equal(canAcceptHostKey({can_close_workspace:true}),true);
  for(const value of [{can_close_workspace:false},{},null])assert.equal(canAcceptHostKey(value),false);
});

test('a login refused for an unknown SSH key sends the password only after Accept key',async context=>{
  const dom=connectionDom(context);let entered=0,logins=0;
  const {request,sent}=keyRequest(async path=>{
    if(path==='/api/session/login'&&++logins===1)throw refused(untrustedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  });
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{entered++;}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view,'  fixture password  ');
  view.submit();await flush();
  assert.equal(view.review.hidden,false);assert.equal(dom.byClass(dom.host,'host-key-review').length,1);
  assert.equal(view.review.getAttribute('role'),'group');assert.equal(view.review.getAttribute('aria-labelledby'),view.heading().id);
  assert.equal(view.heading().textContent,'Check the switch’s SSH key');assert.equal(dom.doc.activeElement,view.heading());
  assert.deepEqual(view.texts(),[UNTRUSTED_BODY,'Fingerprint (ssh-ed25519)','An SSH client that has connected to this switch shows the same fingerprint for the same key type.']);
  assert.deepEqual(view.fingerprints(),[FP_A]);
  assert.ok(view.shown('Accept key')&&view.shown('Cancel'));assert.equal(view.status(),'The switch’s SSH key needs your approval.');
  assert.deepEqual(sent.map(([path])=>path),['/api/session/login']);
  dom.button('Accept key').click();await flush();await flush();
  assert.deepEqual(sent.slice(1),[['/api/session/host-key/accept','POST',{host:SWITCH,port:22,ssh_host_key:FP_A}],
    ['/api/session/login','POST',{host:SWITCH,username:'local-user',password:'  fixture password  ',port:22,remember:false}]]);
  assert.equal(entered,1);assert.equal(view.review.hidden,true);
});

test('Cancel closes the key review without a request and keeps the password',async context=>{
  const dom=connectionDom(context);
  const {request,sent}=keyRequest(async()=>{throw refused(untrustedKey());});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  dom.button('Cancel').click();
  assert.equal(view.review.hidden,true);assert.equal(sent.length,1);
  assert.equal(view.status(),'Connection canceled. EE did not send your password.');assert.equal(view.field('password').value,'fixture-password');
});

test('a changed key is replaced on this computer with both fingerprints echoed back',async context=>{
  const dom=connectionDom(context);let logins=0;
  const {request,sent}=keyRequest(async path=>{
    if(path==='/api/session/login'&&++logins===1)throw refused(changedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  });
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  assert.equal(view.heading().textContent,'The switch’s SSH key changed');assert.match(view.review.className,/\berror\b/);
  assert.deepEqual(view.texts(),[CHANGED_BODY,KEPT_DATA,'Saved key','New key (ssh-rsa)']);
  assert.deepEqual(view.fingerprints(),[FP_A,FP_B]);
  assert.ok(view.shown('Accept new key')&&view.shown('Keep saved key'));
  assert.doesNotMatch(dom.button('Accept new key').className,/primary/);
  assert.equal(view.status(),'The switch’s SSH key changed. Review it before connecting.');
  dom.button('Accept new key').click();await flush();await flush();
  assert.deepEqual(sent.slice(1).map(([path,,body])=>[path,body?.password??body]),[
    ['/api/session/host-key/replace',{host:SWITCH,port:22,saved_ssh_host_key:FP_A,ssh_host_key:FP_B}],
    ['/api/session/login','fixture-password']]);
});

for(const [name,access] of [['false',{...LOCAL_ACCESS,can_close_workspace:false}],['missing',{...LOCAL_ACCESS,can_close_workspace:undefined}],
  ['refused',()=>{throw refused({code:'local_access_required'});}]]){
  test(`a remote client reviews a key but cannot accept it (private access ${name})`,async context=>{
    const dom=connectionDom(context);
    for(const [error,accept,dismiss,note,kept] of [
      [changedKey(),'Accept new key','Keep saved key','A new key can be accepted only in EE on the computer that runs it.','EE kept the saved SSH key and did not connect.'],
      [untrustedKey(),'Accept key','Cancel','This key can be accepted only in EE on the computer that runs it.','Connection canceled. EE did not send your password.']]){
      const {request,sent}=keyRequest(async()=>{throw refused(error);},access);
      const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});
      const view=keyView(dom);fillLogin(view);view.submit();await flush();
      assert.equal(view.shown(accept),false);assert.equal(view.texts().at(-1),note);
      dom.button(accept).click();assert.equal(sent.length,1);
      dom.button(dismiss).click();assert.equal(view.status(),kept);cleanup();
    }
  });
}

for(const code of ['local_access_required','switch_playback_reserved']){
  test(`a refused replacement (${code}) shows its reason and does not retry`,async context=>{
    const dom=connectionDom(context);
    const {request,sent}=keyRequest(async path=>{throw refused(path==='/api/session/login'?changedKey():{code});});
    const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
    const view=keyView(dom);fillLogin(view);view.submit();await flush();
    dom.button('Accept new key').click();await flush();
    assert.equal(view.review.hidden,true);assert.equal(view.status(),hostKeyError({code},hostKeyProblem(changedKey())));
    assert.deepEqual(sent.map(([path])=>path),['/api/session/login','/api/session/host-key/replace']);
    cleanup.update({logged_in:false,has_saved_login:false,error:changedKey()});
    assert.equal(view.review.hidden,true);
  });
}

test('an accept answered with host_key_stale closes the review without a retry',async context=>{
  const dom=connectionDom(context);
  const {request,sent}=keyRequest(async path=>{throw refused(path==='/api/session/login'?untrustedKey():{code:'host_key_stale'});});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  dom.button('Accept key').click();await flush();
  assert.equal(view.review.hidden,true);assert.equal(view.status(),'The switch’s SSH key changed again. Connect again to review the current key.');
  assert.equal(sent.length,2);
});

test('a key problem from the saved login is reviewed from the session and restores once accepted',async context=>{
  const dom=connectionDom(context);let restores=0,entered=0;
  const {request,sent}=keyRequest(async()=>({logged_in:false,error:null}));
  const session={logged_in:false,has_saved_login:true,remembered:{host:SWITCH,port:22,username:'local-user'},error:changedKey()};
  const cleanup=renderSwitchConnection(dom.host,{request,session,restoreSaved:async()=>{restores++;return completeScene();},onConnected:async()=>{entered++;}});dom.after(cleanup);
  const view=keyView(dom);await flush();
  assert.equal(view.review.hidden,false);assert.deepEqual(sent,[]);
  dom.button('Accept new key').click();await flush();await flush();
  assert.deepEqual(sent.map(([path])=>path),['/api/session/host-key/replace']);assert.equal(restores,1);assert.equal(entered,1);
});

test('a key accepted for the address in the form asks the user to connect',async context=>{
  const dom=connectionDom(context);
  const {request,sent}=keyRequest(async()=>({logged_in:false,error:null}));
  const cleanup=renderSwitchConnection(dom.host,{request,session:{logged_in:false},onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);view.field('host').value=SWITCH;
  cleanup.update({logged_in:false,error:untrustedKey()});await flush();
  assert.equal(view.review.hidden,false);
  dom.button('Accept key').click();await flush();
  assert.equal(view.status(),'SSH key saved. Connect to the switch to continue.');
  assert.equal(dom.doc.activeElement,view.field('password'));assert.deepEqual(sent.map(([path])=>path),['/api/session/host-key/accept']);
});

test('a key problem for another address opens no review',async context=>{
  const dom=connectionDom(context);
  const {request}=keyRequest(async()=>completeScene());
  const cleanup=renderSwitchConnection(dom.host,{request,session:{logged_in:false},onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);
  cleanup.update({logged_in:false,error:changedKey()});
  assert.equal(view.review.hidden,true);assert.equal(view.shown('Accept new key'),false);
  assert.equal(view.status(),connectionError({code:'host_key_changed'}));
  view.field('host').value='other.example';
  cleanup.update({logged_in:false,has_saved_login:true,remembered:{host:'other.example',port:22},error:changedKey()});
  assert.equal(view.review.hidden,true);
});

test('editing the address or port closes the review and other edits keep it',async context=>{
  const dom=connectionDom(context);
  const {request}=keyRequest(async()=>completeScene());
  const session={logged_in:false,has_saved_login:true,remembered:{host:SWITCH,port:22},error:untrustedKey()};
  const cleanup=renderSwitchConnection(dom.host,{request,session,restoreSaved:async()=>completeScene(),onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);await flush();
  cleanup.update({...session,remembered:{host:SWITCH,port:22,username:'local-user'}});assert.equal(view.review.hidden,false);
  view.input('password');assert.equal(view.review.hidden,false);
  view.input('host');assert.equal(view.review.hidden,true);
  cleanup.update(session);assert.equal(view.review.hidden,false);
  view.input('port');assert.equal(view.review.hidden,true);
});

test('repeated session updates keep one review, its retry and its status',async context=>{
  const dom=connectionDom(context);let logins=0;
  const {request,sent}=keyRequest(async path=>{
    if(path==='/api/session/login'&&++logins===1)throw refused(untrustedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  });
  const cleanup=renderSwitchConnection(dom.host,{request,restoreSaved:async()=>{throw new Error('the login form must be retried');},onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  for(let index=0;index<3;index++)cleanup.update({logged_in:false,has_saved_login:true,remembered:{host:SWITCH,port:22},error:untrustedKey()});
  assert.equal(dom.byClass(dom.host,'host-key-review').length,1);assert.equal(view.status(),'The switch’s SSH key needs your approval.');
  dom.button('Accept key').click();await flush();await flush();
  assert.deepEqual(sent.map(([path])=>path),['/api/session/login','/api/session/host-key/accept','/api/session/login']);
});

test('a key error with an invalid fingerprint shows the connection message only',async context=>{
  const dom=connectionDom(context);
  const {request}=keyRequest(async()=>{throw refused(untrustedKey({ssh_host_key:FP_A.slice(0,-1)}));});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  assert.equal(view.review.hidden,true);assert.equal(view.status(),'Check the switch’s SSH key before EE sends your password.');
});

test('the host key flow opens reviews by their source and remembers a dismissed key',()=>{
  const flow=createHostKeyFlow(),form={host:'',port:22},idle={pending:false,blocked:false};
  const saved={logged_in:false,has_saved_login:true,remembered:{host:' 192.168.1.2 ',port:22},error:changedKey()};
  assert.equal(flow.fromSession(saved,form,idle),true);assert.equal(flow.retry,'saved');
  assert.equal(flow.fromSession(saved,form,idle),false);assert.equal(flow.retry,'saved');
  flow.fromAction(hostKeyProblem(changedKey()),'form');assert.equal(flow.retry,'form');
  flow.targetEdited();assert.equal(flow.open,null);
  assert.equal(flow.fromSession(saved,form,idle),true);flow.dismiss();
  assert.equal(flow.fromSession(saved,form,idle),false);assert.equal(flow.open,null);
  flow.fromAction(hostKeyProblem(changedKey()),'form');assert.ok(flow.open);flow.close();
  const unsaved={logged_in:false,error:untrustedKey()};
  assert.equal(flow.fromSession(unsaved,form,idle),false);
  assert.equal(flow.fromSession(unsaved,{host:'other.example',port:22},idle),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:2222},idle),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},{pending:true,blocked:false}),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},{pending:false,blocked:true}),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},idle),true);assert.equal(flow.retry,'none');
  assert.equal(flow.fromSession({...unsaved,logged_in:true},form,idle),false);assert.equal(flow.open,null);
});
