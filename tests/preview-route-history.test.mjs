import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {createRouteHistory} from '../preview/route-history.mjs';

const flush=()=>new Promise(resolve=>setImmediate(resolve));
const deferred=()=>{let resolve;return {promise:new Promise(done=>{resolve=done;}),resolve:value=>resolve(value)};};
function browser({native=false,entries=[{hash:'#lighting',state:{unrelated:'preserved'}}],index=entries.length-1}={}){
  const records=structuredClone(entries).map((entry,number)=>({...entry,key:`entry-${number}`})),pending=[],writes=[];
  let nextKey=records.length;
  const location={pathname:'/',search:'?fixture=1',get hash(){return records[index].hash;}};
  const history={get state(){return records[index].state;},get length(){return records.length;},
    replaceState(state,_title,url){writes.push(['replace',url]);records[index]={...records[index],hash:new URL(url,'https://ee.example').hash,state};},
    pushState(state,_title,url){writes.push(['push',url]);records.splice(index+1);records.push({hash:new URL(url,'https://ee.example').hash,state,key:`entry-${nextKey++}`});index++;},
    go(delta){const target=index+delta;if(target>=0&&target<records.length)pending.push(target);}};
  const environment={history,location,...(native?{navigation:{get currentEntry(){return {index,key:records[index].key};},entries(){return records.map((record,index)=>({index,key:record.key}));}}}:{})};
  const routes=createRouteHistory(environment);
  return {routes,environment,records,pending,writes,get index(){return index;},
    pruneStart(count){records.splice(0,count);index-=count;for(let i=0;i<pending.length;i++)pending[i]-=count;},
    visit(target){index=target;return routes.observe();},
    deliver(){assert.ok(pending.length,'A history traversal must be pending');index=pending.shift();return routes.observe();}};
}

test('canceling Back restores the committed entry without rewriting the destination or forward history',async()=>{
  const b=browser();b.routes.commit('Lighting',{fromHistory:true});b.routes.commit('Settings');b.routes.commit('Profiles');
  const before=structuredClone(b.records),writes=b.writes.length;
  b.visit(1);const restore=b.routes.restore();assert.equal(b.pending[0],2);assert.equal(b.writes.length,writes);
  assert.equal(b.deliver(),null);assert.equal(await restore,true);assert.deepEqual(b.records,before);assert.equal(b.index,2);
});

test('accepted Back changes the committed entry, and canceling Forward returns there',async()=>{
  const b=browser();b.routes.commit('Settings');b.routes.commit('Profiles');b.visit(1);b.routes.commit('Settings',{fromHistory:true});
  b.visit(2);const pending=b.routes.restore();b.deliver();await pending;
  assert.equal(b.index,1);assert.deepEqual(b.records.map(entry=>entry.hash),['#lighting','#settings','#profiles']);
});

test('entry metadata survives reload without replacing unrelated history state',()=>{
  const b=browser();assert.equal(b.records[0].state.unrelated,'preserved');b.routes.commit('Settings');
  const marker=structuredClone(b.environment.history.state.eeRoute),reloaded=createRouteHistory(b.environment);
  assert.deepEqual(b.environment.history.state.eeRoute,marker);reloaded.commit('Profiles');
  assert.equal(b.environment.history.state.eeRoute.position,marker.position+1);
});

test('browser entry indices restore an older unmarked same-document destination when available',async()=>{
  const b=browser({native:true,entries:[{hash:'#settings',state:null},{hash:'#lighting',state:null}]});
  const before=structuredClone(b.records);b.visit(0);const pending=b.routes.restore();b.deliver();assert.equal(await pending,true);
  assert.deepEqual(b.records,before);assert.equal(b.index,1);
});

test('canceling Back uses stable route positions when Chromium prunes older native entries',async()=>{
  const b=browser({native:true,entries:[{hash:'#help',state:null},{hash:'#settings',state:null},{hash:'#lighting',state:null}]});
  b.routes.commit('Profiles');b.pruneStart(1);b.routes.commit('Lighting');b.pruneStart(1);
  const before=structuredClone(b.records),writes=b.writes.length;
  b.visit(1);const restored=b.routes.restore();
  assert.deepEqual(b.pending,[2]);b.deliver();assert.equal(await restored,true);
  assert.equal(b.index,2);assert.equal(b.writes.length,writes);assert.deepEqual(b.records,before);
});

test('unmarked legacy destinations resolve current native positions by stable entry key after pruning',async()=>{
  const b=browser({native:true,entries:[{hash:'#help',state:null},{hash:'#settings',state:null},{hash:'#lighting',state:null}]});
  b.pruneStart(1);const before=structuredClone(b.records);b.visit(0);const restored=b.routes.restore();
  assert.deepEqual(b.pending,[1]);b.deliver();assert.equal(await restored,true);assert.deepEqual(b.records,before);
});

test('untracked legacy history without an entry index is never guessed or overwritten',async()=>{
  const b=browser({entries:[{hash:'#settings',state:null},{hash:'#lighting',state:null}]});const before=structuredClone(b.records);
  b.visit(0);assert.equal(await b.routes.restore(),false);assert.deepEqual(b.records,before);assert.deepEqual(b.pending,[]);
});

