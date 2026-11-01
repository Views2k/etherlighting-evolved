import test from 'node:test';
import assert from 'node:assert/strict';
import {connectionError, controllerError, controllerReconnectControls, controllerReconnectConfirmed, showSceneChoice, renderSwitchConnection} from '../preview/connections.mjs';
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
  assert.match(dom.byClass(dom.dialog(),'connection-notice')[0].textContent,/Keep output stopped/);
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
