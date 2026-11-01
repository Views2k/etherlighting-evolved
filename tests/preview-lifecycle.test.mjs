import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {sceneFingerprint} from '../preview/secondary-model.mjs';
import {lightingOperations,lightingSnapshot} from '../preview/lighting-state.mjs';

const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
const extract=(start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
const deferred=()=>{let resolve;const promise=new Promise(done=>{resolve=done;});return {promise,resolve};};
const scene={brightness:100,zones:[],rules:[],overrides:{},sfp:{}};
const no=()=>{};

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
