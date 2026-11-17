import test from 'node:test';
import assert from 'node:assert/strict';
import {connectionError, controllerError, controllerReconnectControls, controllerReconnectConfirmed, remoteCanRestore, remoteConnectionError, showSceneChoice, renderSwitchConnection,
  hostKeyProblem, hostKeyError, canAcceptHostKey, createHostKeyFlow, sshHostKey, renderLanAccess, renderLanPairing, lanAccessError, LAN_PAGE_NOTE} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

test('a request refused by private access names the computer running EE', () => {
  assert.equal(connectionError({code:'local_access_required'}), 'This action is available only on the computer running EE.');
});

test('remote devices are sent to the computer running EE for login problems', () => {
  assert.equal(remoteConnectionError({code:'no_saved_login'}), 'No switch login is saved on the computer running EE. Sign in to the switch on that computer. This page opens the workspace when the switch connects.');
  assert.equal(remoteConnectionError({code:'saved_login_unavailable'}), 'The saved login could not be opened. Sign in to the switch on the computer running EE.');
  for (const code of ['auth_failed','authentication_failed'])
    assert.equal(remoteConnectionError({code}), 'The switch rejected the saved login. Sign in to the switch on the computer running EE.');
  assert.equal(remoteConnectionError({code:'host_key_changed'}), 'The switch identity has changed. Review its SSH host key on the computer running EE before reconnecting.');
  assert.equal(remoteConnectionError({code:'unreachable'}), connectionError({code:'unreachable'}));
});

test('a remote device can restore only a saved login that opens', () => {
  assert.equal(remoteCanRestore({has_saved_login:true}), true);
  for (const session of [{has_saved_login:false}, {}, {has_saved_login:true,saved_login_error:'x'}, null])
    assert.equal(remoteCanRestore(session), false);
});

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

test('reserved playback and unusable saved SSH keys show the service message', () => {
  for (const [code, message] of [['switch_playback_reserved', 'Return switch playback to UniFi from the PC that saved it.'],
    ['host_key_save_failed', 'SSH host keys were saved by a newer version of EE. Update EE to use them.']]) {
    assert.equal(connectionError(Object.assign(new Error(message), {code})), message, code);
    assert.equal(connectionError({code}), 'The connection could not be confirmed. Check the details and try again.', code);
  }
});

