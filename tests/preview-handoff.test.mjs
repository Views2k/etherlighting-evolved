import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {renderSettings,renderHelp} from '../preview/secondary.mjs';
import {connectionError,handoffComplete,handoffConfirmation,handoffStatus,renderSwitchConnection,showSceneChoice,requestHandoff} from '../preview/connections.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';
import {DEFAULT_THEME} from '../preview/theme-catalog.mjs';
import {setTheme} from '../preview/theme.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve,reject;return {promise:new Promise((yes,no)=>{resolve=yes;reject=no;}),resolve:value=>resolve(value),reject:error=>reject(error)};};
const connected=()=>({logged_in:true,connected:true,connecting:false,simulated:false,has_saved_login:true,workspace_id:'fixture',handed_off:false,handoff_state:'none',automatic_connect_paused:false});
const complete=()=>({...connected(),logged_in:false,connected:false,workspace_id:null,handed_off:true,handoff_state:'complete',automatic_connect_paused:true});
const privateStatus={available:false,enabled:false,ready:false,url:null,can_configure:true,error:{code:'tailscale_unavailable'}};

const browserEvents=new EventTarget();
let browserRoot;

function uiDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement;
  const matches=(element,selector)=>selector.split(',').some(part=>{
    const match=part.trim().match(/^([a-z]+)?(?:\[([\w-]+)(?:="([^"]*)")?\])?$/);if(!match)return false;
    const [,tag,key,value]=match,actual=key?.startsWith('data-')?element.dataset[key.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]:element[key]??element.getAttribute(key);
    return (!tag||element.tagName===tag)&&(!key||value===undefined?(!key||actual!=null):String(actual)===value);
  });
  const adapt=element=>{
    element.removeEventListener=(type,listener)=>{if(element.listeners.get(type)===listener)element.listeners.delete(type);};
    element.getBoundingClientRect=()=>element.bounds??{top:0,bottom:300,height:300,left:0,right:600,width:600};
    element.clientHeight=300;element.scrollHeight=300;
    element.scrollTo=({top})=>{element.scrollTop=top;element.listeners.get('scroll')?.();};
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
  // Theme state belongs to one browser realm; each fixture replaces its page content.
  browserRoot ??= dom.doc.createElement('html');
  dom.doc.documentElement=browserRoot;
  dom.doc.querySelector=selector=>dom.findAll(dom.doc.body,node=>matches(node,selector))[0]||null;
  const values=new Map(),globals={
    Option:function(text,value){const option=dom.doc.createElement('option');option.textContent=text;option.value=value;return option;},
    addEventListener:browserEvents.addEventListener.bind(browserEvents),
    removeEventListener:browserEvents.removeEventListener.bind(browserEvents),
    localStorage:{getItem:key=>values.get(key)??null,setItem:(key,value)=>values.set(key,String(value))}
  },original=new Map();
  for(const [key,value] of Object.entries(globals)){
    original.set(key,Object.getOwnPropertyDescriptor(globalThis,key));
    Object.defineProperty(globalThis,key,{configurable:true,writable:true,value});
  }
  dom.after(()=>{for(const [key,descriptor] of original)descriptor?Object.defineProperty(globalThis,key,descriptor):delete globalThis[key];});
  setTheme(DEFAULT_THEME);
  return {...dom,button:(text,root=dom.doc.body)=>dom.findAll(root,node=>node.tagName==='button'&&node.textContent===text)[0],
    dialog:()=>dom.findAll(dom.doc.body,node=>node.tagName==='dialog')[0]};
}

const aboutInfo={name:'Etherlighting Evolved',version:'0.5.1',disclaimer:'Unofficial community project. Not affiliated with, endorsed by or supported by Ubiquiti Inc. It drives undocumented interfaces on the switch over SSH; use at your own risk.'};

async function settings(context,{send=async()=>complete(),reconcile=complete(),about=async()=>aboutInfo,first=connected(),remote=false,
  capabilities=privateStatus,onCloseWorkspace,state={num_ports:8,ports:[],sfp:[],mode:'running',stats:{}}}={}){
  const dom=uiDom(context),calls=[],sessions=[];let sessionReads=0;
  const request=async(path,method,body)=>{
    calls.push({path,method,body});
    if(path==='/api/about')return about();
    if(path==='/api/session/handoff')return send();
    if(path==='/api/session')return ++sessionReads===1?first:reconcile;
    if(path==='/api/state')return state;
    if(path==='/api/private-access')return capabilities;
    if(path==='/api/controller')return {connected:true,configured:true,preferences_supported:true,auto_reconnect:true,saved_login_available:true};
    throw new Error(`Unexpected request ${path}`);
  };
  const cleanup=await renderSettings(dom.host,{request,onSessionChange:async(...args)=>sessions.push(args),remote,onCloseWorkspace});
  dom.after(cleanup);await flush();return {...dom,calls,sessions,cleanup};
}

const NO_HOST_LOGIN=' The computer running EE has no saved switch login that can be opened, so you will need that computer to connect again.';
async function handoffMessage(ui){
  ui.button('Handoff to UniFi').click();await flush();
  const dialog=ui.dialog(),message=ui.findAll(dialog,node=>node.tagName==='p')[0].textContent;
  ui.button('Cancel',dialog).click();await flush();return message;
}

test('Close web session needs administration access, and a tab without it says where to close the session',async context=>{
  const CLOSE_HELP='To close the web session, choose Open in browser in the EE tray menu, or quit EE. If EE runs from source, use the administration link it printed when it started.';
  const shown=ui=>ui.findAll(ui.host,node=>node.tagName==='p'&&node.textContent===CLOSE_HELP).length;
  for(const [capabilities,button,help] of [[{...privateStatus,can_close_workspace:true},true,0],
    [{...privateStatus,can_close_workspace:false,admin_required:true},false,1],[{...privateStatus,can_close_workspace:false},false,0]]){
    const ui=await settings(context,{capabilities,onCloseWorkspace:async()=>{}});
    assert.equal(Boolean(ui.button('Close web session')),button,JSON.stringify(capabilities));
    assert.equal(shown(ui),help,JSON.stringify(capabilities));
  }
});

test('remote Settings leaves out saved-login removal and UniFi Network setup',async context=>{
  const ui=await settings(context,{remote:true}),nav=ui.byClass(ui.host,'settings-nav')[0];
  assert.equal(ui.button('Forget saved switch login'),undefined);
  assert.equal(ui.button('UniFi',nav).hidden,true);assert.equal(ui.byClass(ui.host,'controller-settings')[0].hidden,true);
  assert.equal(ui.calls.some(call=>call.path.startsWith('/api/controller')),false);
  assert.ok(ui.button('Handoff to UniFi'));
  assert.equal((await handoffMessage(ui)).endsWith(NO_HOST_LOGIN),false);
});

test('a remote handoff warns when the host has no saved login that opens',async context=>{
  for(const first of [{...connected(),has_saved_login:false},{...connected(),saved_login_error:'x'}]){
    const ui=await settings(context,{remote:true,first});
    assert.equal((await handoffMessage(ui)).endsWith(NO_HOST_LOGIN),true);
    assert.equal(ui.calls.some(call=>call.method),false);
  }
});

test('local Settings keeps saved-login removal, UniFi Network setup and the plain handoff message',async context=>{
  const ui=await settings(context,{first:{...connected(),has_saved_login:true,saved_login_error:'x'}});
  assert.ok(ui.button('Forget saved switch login'));
  assert.equal(ui.byClass(ui.host,'controller-settings')[0].hidden,false);
  assert.equal((await handoffMessage(ui)).endsWith(NO_HOST_LOGIN),false);
});

test('Settings shows the connected switch SSH key fingerprint as text that can be selected',async context=>{
  const fingerprint='SHA256:'+'A'.repeat(43);
  // The UniFi Network panel has its own, empty certificate fingerprint.
  const codes=ui=>ui.findAll(ui.host,node=>node.tagName==='code'&&node.textContent).map(node=>[node.className,node.textContent]);
  const labels=ui=>ui.findAll(ui.host,node=>node.tagName==='p'&&node.textContent?.startsWith('SSH key fingerprint')).map(node=>node.textContent);
  const ui=await settings(context,{first:{...connected(),ssh_host_key:fingerprint,ssh_host_key_type:'ssh-ed25519'}});
  assert.deepEqual(codes(ui),[['certificate-fingerprint',fingerprint]]);assert.deepEqual(labels(ui),['SSH key fingerprint (ssh-ed25519)']);
  for(const first of [connected(),{...connected(),ssh_host_key:'SHA256:short'},{...connected(),simulated:true,ssh_host_key:fingerprint}]){
    const other=await settings(context,{first});
    assert.deepEqual(codes(other),[]);assert.deepEqual(labels(other),[]);
  }
});

test('canceling a remote scene choice points to the login saved on the host',async context=>{
  for(const [hasSaved,text] of [[false,'No switch login is saved on the computer running EE. Sign in to the switch on that computer. This page opens the workspace when the switch connects.'],
    [true,'Lighting setup canceled. Choose Connect with saved login to try again.']]){
    const ui=uiDom(context),choice={connection_id:'candidate',legacy_revision:'legacy',legacy_available:true};
    const request=async path=>path==='/api/private-access'?privateStatus:{logged_in:false,pending_scene_choice:null,has_saved_login:hasSaved};
    const cleanup=renderSwitchConnection(ui.host,{request,session:{logged_in:false,has_saved_login:true,pending_scene_choice:choice},remote:true,
      restoreSaved:async()=>assert.fail('No reconnect'),onConnected:async()=>assert.fail('No workspace')});ui.after(cleanup);await flush();
    ui.button('Cancel',ui.dialog()).click();await flush();
    assert.equal(ui.byClass(ui.host,'connection-notice')[0].textContent,text);
  }
});

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

test('handoff confirmation names the fans only on a switch with fan control',async context=>{
  const copy=async options=>{
    const ui=await settings(context,options);ui.button('Handoff to UniFi').click();await flush();
    const text=ui.findAll(ui.dialog(),node=>node.tagName==='p')[0].textContent;ui.button('Cancel',ui.dialog()).click();await flush();return text;
  };
  const fans=await copy({state:{num_ports:8,ports:[],sfp:[],mode:'running',stats:{},fans:{supported:true}}});
  assert.match(fans,/return the fans to the switch’s own controller/);assert.match(fans,/your fan mode are kept/);
  assert.match(fans,/switch helpers/);assert.match(fans,/Saved logins, profiles/);assert.match(fans,/wait until you connect again/);
  assert.doesNotMatch(await copy(),/fan/);
  assert.ok(handoffConfirmation(true,true).endsWith(' Unapplied connection changes will be discarded.'));
});

test('handoffStatus adds the fan result to a completed handoff',()=>{
  const lighting=handoffStatus(complete()).message;
  const returned=handoffStatus({...complete(),fan_handoff:'returned'});
  assert.match(returned.message,/runs the fans again/);assert.equal(returned.error,false);assert.equal(returned.tone,'online');
  assert.ok(returned.message.startsWith(`${lighting} `));
  const unconfirmed=handoffStatus({...complete(),fan_handoff:'unconfirmed'});
  assert.equal(unconfirmed.error,true);assert.equal(unconfirmed.tone,'');assert.match(unconfirmed.message,/choose Handoff to UniFi to retry/);
  for(const value of [null,undefined,'not_running']){
    assert.deepEqual(handoffStatus({...complete(),fan_handoff:value}),{message:'Lighting returned to UniFi. Your saved logins and profiles are kept. Connect when you want EE to control lighting again.',error:false,tone:'online'});
  }
});

// The page's own status line, not the notices of its controller and private access sections.
const settingsNotice=ui=>ui.findAll(ui.host,node=>node.tagName==='p'&&node.className.split(' ')[0]==='notice'&&!node.className.includes('connection-notice'))[0];

test('Settings shows unconfirmed fans after a completed handoff as an error',async context=>{
  const ui=await settings(context,{send:async()=>({...complete(),fan_handoff:'unconfirmed'})});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  const notice=settingsNotice(ui);
  assert.ok(notice.textContent.startsWith('Lighting returned to UniFi. EE could not confirm'));assert.ok(notice.className.split(' ').includes('error'));
});

test('Settings names returned fans after a completed handoff',async context=>{
  const ui=await settings(context,{send:async()=>({...complete(),fan_handoff:'returned'})});
  ui.button('Handoff to UniFi').click();await flush();ui.button('Handoff to UniFi',ui.dialog()).click();await flush();
  const notice=settingsNotice(ui);
  assert.equal(notice.textContent,'Lighting returned to UniFi. The switch’s own controller runs the fans again.');
  assert.equal(notice.className.split(' ').includes('error'),false);
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

for(const playback of ['last_confirmed','playing','unreachable']){
  test(`saved switch lighting leaves the ordinary workspace connection available (${playback})`,async context=>{
    const ui=uiDom(context),calls=[],enters=[];
    const session={...complete(),handed_off:false,automatic_connect_paused:false,handoff_state:'none',switch_playback:playback};
    const cleanup=renderSwitchConnection(ui.host,{session,request:async(path)=>{calls.push(path);return privateStatus;},
      restoreSaved:async()=>({...connected(),switch_playback:'playing'}),onConnected:async value=>enters.push(value)});
    ui.after(cleanup);await flush();
    const restore=ui.button('Connect with saved login');
    assert.equal(restore.hidden,false);assert.equal(restore.disabled,false);
    restore.click();await flush();
    assert.equal(enters.length,1);assert.deepEqual(calls,['/api/private-access']);
    assert.equal(ui.host.querySelector('.playback-editor'),null);
  });
}

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
  assert.match(paragraphs,/also returns the fans to the switch’s own controller/);
});

test('the app binds intentional saved-login resumption without invoking it on handoff rendering',async()=>{
  const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
  const code=source.slice(source.indexOf('function showConnection()'),source.indexOf('function showLoginHelp()'));
  const sent=[],context={workspaceReady:false,secondaryCleanup:null,view:{},session:complete(),workspaceOptions:{live:true},
    renderSwitchConnection:(_host,options)=>{context.options=options;return ()=>{};},request:async(...args)=>sent.push(args),acceptSession(){},showLoginHelp(){},updateConnection(){},status(){},handoffStatus};
  vm.createContext(context);vm.runInContext(code,context);context.showConnection();assert.deepEqual(sent,[]);
  await context.options.restoreSaved();assert.deepEqual(sent,[['/api/session/restore?resume=true','POST']]);
});

test('Settings section navigation scrolls its pane without replacing form values or issuing requests',async context=>{
  const ui=await settings(context),pane=ui.byClass(ui.host,'settings-pane')[0],nav=ui.byClass(ui.host,'settings-nav')[0];
  pane.bounds={top:50,bottom:350,height:300};pane.scrollHeight=2500;
  pane.children.forEach((section,index)=>Object.defineProperty(section,'bounds',{get:()=>({top:50+index*500-pane.scrollTop,bottom:550+index*500-pane.scrollTop,height:500})}));
  const field=ui.findAll(ui.host,node=>node.tagName==='input')[0];field.value='Unapplied fixture value';
  const before=ui.calls.length;
  ui.button('Remote',nav).click();
  assert.equal(pane.scrollTop,1500);assert.equal(ui.host.scrollTop,0);
  assert.equal(ui.button('Remote',nav).getAttribute('aria-pressed'),'true');
  assert.equal(field.isConnected,true);assert.equal(field.value,'Unapplied fixture value');assert.equal(ui.calls.length,before);
  ui.cleanup();assert.equal(pane.listeners.has('scroll'),false);
});

test('About shows the service disclaimer, including its risk sentence, and keeps the short line without it',async context=>{
  const text=ui=>ui.findAll(ui.byClass(ui.host,'settings-about')[0],node=>node.tagName==='p').map(node=>node.textContent);
  assert.ok(text(await settings(context)).includes(aboutInfo.disclaimer));
  const offline=await settings(context,{about:async()=>{throw new Error('The workspace could not be reached.');}});
  assert.ok(text(offline).includes('Unofficial community project. Not affiliated with Ubiquiti.'));
});