test('accepting untracked legacy history starts a fresh managed chain instead of fabricating its old position',()=>{
  const b=browser({entries:[{hash:'#settings',state:null},{hash:'#lighting',state:null}]});
  const originalChain=b.environment.history.state.eeRoute.chain;b.visit(0);b.routes.commit('Settings',{fromHistory:true});
  assert.notEqual(b.environment.history.state.eeRoute.chain,originalChain);assert.equal(b.environment.history.state.eeRoute.position,0);
  b.routes.commit('Help');assert.equal(b.environment.history.state.eeRoute.position,1);
});

test('explicit navigation from an untracked cursor starts a new chain without assuming skipped entries',()=>{
  const b=browser({entries:[{hash:'#settings',state:null},{hash:'#lighting',state:null}]});
  const originalChain=b.environment.history.state.eeRoute.chain;b.visit(0);b.routes.commit('Help');
  assert.notEqual(b.environment.history.state.eeRoute.chain,originalChain);assert.equal(b.environment.history.state.eeRoute.position,0);
  assert.deepEqual(b.records.map(entry=>entry.hash),['#settings','#help']);
});

const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
const navigateSource=source.slice(source.indexOf('async function navigate(target,'),source.indexOf('function renderStudio()'));
function app(b,confirmDraft){
  const no=()=>{},rendered=[];
  const context={workspaceReady:true,navigationVersion:0,page:'Profiles',routeHistory:b.routes,
    secondaryCleanup:Object.assign(()=>{},{canLeave:async()=>true}),pendingPlan:null,operation:Promise.resolve(),
    confirmDraft,activeColorPopover:undefined,portAnimator:{stop:no},portGlow:null,uplinkBandwidth:null,saveUiState:no,
    document:{title:'',querySelectorAll:()=>[]},view:{querySelector:()=>null,classList:{remove:no,add:no},offsetWidth:1,innerHTML:''},
    renderStudio:()=>rendered.push('Lighting'),renderEffectGallery:()=>()=>{},renderAutomation:async()=>()=>{},renderProfiles:async()=>()=>{},
    renderSettings:async()=>()=>{},renderHelp:async()=>{rendered.push('Help');return ()=>{};},
    workspaceRequest:()=>no,state:{},effects:[],applyProfile:no,acceptSession:no,closeWorkspace:no,refresh:async()=>{},notify:no};
  vm.createContext(context);vm.runInContext(navigateSource,context);return {context,rendered};
}
const populated=()=>{const b=browser();b.routes.commit('Lighting',{fromHistory:true});b.routes.commit('Settings');b.routes.commit('Profiles');return b;};

test('two rapid Back requests sharing Keep editing preserve every original destination',async()=>{
  const b=populated(),choice=deferred(),{context}=app(b,()=>choice.promise),draft={color:'#123456'};
  context.draft=draft;const before=structuredClone(b.records);
  const first=context.navigate('Settings',{fromHistory:true,historyEntry:b.visit(1)});await flush();
  const second=context.navigate('Lighting',{fromHistory:true,historyEntry:b.visit(0)});await flush();
  choice.resolve(false);await flush();assert.equal(b.pending.length,1);assert.equal(b.pending[0],2);
  b.deliver();await Promise.all([first,second]);
  assert.equal(context.page,'Profiles');assert.equal(context.draft,draft);assert.deepEqual(b.records,before);
});

test('a later Back intent waits for reversal and then reaches its original entry before confirmation',async()=>{
  const b=populated();let choices=0;const {context}=app(b,async()=>++choices!==1);
  const first=context.navigate('Settings',{fromHistory:true,historyEntry:b.visit(1)});await flush();assert.equal(b.pending[0],2);
  const latest=context.navigate('Lighting',{fromHistory:true,historyEntry:b.visit(0)});await flush();
  assert.equal(choices,1);b.deliver();await flush();assert.equal(b.pending[0],0);
  b.deliver();await Promise.all([first,latest]);
  assert.equal(context.page,'Lighting');assert.equal(b.index,0);assert.equal(choices,2);
  assert.deepEqual(b.records.map(entry=>entry.hash),['#lighting','#settings','#profiles']);
});

test('a new app navigation during reversal preserves the restored trail before adding its destination',async()=>{
  const b=populated();let choices=0;const {context}=app(b,async()=>++choices!==1);
  const first=context.navigate('Settings',{fromHistory:true,historyEntry:b.visit(1)});await flush();
  const next=context.navigate('Help');await flush();assert.equal(b.records.length,3);
  b.deliver();await Promise.all([first,next]);
  assert.equal(context.page,'Help');assert.deepEqual(b.records.map(entry=>entry.hash),['#lighting','#settings','#profiles','#help']);
});

test('an older secondary-page rejection cannot reverse a newer accepted navigation',async()=>{
  const b=populated(),old=deferred(),{context}=app(b,async()=>true);let leaves=0;
  context.secondaryCleanup.canLeave=()=>++leaves===1?old.promise:Promise.resolve(true);
  const first=context.navigate('Settings',{fromHistory:true,historyEntry:b.visit(1)});await flush();
  const next=context.navigate('Lighting',{fromHistory:true,historyEntry:b.visit(0)});await next;
  old.resolve(false);await first;
  assert.equal(context.page,'Lighting');assert.equal(b.index,0);assert.deepEqual(b.pending,[]);
});