test('lighting that could not be confirmed, applied or saved has its own message', () => {
  assert.equal(connectionError({code:'output_unconfirmed',message:'private-host'}), 'EE could not confirm the switch lighting. Check that the switch is reachable, then connect again.');
  assert.equal(connectionError({code:'invalid_settings'}), 'EE could not apply its lighting settings to this switch. Connect again; if this continues, check the advanced settings in ee.toml.');
  assert.equal(connectionError({code:'scene_save_failed'}), 'The lighting setup could not be saved. Check the EE data folder and try again.');
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
    Object.defineProperty(element,'firstElementChild',{get:()=>element.children[0]});
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

const FP_A='SHA256:'+'A'.repeat(43),FP_B='SHA256:'+'B'.repeat(42)+'A',FP_C='SHA256:'+'C'.repeat(43),SWITCH='192.168.1.2';
const changedKey=(changes={})=>({code:'host_key_changed',message:'The switch’s SSH key changed. Check the new fingerprint before signing in.',
  host:SWITCH,port:22,ssh_host_key:FP_B,ssh_host_key_type:'ssh-rsa',saved_ssh_host_key:FP_A,...changes});
const refused=detail=>Object.assign(new Error(detail.message||'refused'),{code:detail.code,details:detail});
const LOCAL_ACCESS={available:false,enabled:false,ready:false,can_configure:true,url:null,can_close_workspace:true,local:true};
// The private gateway's own answer (preview/private-gateway.mjs), and a read-only answer from the local gateway to a
// tab that may not change private access or close the workspace.
const GATEWAY_ACCESS={available:true,enabled:true,ready:true,url:null,can_configure:false,can_close_workspace:false,can_change_port:false,setup_action:null,action_url:null};
const READ_ONLY_LOCAL_ACCESS={...GATEWAY_ACCESS,admin_required:true,local:true};
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

test('changed key problems are read only from complete key errors',()=>{
  const changed=hostKeyProblem(changedKey());
  assert.deepEqual({...changed,body:undefined},{host:SWITCH,port:22,fingerprint:FP_B,savedFingerprint:FP_A,keyType:'ssh-rsa',
    key:[SWITCH,22,FP_B,FP_A].join('\n'),address:SWITCH,body:undefined});
  assert.deepEqual(changed.body,{host:SWITCH,port:22,ssh_host_key:FP_B,saved_ssh_host_key:FP_A});
  const other=hostKeyProblem(refused(changedKey({port:2222,host:' Switch.Example '})));
  assert.deepEqual(other.body,{host:' Switch.Example ',port:2222,ssh_host_key:FP_B,saved_ssh_host_key:FP_A});
  assert.equal(other.address,'Switch.Example port 2222');assert.equal(other.key,['switch.example',2222,FP_B,FP_A].join('\n'));
  assert.equal(hostKeyProblem(changedKey({ssh_host_key_type:'bad type'})).keyType,null);
  assert.equal(hostKeyProblem(changedKey({ssh_host_key_type:undefined})).keyType,null);
  for(const invalid of [changedKey({code:'host_key_untrusted'}),changedKey({code:'auth_failed'}),changedKey({ssh_host_key:FP_B.slice(0,-1)}),
    changedKey({port:'22'}),changedKey({port:true}),changedKey({port:0}),changedKey({port:22.5}),changedKey({host:''}),changedKey({host:'   '}),
    changedKey({host:'switch\u0000'}),changedKey({host:'a'.repeat(254)}),changedKey({saved_ssh_host_key:undefined}),
    changedKey({saved_ssh_host_key:FP_B}),changedKey({code:'toString'}),changedKey({code:'__proto__'}),changedKey({code:'constructor'}),
    Object.create(changedKey()),null,'host_key_changed'])
    assert.equal(hostKeyProblem(invalid),null);
});

test('the connected switch key is read only when its fingerprint is valid',()=>{
  assert.deepEqual(sshHostKey({ssh_host_key:FP_A,ssh_host_key_type:'ssh-ed25519'}),{fingerprint:FP_A,keyType:'ssh-ed25519'});
  assert.deepEqual(sshHostKey({ssh_host_key:FP_A,ssh_host_key_type:'bad type'}),{fingerprint:FP_A,keyType:null});
  for(const session of [{ssh_host_key:'SHA256:short'},{ssh_host_key:null},{},null])assert.equal(sshHostKey(session),null);
});

test('host key errors and replacement failures have their own messages',()=>{
  assert.equal(connectionError({code:'host_key_changed'}),'The switch’s SSH key changed. Review the saved and new keys before connecting.');
  assert.equal(connectionError({code:'host_key_untrusted'}),'The connection could not be confirmed. Check the details and try again.');
  assert.equal(remoteConnectionError({code:'host_key_untrusted'}),connectionError({code:'host_key_untrusted'}));
  assert.equal(hostKeyError({code:'host_key_stale'}),'The switch’s SSH key changed again. Connect again to review the current key.');
  assert.match(hostKeyError({code:'host_key_in_use'}),/^EE still has an open switch connection\./);
  assert.match(hostKeyError({code:'switch_playback_reserved'}),/^EE is handing lighting back to UniFi/);
  assert.equal(hostKeyError({code:'host_key_save_failed'}),'The SSH key could not be saved. Check the EE data folder and try again.');
  assert.equal(hostKeyError({code:'invalid_credentials'}),'The SSH key details were not accepted. Connect again to review the key.');
  assert.equal(hostKeyError({code:'local_access_required'}),'A new key can be accepted only in EE on the computer that runs it.');
  for(const error of [{code:'toString'},new Error('timeout'),undefined])
    assert.equal(hostKeyError(error),'The SSH key could not be saved. Connect again to review the key.');
  const page={hostname:'127.0.0.1'};
  for(const value of [LOCAL_ACCESS,READ_ONLY_LOCAL_ACCESS])assert.equal(canAcceptHostKey(value,page),true);
  for(const value of [GATEWAY_ACCESS,{can_close_workspace:true},{local:false},{local:'true'},{},null])assert.equal(canAcceptHostKey(value,page),false);
  // A LAN answer can be changed on the way, so only a page on this computer accepts a key.
  for(const hostname of ['192.168.1.2','ee.example.ts.net'])assert.equal(canAcceptHostKey(LOCAL_ACCESS,{hostname}),false,hostname);
  assert.equal(canAcceptHostKey(LOCAL_ACCESS,{hostname:'localhost'}),true);
});

test('a login to a new switch sends the password at once and opens no key review',async context=>{
  const dom=connectionDom(context);let entered=0;
  const {request,sent}=keyRequest(async path=>path==='/api/session/login'?completeScene():{logged_in:false,error:null});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{entered++;}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();await flush();
  assert.deepEqual(sent.map(([path])=>path),['/api/session/login']);
  assert.equal(entered,1);assert.equal(view.review.hidden,true);
});

test('a changed key is replaced on this computer with both fingerprints echoed back',async context=>{
  const dom=connectionDom(context);let logins=0;
  const {request,sent}=keyRequest(async path=>{
    if(path==='/api/session/login'&&++logins===1)throw refused(changedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  });
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view,'  fixture password  ');view.submit();await flush();
  assert.equal(view.review.hidden,false);assert.equal(dom.byClass(dom.host,'host-key-review').length,1);
  assert.equal(view.review.getAttribute('role'),'group');assert.equal(view.review.getAttribute('aria-labelledby'),view.heading().id);
  assert.equal(view.heading().textContent,'The switch’s SSH key changed');assert.match(view.review.className,/\berror\b/);
  assert.equal(dom.doc.activeElement,view.heading());
  assert.deepEqual(view.texts(),[CHANGED_BODY,KEPT_DATA,'Saved key','New key (ssh-rsa)']);
  assert.deepEqual(view.fingerprints(),[FP_A,FP_B]);
  assert.ok(view.shown('Accept new key')&&view.shown('Keep saved key'));
  assert.doesNotMatch(dom.button('Accept new key').className,/primary/);
  assert.equal(view.status(),'The switch’s SSH key changed. Review it before connecting.');
  assert.deepEqual(sent.map(([path])=>path),['/api/session/login']);
  dom.button('Accept new key').click();await flush();await flush();
  assert.deepEqual(sent.slice(1),[['/api/session/host-key/replace','POST',{host:SWITCH,port:22,ssh_host_key:FP_B,saved_ssh_host_key:FP_A}],
    ['/api/session/login','POST',{host:SWITCH,username:'local-user',password:'  fixture password  ',port:22,remember:false}]]);
  assert.equal(view.review.hidden,true);
});

test('a changed key without a known type is labeled New key',async context=>{
  const dom=connectionDom(context);
  const {request}=keyRequest(async()=>{throw refused(changedKey({ssh_host_key_type:null,port:2222}));});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.field('port').value='2222';view.submit();await flush();
  assert.deepEqual(view.texts(),[CHANGED_BODY.replace('192.168.1.2','192.168.1.2 port 2222'),KEPT_DATA,'Saved key','New key']);
});

test('Keep saved key closes the review without a request and keeps the password',async context=>{
  const dom=connectionDom(context);
  const {request,sent}=keyRequest(async()=>{throw refused(changedKey());});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  dom.button('Keep saved key').click();
  assert.equal(view.review.hidden,true);assert.equal(sent.length,1);
  assert.equal(view.status(),'EE kept the saved SSH key and did not connect.');assert.equal(view.field('password').value,'fixture-password');
});

test('a local tab without administration access can still accept a key',async context=>{
  const dom=connectionDom(context);let logins=0;
  const {request,sent}=keyRequest(async path=>{
    if(path==='/api/session/login'&&++logins===1)throw refused(changedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  },READ_ONLY_LOCAL_ACCESS);
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  assert.ok(view.shown('Accept new key'));assert.ok(!view.texts().includes('A new key can be accepted only in EE on the computer that runs it.'));
  dom.button('Accept new key').click();await flush();await flush();
  assert.deepEqual(sent.slice(1).map(([path])=>path),['/api/session/host-key/replace','/api/session/login']);
});

for(const [name,access] of [['from the private gateway',GATEWAY_ACCESS],['local false',{...LOCAL_ACCESS,local:false}],
  ['refused',()=>{throw refused({code:'local_access_required'});}]]){
  test(`a remote client reviews a changed key but cannot accept it (private access ${name})`,async context=>{
    const dom=connectionDom(context);
    const {request,sent}=keyRequest(async()=>{throw refused(changedKey());},access);
    const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
    const view=keyView(dom);fillLogin(view);view.submit();await flush();
    assert.equal(view.shown('Accept new key'),false);
    assert.equal(view.texts().at(-1),'A new key can be accepted only in EE on the computer that runs it.');
    dom.button('Accept new key').click();assert.equal(sent.length,1);
    dom.button('Keep saved key').click();assert.equal(view.status(),'EE kept the saved SSH key and did not connect.');
  });
}

for(const code of ['local_access_required','switch_playback_reserved']){
  test(`a refused replacement (${code}) shows its reason and does not retry`,async context=>{
    const dom=connectionDom(context);
    const {request,sent}=keyRequest(async path=>{throw refused(path==='/api/session/login'?changedKey():{code});});
    const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
    const view=keyView(dom);fillLogin(view);view.submit();await flush();
    dom.button('Accept new key').click();await flush();
    assert.equal(view.review.hidden,true);assert.equal(view.status(),hostKeyError({code}));
    assert.deepEqual(sent.map(([path])=>path),['/api/session/login','/api/session/host-key/replace']);
    cleanup.update({logged_in:false,has_saved_login:false,error:changedKey()});
    assert.equal(view.review.hidden,true);
  });
}

test('a replacement answered with host_key_stale closes the review without a retry',async context=>{
  const dom=connectionDom(context);
  const {request,sent}=keyRequest(async path=>{throw refused(path==='/api/session/login'?changedKey():{code:'host_key_stale'});});
  const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  dom.button('Accept new key').click();await flush();
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
  cleanup.update({logged_in:false,error:changedKey()});await flush();
  assert.equal(view.review.hidden,false);
  dom.button('Accept new key').click();await flush();
  assert.equal(view.status(),'SSH key saved. Connect to the switch to continue.');
  assert.equal(dom.doc.activeElement,view.field('password'));assert.deepEqual(sent.map(([path])=>path),['/api/session/host-key/replace']);
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
  const session={logged_in:false,has_saved_login:true,remembered:{host:SWITCH,port:22},error:changedKey()};
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
    if(path==='/api/session/login'&&++logins===1)throw refused(changedKey());
    return path==='/api/session/login'?completeScene():{logged_in:false,error:null};
  });
  const cleanup=renderSwitchConnection(dom.host,{request,restoreSaved:async()=>{throw new Error('the login form must be retried');},onConnected:async()=>{}});dom.after(cleanup);
  const view=keyView(dom);fillLogin(view);view.submit();await flush();
  for(let index=0;index<3;index++)cleanup.update({logged_in:false,has_saved_login:true,remembered:{host:SWITCH,port:22},error:changedKey()});
  assert.equal(dom.byClass(dom.host,'host-key-review').length,1);assert.equal(view.status(),'The switch’s SSH key changed. Review it before connecting.');
  dom.button('Accept new key').click();await flush();await flush();
  assert.deepEqual(sent.map(([path])=>path),['/api/session/login','/api/session/host-key/replace','/api/session/login']);
});

test('a key error with an invalid fingerprint, or the removed first-use error, shows the connection message only',async context=>{
  const dom=connectionDom(context);
  for(const [error,text] of [[changedKey({ssh_host_key:FP_B.slice(0,-1)}),'The switch’s SSH key changed. Review the saved and new keys before connecting.'],
    [{...changedKey(),code:'host_key_untrusted',saved_ssh_host_key:undefined},'The connection could not be confirmed. Check the details and try again.']]){
    const {request}=keyRequest(async()=>{throw refused(error);});
    const cleanup=renderSwitchConnection(dom.host,{request,onConnected:async()=>{}});
    const view=keyView(dom);fillLogin(view);view.submit();await flush();
    assert.equal(view.review.hidden,true);assert.equal(view.status(),text);cleanup();
  }
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
  const unsaved={logged_in:false,error:changedKey({ssh_host_key:FP_C})};
  assert.equal(flow.fromSession(unsaved,form,idle),false);
  assert.equal(flow.fromSession(unsaved,{host:'other.example',port:22},idle),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:2222},idle),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},{pending:true,blocked:false}),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},{pending:false,blocked:true}),false);
  assert.equal(flow.fromSession(unsaved,{host:SWITCH,port:22},idle),true);assert.equal(flow.retry,'none');
  assert.equal(flow.fromSession({...unsaved,logged_in:true},form,idle),false);assert.equal(flow.open,null);
});

