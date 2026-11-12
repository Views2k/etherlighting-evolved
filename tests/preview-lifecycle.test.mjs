import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {sceneFingerprint} from '../preview/secondary-model.mjs';
import {lightingOperations,lightingSnapshot} from '../preview/lighting-state.mjs';
import {createUplinkMeasurements} from '../preview/uplink-bandwidth.mjs';

const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
const extract=(start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
const deferred=()=>{let resolve;const promise=new Promise(done=>{resolve=done;});return {promise,resolve};};
const scene={brightness:100,zones:[],rules:[],overrides:{},sfp:{}};
const no=()=>{};

function refreshFixture(){
  const reads=[],measurements=createUplinkMeasurements();
  let now=0;
  class Socket {
    constructor(){this.listeners={};this.readyState=1;}
    addEventListener(type,callback){this.listeners[type]=callback;}
    close(){this.readyState=3;this.listeners.close();}
    message(payload){this.listeners.message({data:JSON.stringify(payload)});}
  }
  const c={session:{workspace_id:'demo',logged_in:true},state:{},page:'Help',lastApplied:null,workspaceReady:true,
    nextRefreshId:0,appliedRefreshId:0,liveUplinkVersion:0,closingWorkspace:false,streamPaused:false,
    currentSocket:null,disconnected:false,retryTimer:null,updateDraftBar:no,updateConnection:no,
    receiveScheduleClock:no,scheduleLiveUpdate:no,pushLiveColors:no,status:no,performance:{now:()=>now},
    showSceneChoice:async(_send,next)=>next,request:no,
    WebSocket:Socket,location:{protocol:'http:',host:'fixture'},document:{hidden:false,querySelector:()=>({})},
    clearTimeout:no,setTimeout:no,uplinkMeasurements:measurements,
    workspaceRequest:()=>path=>{const item=deferred();reads.push({path,...item});return item.promise;}};
  c.updateLive=()=>measurements.update(c.state,c.disconnected,c.session.workspace_id,now);
  vm.createContext(c);
  vm.runInContext(extract('async function refresh(rebuild', 'function isSfpPort(')+
    extract('function connectFrames()', "document.querySelectorAll('[data-page]')")+
    extract('async function acceptSession(next)', 'async function closeWorkspace()'),c);
  c.connectFrames();
  const snapshot=(n,generation='A')=>({connected:true,brightness:70,telemetry:{generation},
    uplink_bandwidth:{status:'ready',sequence:n,ports:[8],download_bps:n*1e6,upload_bps:n*2e6,
      sample_period_ms:1000,sample_age_ms:0}});
  return {c,reads,measurements,snapshot,at:value=>{now=value;}};
}

test('a slow port refresh keeps newer streamed rates, their age and the generation from recovery',async()=>{
  const {c,reads,measurements,snapshot,at}=refreshFixture();
  const pending=c.refresh(false);
  reads[0].resolve(snapshot(8));await new Promise(setImmediate);
  at(1000);c.currentSocket.message({type:'frame',...snapshot(1,'B')});
  at(5000);reads[1].resolve([{port:8}]);await pending;
  assert.equal(c.state.uplink_bandwidth.sequence,1);assert.equal(c.state.telemetry.generation,'B');
  assert.equal(measurements.last.reading.download,1e6);assert.equal(measurements.last.expires,11000);
  assert.equal(c.state.brightness,70);assert.equal(c.state.ports[0].port,8);
});

test('browser state ages from the state response even when the port request takes over ten seconds',async()=>{
  const {c,reads,measurements,snapshot,at}=refreshFixture();
  const pending=c.refresh(false);at(500);reads[0].resolve(snapshot(3));await new Promise(setImmediate);
  at(15000);reads[1].resolve([]);await pending;
  assert.equal(measurements.last.expires,10500);assert.equal(measurements.last.reading.download,3e6);
});

test('a pending refresh and a retired socket cannot restore readings after a lost connection',async()=>{
  const {c,reads,measurements,snapshot}=refreshFixture();
  const old=c.currentSocket;
  old.message({type:'frame',...snapshot(1)});
  const pending=c.refresh(false);reads[0].resolve(snapshot(2));await new Promise(setImmediate);
  old.close();assert.equal(measurements.last,null);
  c.connectFrames();c.currentSocket.listeners.open();old.message({type:'frame',...snapshot(9)});old.listeners.error();
  reads[1].resolve([]);await pending;
  assert.equal(c.currentSocket.readyState,1);assert.equal(c.state.uplink_bandwidth,null);assert.equal(measurements.last,null);
  c.currentSocket.message({type:'frame',...snapshot(1,'B')});assert.equal(measurements.last.reading.download,1e6);
});

test('out-of-order refreshes keep the latest completed snapshot',async()=>{
  const {c,reads,measurements,snapshot}=refreshFixture();
  const first=c.refresh(false),second=c.refresh(false);
  reads[2].resolve(snapshot(2));reads[3].resolve([]);await second;
  reads[0].resolve(snapshot(1));reads[1].resolve([]);await first;
  assert.equal(c.state.uplink_bandwidth.sequence,2);assert.equal(measurements.last.reading.download,2e6);
});

test('a switch disconnect in the active session clears rates while a port refresh is pending',async()=>{
  const {c,reads,measurements,snapshot}=refreshFixture();
  c.currentSocket.message({type:'frame',...snapshot(1)});
  const pending=c.refresh(false);reads[0].resolve(snapshot(2));await new Promise(setImmediate);
  await c.acceptSession({...c.session,connected:false});
  reads[1].resolve([]);await pending;
  assert.equal(c.state.connected,false);assert.equal(c.session.connected,false);assert.equal(measurements.last,null);
});

test('a partial frame for a replacement generation clears readings from the previous generation',()=>{
  const {c,measurements,snapshot}=refreshFixture();
  c.currentSocket.message({type:'frame',...snapshot(1)});
  c.currentSocket.message({type:'frame',connected:true,telemetry:{generation:'B'}});
  c.updateLive();
  assert.equal(c.state.uplink_bandwidth,null);assert.equal(measurements.last,null);
});

test('an Apply preflight from an old workspace cannot create new workspace history',async()=>{
  const read=deferred(),writes=[];
  const context={view:{querySelector:()=>null},edits:new Set(['master']),serialize:fn=>fn(),
    session:{workspace_id:'old'},state:{...scene,connected:true},draft:{brightness:50},
    disconnected:false,pendingPlan:null,lastApplied:null,appliedCount:0,draftBaseline:sceneFingerprint(scene),
    workspaceRequest:()=>async(...args)=>{if(args[1])writes.push(args);return read.promise;},
    lightingOperations,lightingSnapshot,sceneFingerprint};
  vm.createContext(context);vm.runInContext(extract('async function flushEdits()', 'function discardDraft()'),context);
  const pending=context.flushEdits();
  context.session={workspace_id:'new'};context.state={};context.draft={};context.edits.clear();
  read.resolve({revision:'old',scene});
  await assert.rejects(pending,/connected switch changed/);
  assert.equal(context.pendingPlan,null);assert.equal(context.lastApplied,null);assert.equal(writes.length,0);
});

test('replacement workspace initializes independently of an older pending state read',async()=>{
  const reads=[],navigated=[];
  const context={closingWorkspace:false,session:{workspace_id:'old'},workspaceReady:false,openingWorkspace:null,
    state:{},selected:new Set(),secondaryCleanup:null,edits:new Set(),effects:[],draft:{},draftBaseline:null,
    rememberedEffects:new Map(),galleryState:{},showSceneChoice:async(_send,next)=>next,request:no,saveUiState:no,
    resetWorkspace:()=>{context.state={};context.workspaceReady=false;},updateConnection:no,showConnection:no,
    refresh:()=>{const item=deferred();reads.push(item);return item.promise;},workspaceStorageKey:()=>null,
    location:{hash:'#lighting'},page:'Lighting',navigate:async target=>navigated.push([context.session.workspace_id,target]),
    portAnimator:{setPreview:no},status:no,previewDraft:no,document:{querySelector:()=>({})},view:{innerHTML:''}};
  vm.createContext(context);vm.runInContext(extract('async function acceptSession(next)', 'async function closeWorkspace()'),context);
  const first=context.acceptSession({workspace_id:'old',logged_in:true});await new Promise(setImmediate);
  const second=context.acceptSession({workspace_id:'new',logged_in:true});await new Promise(setImmediate);
  assert.equal(reads.length,2);
  reads[0].resolve();await first;assert.equal(context.workspaceReady,false);assert.deepEqual(navigated,[]);
  context.state={ports:[{port:1}]};reads[1].resolve();await second;
  assert.equal(context.workspaceReady,true);assert.deepEqual(navigated,[['new','Lighting']]);
  assert.deepEqual([...context.selected],[1]);assert.equal(context.openingWorkspace,null);
});

test('reload reconciles accepted configuration and preserves only unapplied output ownership',async()=>{
  const accepted={...scene,brightness:50},saved={selected:[1],draft:{brightness:50,mode:'released'},
    draftBaseline:sceneFingerprint(scene),recovery:{before:{revision:'before',scene},mode:'running'}};
  const calls=[];
  const context={closingWorkspace:false,session:{workspace_id:'same'},workspaceReady:false,openingWorkspace:null,
    state:{...accepted,num_ports:1,ports:[{port:1}],sfp:[],connected:true,mode:'running'},selected:new Set(),secondaryCleanup:null,
    edits:new Set(),draft:{},draftBaseline:null,lastApplied:null,rememberedEffects:new Map(),galleryState:{},
    showSceneChoice:async(_send,next)=>next,request:async(...args)=>{calls.push(args);return {revision:'accepted',scene:accepted};},
    saveUiState:no,resetWorkspace:no,updateConnection:no,showConnection:no,refresh:async()=>{},
    workspaceStorageKey:()=> 'fixture',sessionStorage:{getItem:()=>JSON.stringify(saved)},allPorts:()=>[1],
    lightingOperations,lightingSnapshot,sceneFingerprint,location:{hash:'#settings'},page:'Lighting',
    navigate:async target=>{assert.equal(target,'Lighting');},portAnimator:{setPreview:no},status:no,notify:no,
    previewDraft:no,document:{querySelector:()=>({})},view:{innerHTML:''}};
  vm.createContext(context);vm.runInContext(extract('async function acceptSession(next)', 'async function closeWorkspace()'),context);
  await context.acceptSession({workspace_id:'same',logged_in:true});
  assert.equal(JSON.stringify(context.draft),JSON.stringify({mode:'released'}));
  assert.equal(context.lastApplied.after.revision,'accepted');assert.equal(context.lastApplied.before.revision,'before');
  assert.equal(context.draftBaseline,sceneFingerprint(accepted));assert.deepEqual(calls,[['/api/scene']]);
});
