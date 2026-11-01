import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {renderSettings,renderHelp} from '../preview/secondary.mjs';
import {connectionError,handoffComplete,handoffStatus,renderSwitchConnection,showSceneChoice,requestHandoff} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve,reject;return {promise:new Promise((yes,no)=>{resolve=yes;reject=no;}),resolve:value=>resolve(value),reject:error=>reject(error)};};
const connected=()=>({logged_in:true,connected:true,connecting:false,simulated:false,has_saved_login:true,workspace_id:'fixture',handed_off:false,handoff_state:'none',automatic_connect_paused:false});
const complete=()=>({...connected(),logged_in:false,connected:false,workspace_id:null,handed_off:true,handoff_state:'complete',automatic_connect_paused:true});
const privateStatus={available:false,enabled:false,ready:false,url:null,can_configure:true,error:{code:'tailscale_unavailable'}};

function uiDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement;
  const matches=(element,selector)=>selector.split(',').some(part=>{
    const match=part.trim().match(/^([a-z]+)?(?:\[([\w-]+)(?:="([^"]*)")?\])?$/);if(!match)return false;
    const [,tag,key,value]=match,actual=key?.startsWith('data-')?element.dataset[key.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]:element[key]??element.getAttribute(key);
    return (!tag||element.tagName===tag)&&(!key||value===undefined?(!key||actual!=null):String(actual)===value);
  });
  const adapt=element=>{
    element.removeAttribute=key=>element.attributes.delete(key);
    element.toggleAttribute=(name,present)=>present?element.setAttribute(name,''):element.removeAttribute(name);
    element.querySelectorAll=selector=>dom.findAll(element,node=>node!==element&&matches(node,selector));
    element.querySelector=selector=>selector===':scope > button'?element.children.find(child=>child.tagName==='button'):element.querySelectorAll(selector)[0]||null;
    element.matches=selector=>matches(element,selector);
    element.closest=selector=>{let node=element;while(node){if(matches(node,selector))return node;node=node.parentNode;}return null;};
    element.reportValidity=()=>true;element.showModal=()=>{element.open=true;};element.close=()=>{element.open=false;};
    element.remove=()=>{if(element.parentNode){const parent=element.parentNode;parent.children.splice(parent.children.indexOf(element),1);element.parentNode=null;}};
    Object.defineProperty(element,'firstElementChild',{get:()=>element.children[0]});
    if(element.tagName==='form')Object.defineProperty(element,'elements',{get:()=>element.querySelectorAll('input,select,button')});
    return element;
  };
  dom.doc.createElement=tag=>adapt(create(tag));adapt(dom.host);
  const oldOption=Object.getOwnPropertyDescriptor(globalThis,'Option');
  Object.defineProperty(globalThis,'Option',{configurable:true,value:function(text,value){const option=dom.doc.createElement('option');option.textContent=text;option.value=value;return option;}});
  context.after(()=>oldOption?Object.defineProperty(globalThis,'Option',oldOption):delete globalThis.Option);
  return {...dom,button:(text,root=dom.doc.body)=>dom.findAll(root,node=>node.tagName==='button'&&node.textContent===text)[0],
    dialog:()=>dom.findAll(dom.doc.body,node=>node.tagName==='dialog')[0]};
}

async function settings(context,{send=async()=>complete(),reconcile=complete()}={}){
  const dom=uiDom(context),calls=[],sessions=[];let sessionReads=0;
  const request=async(path,method,body)=>{
    calls.push({path,method,body});
    if(path==='/api/session/handoff')return send();
    if(path==='/api/session')return ++sessionReads===1?connected():reconcile;
    if(path==='/api/state')return {num_ports:8,ports:[],sfp:[],mode:'running',stats:{}};
    if(path==='/api/private-access')return privateStatus;
    if(path==='/api/controller')return {connected:true,configured:true,preferences_supported:true,auto_reconnect:true,saved_login_available:true};
    throw new Error(`Unexpected request ${path}`);
  };
  const cleanup=await renderSettings(dom.host,{request,onSessionChange:async(...args)=>sessions.push(args)});
  dom.after(cleanup);await flush();return {...dom,calls,sessions,cleanup};
}

test('handoff requires a scoped confirmation and Cancel sends no command',async context=>{
  const ui=await settings(context);assert.ok(ui.button('Handoff to UniFi'));assert.equal(ui.calls.some(call=>call.method),false);
  assert.ok(ui.button('Refresh connection'));
  assert.equal(ui.button('Handoff to UniFi').className.split(/\s+/).includes('quiet'),false);
  ui.button('Handoff to UniFi').click();await flush();const dialog=ui.dialog();assert.ok(dialog);
  assert.equal(ui.doc.activeElement,ui.button('Cancel',dialog));
  const copy=ui.findAll(dialog,node=>node.tagName==='p')[0].textContent;
  assert.match(copy,/switch helpers/);assert.match(copy,/Saved logins, profiles/);assert.match(copy,/wait until you connect again/);
  assert.equal(ui.calls.some(call=>call.method),false);ui.button('Cancel',dialog).click();await flush();
  assert.equal(ui.dialog(),undefined);assert.equal(ui.calls.some(call=>call.method),false);assert.deepEqual(ui.sessions,[]);
});

test('confirmed handoff sends one scoped request, waits for cleanup and preserves saved-data controls',async context=>{
  const response=deferred(),ui=await settings(context,{send:()=>response.promise});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  assert.equal(ui.host.inert,true);assert.ok(ui.button('Handing off…'));
  ui.button('Handing off…').click();await flush();assert.equal(ui.calls.filter(call=>call.method).length,1);
  assert.deepEqual(ui.calls.filter(call=>call.method),[{path:'/api/session/handoff',method:'POST',body:undefined}]);assert.deepEqual(ui.sessions,[]);
  let left=false;const leaving=ui.cleanup.canLeave().then(()=>{left=true;});await flush();assert.equal(left,false);
  const result=complete();response.resolve(result);await leaving;await flush();assert.equal(left,true);
  assert.equal(ui.sessions[0][0],result);assert.equal(ui.sessions[0][0].has_saved_login,true);
  assert.ok(ui.button('Forget saved switch login'));assert.equal(ui.calls.some(call=>/forget|controller.*POST|presets/.test(call.path)),false);
});

test('an unconfirmed cleanup keeps a truthful error and never automatically retries or reconnects',async context=>{
  const failure=Object.assign(new Error('private-host secret'),{code:'handoff_unconfirmed'});
  const result={...complete(),handed_off:false,handoff_state:'unconfirmed',error:{code:'handoff_unconfirmed'}};
  const ui=await settings(context,{send:async()=>{throw failure;},reconcile:result});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  assert.deepEqual(ui.calls.filter(call=>call.method).map(call=>call.path),['/api/session/handoff']);
  assert.equal(ui.calls.filter(call=>call.path==='/api/session').length,2);
  assert.equal(ui.sessions[0][0],result);assert.equal(ui.sessions[0][1].error,true);
  assert.match(ui.sessions[0][1].message,/could not be confirmed/);assert.doesNotMatch(ui.sessions[0][1].message,/private-host|secret|returned to UniFi/);
});

test('a lost handoff reply succeeds only after reading confirmed completion',async context=>{
  const result=complete(),ui=await settings(context,{send:async()=>{throw new Error('Network failed');},reconcile:result});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  assert.equal(ui.sessions[0][0],result);assert.equal(ui.sessions[0][1],undefined);
  assert.equal(ui.calls.filter(call=>call.method).length,1);
});

test('a generic successful response cannot claim that cleanup completed',async context=>{
  const result={...complete(),handed_off:false,handoff_state:'pending'};
  const ui=await settings(context,{send:async()=>result,reconcile:result});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  assert.equal(handoffComplete(result),false);assert.equal(ui.sessions[0][1].error,true);
  assert.match(ui.sessions[0][1].message,/could not be confirmed/);
});

test('disposing Settings cancels confirmation and ignores a late handoff response',async context=>{
  const first=await settings(context);first.button('Handoff to UniFi').click();await flush();first.cleanup();await flush();
  assert.equal(first.dialog(),undefined);assert.equal(first.calls.some(call=>call.method),false);
  const response=deferred(),second=await settings(context,{send:()=>response.promise});
  second.button('Handoff to UniFi').click();await flush();second.button('Handoff to UniFi',second.dialog()).click();await flush();
  second.cleanup();response.resolve(complete());await flush();assert.deepEqual(second.sessions,[]);
});

test('handoff status distinguishes a verified return from pending, failed and paused sessions',()=>{
  assert.equal(handoffComplete(complete()),true);assert.match(handoffStatus(complete()).message,/returned to UniFi/);
  for(const change of [{logged_in:true},{connected:true},{connecting:true},{handed_off:false},{handoff_state:'pending'},{automatic_connect_paused:false}])assert.equal(handoffComplete({...complete(),...change}),false);
  assert.equal(handoffStatus({...complete(),handed_off:false,handoff_state:'pending'}).tone,'waiting');
  assert.equal(handoffStatus({...complete(),handed_off:false,handoff_state:'unconfirmed'}).error,true);
  assert.doesNotMatch(handoffStatus({...complete(),handed_off:false,handoff_state:'none'}).message,/returned/);
  assert.equal(handoffStatus(connected()),null);
  assert.doesNotMatch(connectionError({code:'handoff_save_failed',message:'private secret'}),/private|secret/);
});

test('a refreshed handoff login keeps saved credentials available without restoring automatically',async context=>{
  const ui=uiDom(context),calls=[],restores=[],enters=[];
  const cleanup=renderSwitchConnection(ui.host,{request:async(...args)=>{calls.push(args);return privateStatus;},session:complete(),
    restoreSaved:async()=>{restores.push(true);return connected();},onConnected:async value=>enters.push(value)});ui.after(cleanup);await flush();
  assert.deepEqual(restores,[]);assert.equal(ui.button('Connect with saved login').hidden,false);
  assert.match(ui.byClass(ui.host,'connection-notice')[0].textContent,/returned to UniFi/);
  ui.button('Connect with saved login').click();await flush();assert.equal(restores.length,1);assert.equal(enters.length,1);
  assert.deepEqual(calls.map(call=>call[0]),['/api/private-access']);
});

test('pending handoff blocks manual, saved and demo reconnect attempts',async context=>{
  const ui=uiDom(context),actions=[],session={...complete(),handed_off:false,handoff_state:'pending'};
  const cleanup=renderSwitchConnection(ui.host,{request:async()=>privateStatus,session,restoreSaved:async()=>actions.push('restore'),onDemo:()=>actions.push('demo')});ui.after(cleanup);await flush();
  const saved=ui.button('Connect with saved login');assert.equal(saved.disabled,true);saved.click();ui.button('Continue with demo').click();await flush();
  assert.deepEqual(actions,[]);assert.equal(ui.button('Handing off…').disabled,true);
});

test('unconfirmed handoff offers explicit cleanup retry while keeping all reconnect actions blocked',async context=>{
  const ui=uiDom(context),calls=[],actions=[],result=deferred(),session={...complete(),handed_off:false,handoff_state:'unconfirmed'};
  const cleanup=renderSwitchConnection(ui.host,{request:async(...args)=>{if(args[0]==='/api/private-access')return privateStatus;calls.push(args);return result.promise;},session,
    restoreSaved:async()=>actions.push('restore'),onDemo:()=>actions.push('demo'),onSessionChange:async value=>actions.push(value)});ui.after(cleanup);await flush();
  const retry=ui.button('Retry handoff');assert.equal(retry.hidden,false);assert.equal(retry.disabled,false);
  assert.equal(ui.button('Connect with saved login').disabled,true);ui.button('Connect with saved login').click();ui.button('Continue with demo').click();await flush();assert.deepEqual(actions,[]);
  retry.click();await flush();assert.ok(ui.dialog());assert.equal(calls.length,0);ui.button('Cancel',ui.dialog()).click();await flush();assert.equal(calls.length,0);
  retry.click();await flush();ui.button('Retry handoff',ui.dialog()).click();await flush();retry.click();await flush();
  assert.deepEqual(calls,[['/api/session/handoff','POST']]);assert.equal(retry.disabled,true);
  const confirmed=complete();result.resolve(confirmed);await flush();assert.deepEqual(actions,[confirmed]);
  assert.equal(retry.hidden,true);assert.equal(ui.button('Connect with saved login').disabled,false);
  assert.equal(ui.doc.activeElement,ui.button('Connect with saved login'));assert.match(ui.byClass(ui.host,'connection-notice')[0].textContent,/returned to UniFi/);
});

test('a failed explicit retry stays recoverable without silently reconnecting',async context=>{
  const ui=uiDom(context),calls=[],session={...complete(),handed_off:false,handoff_state:'unconfirmed',error:{code:'handoff_unconfirmed'}};
  const cleanup=renderSwitchConnection(ui.host,{request:async(path,method)=>{if(path==='/api/private-access')return privateStatus;calls.push([path,method]);if(method)throw Object.assign(new Error('secret diagnostic'),{code:'handoff_unconfirmed'});return session;},session,restoreSaved:async()=>assert.fail('No automatic reconnect')});ui.after(cleanup);await flush();
  ui.button('Retry handoff').click();await flush();ui.button('Retry handoff',ui.dialog()).click();await flush();
  assert.deepEqual(calls,[['/api/session/handoff','POST'],['/api/session',undefined]]);
  assert.equal(ui.button('Retry handoff').disabled,false);assert.equal(ui.button('Connect with saved login').disabled,true);
  assert.match(ui.byClass(ui.host,'connection-notice')[0].textContent,/Retry handoff/);assert.doesNotMatch(ui.byClass(ui.host,'connection-notice')[0].textContent,/secret|returned/);
});

test('historic cleanup uncertainty blocks reconnect and explains review without offering an impossible retry',async context=>{
  const ui=uiDom(context),actions=[],session={...complete(),handed_off:false,handoff_state:'unconfirmed',error:{code:'handoff_recovery_required',message:'private diagnostic'}};
  const cleanup=renderSwitchConnection(ui.host,{request:async(path)=>{assert.equal(path,'/api/private-access');return privateStatus;},session,
    restoreSaved:async()=>actions.push('restore'),onDemo:()=>actions.push('demo')});ui.after(cleanup);await flush();
  const retry=ui.button('Retry handoff');assert.equal(retry.hidden,true);assert.equal(retry.disabled,true);retry.click();
  ui.button('Connect with saved login').click();ui.button('Continue with demo').click();await flush();assert.deepEqual(actions,[]);assert.equal(ui.dialog(),undefined);
  assert.equal(ui.button('Connect with saved login').disabled,true);
  const status=ui.byClass(ui.host,'connection-notice')[0].textContent;assert.match(status,/recovery review/);assert.doesNotMatch(status,/Retry|private|diagnostic/);
  assert.equal(handoffComplete({...complete(),error:session.error}),false);
});

test('historic uncertainty discovered by reconciliation overrides a generic network failure',async()=>{
  const session={...complete(),handed_off:false,handoff_state:'unconfirmed',error:{code:'handoff_recovery_required'}};
  const result=await requestHandoff(async(_path,method)=>{if(method)throw new Error('Network failed');return session;});
  assert.equal(result.session,session);assert.match(result.error,/recovery review/);assert.doesNotMatch(result.error,/Retry/);
});

test('retained scene-choice cleanup exposes Retry handoff instead of opening a lighting chooser',async context=>{
  const ui=uiDom(context),session={...complete(),handed_off:false,handoff_state:'unconfirmed',pending_scene_choice:{connection_id:'candidate',legacy_revision:'legacy',legacy_available:true}};
  const send=async(path)=>{assert.equal(path,'/api/private-access');return privateStatus;};
  assert.equal(await showSceneChoice(send,session),session);assert.equal(ui.dialog(),undefined);
  const cleanup=renderSwitchConnection(ui.host,{request:send,session,restoreSaved:async()=>assert.fail('No reconnect')});ui.after(cleanup);await flush();
  assert.equal(ui.dialog(),undefined);assert.equal(ui.button('Retry handoff').disabled,false);assert.equal(ui.button('Retry handoff').hidden,false);
  assert.match(ui.byClass(ui.host,'connection-notice')[0].textContent,/Retry handoff/);
});

test('a handoff update closes an existing scene chooser without sending a scene choice',async context=>{
  const ui=uiDom(context),sent=[],candidate={logged_in:false,pending_scene_choice:{connection_id:'candidate',legacy_revision:'legacy',legacy_available:true}};
  const promise=showSceneChoice(async(...args)=>sent.push(args),candidate);assert.ok(ui.dialog());
  const pending={...candidate,handed_off:false,handoff_state:'pending',automatic_connect_paused:true};
  assert.equal(await showSceneChoice(async(...args)=>sent.push(args),pending),pending);assert.equal(await promise,pending);
  assert.equal(ui.dialog(),undefined);assert.deepEqual(sent,[]);
});

test('Help explains the same explicit handoff and saved-login recovery actions',context=>{
  const ui=uiDom(context);renderHelp(ui.host);
  const paragraphs=ui.findAll(ui.host,node=>node.tagName==='p').map(node=>node.textContent).join('\n');
  assert.match(paragraphs,/Handoff to UniFi in Settings/);assert.match(paragraphs,/saved logins, profiles/);
  assert.match(paragraphs,/Connect with saved login/);assert.match(paragraphs,/Retry handoff/);
});

test('the app binds intentional saved-login resumption without invoking it on handoff rendering',async()=>{
  const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
  const code=source.slice(source.indexOf('function showConnection()'),source.indexOf('function showLoginHelp()'));
  const sent=[],context={workspaceReady:false,secondaryCleanup:null,view:{},session:complete(),workspaceOptions:{live:true},
    renderSwitchConnection:(_host,options)=>{context.options=options;return ()=>{};},request:async(...args)=>sent.push(args),acceptSession(){},showLoginHelp(){},updateConnection(){},status(){},handoffStatus};
  vm.createContext(context);vm.runInContext(code,context);context.showConnection();assert.deepEqual(sent,[]);
  await context.options.restoreSaved();assert.deepEqual(sent,[['/api/session/restore?resume=true','POST']]);
});