const LAN_STATEMENT='LAN access uses plain HTTP, so traffic between your devices and this PC is not encrypted. Anyone who can watch or change traffic on your network can read workspace data, such as the switch address and SSH username, port names, profiles and schedules, and can copy a device’s access key. With that key they can do anything a paired device can, including changing lighting and fans and handing lighting back to UniFi, until you remove that device or turn off LAN access. EE does not send switch or UniFi passwords over LAN access, because switch login and UniFi Network setup work only on this PC. Use Tailscale for an encrypted connection.';
const LAN_ADMIN='To change LAN access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started.';
const LAN_OFF={available:true,enabled:false,ready:false,port:4280,addresses:[],error:null,pairing:null,devices:[],max_devices:16,can_configure:true,can_change_port:true,admin_required:false};
const LAN_ON={...LAN_OFF,enabled:true,ready:true,can_change_port:false,addresses:[{url:'http://192.168.1.2:4280',interface:'Ethernet'}]};
const KITCHEN={id:'3f9c0a1b2c3d4e5f',name:'Kitchen iPad',client:'browser',paired_at:'2026-11-16T20:10:12.000Z',paired_from:'10.0.0.1',last_used_at:null,last_address:null};
function lanPanel(dom,answer,options){
  const sent=[];
  const cleanup=renderLanAccess(dom.host,async(...args)=>{sent.push(args);return structuredClone(await answer(...args));},options);dom.after(cleanup);
  const texts=()=>dom.findAll(dom.host,node=>['p','span','strong','code'].includes(node.tagName)&&!hiddenIn(node)).map(node=>node.textContent);
  const hiddenIn=node=>{for(let item=node;item;item=item.parentNode)if(item.hidden)return true;return false;};
  return {sent,cleanup,texts,shown:node=>!hiddenIn(node),
    toggle:()=>dom.findAll(dom.host,node=>node.name==='lan_access')[0],
    button:label=>dom.findAll(dom.host,node=>node.tagName==='button'&&node.textContent===label)[0],
    state:()=>dom.byClass(dom.host,'connection-state')[0].textContent,notice:()=>dom.byClass(dom.host,'connection-notice')[0].textContent};
}

test('the LAN panel always shows the unencrypted statement',async context=>{
  for(const answer of [()=>new Promise(()=>{}),()=>LAN_OFF,()=>LAN_ON,()=>({available:false,enabled:false,ready:false,can_configure:false,admin_required:false}),
    ()=>({...LAN_ON,pairing:null,devices:null,can_configure:false,admin_required:true})]){
    const dom=connectionDom(context),ui=lanPanel(dom,answer);await flush();
    const statement=dom.byClass(dom.host,'lan-access-warning')[0];
    assert.equal(statement.textContent,LAN_STATEMENT);assert.equal(ui.shown(statement),true);
    assert.equal(dom.findAll(dom.host,node=>node.tagName==='h2')[0].textContent,'LAN access');
  }
});

test('LAN access stays off until Apply, saves a port change while off, and reports the result',async context=>{
  const dom=connectionDom(context);let current=LAN_OFF;
  const ui=lanPanel(dom,(path,method,body)=>{
    if(method==='PUT')current=body.enabled?LAN_ON:{...LAN_OFF,port:body.port??4280};
    return current;
  });
  await flush();
  assert.equal(ui.state(),'LAN access is off');assert.equal(ui.button('Apply setting').disabled,true);
  assert.equal(ui.texts().includes('Before you turn on LAN access, make sure Windows lists this network as private. If Windows then asks about Node.js JavaScript Runtime, allow it on private networks only. EE’s service runs on Node.js. EE does not check the firewall, so if a device cannot open the address, check the firewall settings on this PC.'),true);
  const port=dom.findAll(dom.host,node=>node.name==='lan_port')[0];
  port.value='80';port.listeners.get('input')();
  assert.equal(ui.notice(),'Enter a whole-number port between 1024 and 65535.');assert.equal(ui.button('Apply setting').disabled,true);
  port.value='4281';port.listeners.get('input')();ui.button('Apply setting').click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access','PUT',{enabled:false,port:4281}]);assert.equal(ui.notice(),'LAN port saved.');
  ui.toggle().checked=true;ui.toggle().listeners.get('change')();assert.equal(ui.cleanup.hasChanges(),true);
  ui.button('Apply setting').click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access','PUT',{enabled:true}]);
  assert.equal(ui.notice(),'LAN access enabled.');assert.equal(ui.state(),'LAN access is on');
  assert.equal(ui.texts().includes('http://192.168.1.2:4280 (Ethernet)'),true);assert.equal(ui.shown(ui.button('Copy address')),true);
  assert.equal(ui.cleanup.hasChanges(),false);
});

test('a saved setting that did not open is retried, and an address-less PC says so',async context=>{
  const dom=connectionDom(context);
  const ui=lanPanel(dom,(_path,method)=>method==='PUT'?{...LAN_ON,addresses:[]}:{...LAN_OFF,enabled:true,can_change_port:false,error:{code:'lan_access_port_in_use',message:'x'}});
  await flush();
  assert.equal(ui.state(),'Another program uses this port. Choose another LAN port in Connection options.');
  ui.button('Retry LAN access').click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access','PUT',{enabled:true}]);
  assert.equal(ui.state(),'LAN access is on, but this PC has no private network address. Connect it to your network, then refresh.');
});

test('with administration access the LAN panel shows the code, its expiry and the paired devices',async context=>{
  context.mock.timers.enable({apis:['Date','setInterval'],now:Date.parse('2026-11-16T20:10:28.000Z')});
  const dom=connectionDom(context);
  const used={...KITCHEN,last_used_at:'2026-11-16T20:14:03.000Z',last_address:'10.0.0.1'},office={...KITCHEN,id:'0123456789abcdef',name:'Office PC',client:'app'};
  let current={...LAN_ON,devices:[used]};
  const ui=lanPanel(dom,(path,method)=>{
    if(path==='/api/lan-access/code')current={...current,pairing:{code:'482913',expires_at:'2026-11-16T20:15:00.000Z',attempts_left:5,ended:null}};
    if(method==='DELETE')current={...current,devices:current.devices.filter(device=>device.id!==KITCHEN.id)};
    return current;
  });
  await flush();
  const date=new Intl.DateTimeFormat('en-US',{month:'short',day:'numeric',year:'numeric'}).format(new Date(KITCHEN.paired_at));
  const time=new Intl.DateTimeFormat('en-US',{hour:'numeric',minute:'2-digit'}).format(new Date(used.last_used_at));
  assert.ok(ui.texts().includes('Kitchen iPad'));assert.ok(ui.texts().includes(`Browser · Paired ${date} from 10.0.0.1`));
  assert.ok(ui.texts().includes(`Last used from 10.0.0.1 at ${time}`));
  ui.button('Pair a device').click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access/code','POST']);
  assert.equal(dom.byClass(dom.host,'lan-pairing-code')[0].textContent,'482 913');
  assert.ok(ui.texts().includes('Open the address above on the other device and enter this code. It works once and expires in 4:32.'));
  assert.equal(ui.shown(ui.button('New code')),true);assert.equal(ui.shown(ui.button('Pair a device')),false);
  context.mock.timers.tick(1000);
  assert.ok(ui.texts().includes('Open the address above on the other device and enter this code. It works once and expires in 4:31.'));
  current={...current,devices:[used,office],pairing:{...current.pairing,code:null,ended:'used'}};
  const reads=ui.sent.length;context.mock.timers.tick(1000);await flush();
  assert.deepEqual(ui.sent.slice(reads),[['/api/lan-access']]);
  assert.equal(ui.notice(),'Paired Office PC.');assert.ok(ui.texts().includes('Desktop app · Paired '+date+' from 10.0.0.1'));
  context.mock.timers.tick(4000);await flush();assert.equal(ui.sent.length,reads+1);
  dom.findAll(dom.host,node=>node.tagName==='button'&&node.textContent==='Remove')[0].click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access/devices/3f9c0a1b2c3d4e5f','DELETE']);
  assert.equal(ui.notice(),'Removed Kitchen iPad. It needs a new pairing code to connect again.');
  assert.equal(ui.texts().includes('Kitchen iPad'),false);
});

test('each ended pairing code shows its reason',async context=>{
  for(const [ended,text] of [['expired','The pairing code expired. Choose Pair a device to show a new one.'],
    ['attempts','The pairing code ended after 5 wrong attempts. If no one at your devices entered them, another device on your network may be trying to pair. Choose Pair a device to show a new one.'],
    ['failed','The device could not be saved. Check the local data folder, then choose Pair a device to show a new code.'],['used','The pairing code was used.']]){
    const dom=connectionDom(context),ui=lanPanel(dom,()=>({...LAN_ON,pairing:{code:null,expires_at:'2026-11-16T20:15:00.000Z',attempts_left:ended==='attempts'?0:5,ended}}));
    await flush();assert.equal(ui.notice(),text,ended);
  }
  const full=connectionDom(context),ui=lanPanel(full,()=>({...LAN_ON,devices:Array.from({length:16},(_,index)=>({...KITCHEN,id:index.toString(16).padStart(16,'0')}))}));
  await flush();
  assert.ok(ui.texts().includes('16 devices are paired, the most EE allows. Remove one before pairing another.'));assert.equal(ui.button('Pair a device').disabled,true);
});

test('turning LAN access off with devices paired asks first',async context=>{
  const dom=connectionDom(context);
  const ui=lanPanel(dom,(_path,method)=>method==='PUT'?LAN_OFF:{...LAN_ON,devices:[KITCHEN]});
  await flush();
  ui.toggle().checked=false;ui.toggle().listeners.get('change')();
  ui.button('Apply setting').click();await flush();
  assert.equal(dom.findAll(dom.doc.body,node=>node.tagName==='h2'&&node.textContent==='Turn off LAN access?').length,1);
  assert.ok(dom.findAll(dom.doc.body,node=>node.textContent==='All paired devices are removed and disconnected. Each one needs a new pairing code after you turn LAN access on again.').length);
  dom.button('Cancel').click();await flush();
  assert.equal(ui.sent.some(call=>call[1]==='PUT'),false);assert.equal(ui.cleanup.hasChanges(),true);
  ui.button('Apply setting').click();await flush();dom.button('Turn off').click();await flush();
  assert.deepEqual(ui.sent.at(-1),['/api/lan-access','PUT',{enabled:false}]);assert.equal(ui.notice(),'LAN access disabled.');
});

test('LAN access is read-only without administration access and on remote pages',async context=>{
  const dom=connectionDom(context),ui=lanPanel(dom,()=>({...LAN_ON,pairing:null,devices:null,can_configure:false,admin_required:true}));
  await flush();
  assert.ok(ui.texts().includes(LAN_ADMIN));assert.equal(ui.toggle().disabled,true);assert.equal(ui.button('Apply setting').disabled,true);
  assert.equal(ui.shown(ui.button('Pair a device')),false);assert.equal(ui.texts().includes('Paired devices'),false);
  ui.button('Refresh LAN access').click();await flush();assert.deepEqual(ui.sent,[['/api/lan-access'],['/api/lan-access']]);
  assert.equal(lanAccessError({code:'admin_required'}),LAN_ADMIN);
  for(const [lan,lines] of [[true,[LAN_PAGE_NOTE,'Change this setting in EE on the host computer.']],[false,['Change this setting in EE on the host computer.']]]){
    const remote=connectionDom(context),page=lanPanel(remote,()=>assert.fail('A remote page reads no LAN access status.'),{remote:true,lan});
    await flush();
    assert.deepEqual(page.texts(),lines);assert.deepEqual(page.sent,[]);assert.equal(page.cleanup.hasChanges(),false);
  }
  assert.equal(LAN_PAGE_NOTE,'This device uses LAN access. Its connection to the computer running EE is not encrypted.');
});

test('the pairing screen sends the code and name, shows the warning, and reports a refusal',async context=>{
  const dom=connectionDom(context),sent=[];let paired=0,refuse=true;
  renderLanPairing(dom.host,{request:async(...args)=>{
    sent.push(args);
    if(refuse)throw Object.assign(new Error('x'),{code:'lan_pairing_failed'});
    return {paired:true};
  },onPaired:()=>{paired++;}});
  const form=dom.findAll(dom.host,node=>node.tagName==='form')[0],field=name=>dom.findAll(form,node=>node.name===name)[0];
  const texts=()=>dom.findAll(dom.host,node=>node.tagName==='p').map(node=>node.textContent);
  assert.equal(dom.findAll(dom.host,node=>node.tagName==='h1')[0].textContent,'Pair this device');
  assert.ok(texts().includes('Enter the pairing code shown on the computer running EE. On that computer, open Settings, go to LAN access and choose Pair a device.'));
  assert.ok(texts().includes('EE on another device never asks for switch or UniFi passwords. If it does, do not enter them.'));
  assert.ok(texts().includes('This connection uses plain HTTP and is not encrypted. Anyone who can watch or change traffic on your network can read workspace data and this device’s access key, and can use the key to control EE. EE does not send switch or UniFi passwords over it.'));
  assert.deepEqual([field('pairing_code').inputMode,field('pairing_code').autocomplete,field('pairing_code').maxLength],['numeric','one-time-code',7]);
  assert.equal(field('device_name').placeholder,'For example, Kitchen iPad');
  const submit=()=>form.listeners.get('submit')({preventDefault(){}});
  field('pairing_code').value='48291';await submit();
  assert.deepEqual(sent,[]);assert.equal(dom.byClass(dom.host,'connection-notice')[0].textContent,'Enter the 6-digit code.');
  field('pairing_code').value='482 913';field('device_name').value='  ';await submit();
  assert.equal(dom.byClass(dom.host,'connection-notice')[0].textContent,'Enter a name for this device, up to 40 characters.');
  field('device_name').value=' Kitchen iPad ';await submit();
  assert.deepEqual(sent,[['/api/lan-access/pair','POST',{code:'482913',name:'Kitchen iPad',client:'browser'}]]);
  assert.equal(dom.byClass(dom.host,'connection-notice')[0].textContent,'The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one.');
  assert.equal(paired,0);
  refuse=false;field('pairing_code').value='482-913';await submit();
  assert.equal(paired,1);assert.equal(dom.byClass(dom.host,'connection-notice')[0].textContent,'Paired. Opening the workspace…');
  const revoked=connectionDom(context);renderLanPairing(revoked.host,{request:async()=>({}),revoked:true,onPaired(){}});
  assert.ok(revoked.findAll(revoked.host,node=>node.tagName==='p').some(node=>node.textContent==='This device is not paired. Enter a new pairing code from the computer running EE to continue.'));
});
