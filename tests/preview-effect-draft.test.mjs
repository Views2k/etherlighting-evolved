import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {effectDrafts,effectDraftForPort,stageEffectDraft,leaveSfpEffect,portEffect} from '../preview/effect-draft.mjs';
import {lightingOperations,lightingSnapshot} from '../preview/lighting-state.mjs';
import {previewDraftColors} from '../preview/draft-preview.mjs';
import {sceneFingerprint} from '../preview/secondary-model.mjs';

const config=(id,ports,params={},extra={})=>({id,ports,params,...extra});
const ports=()=>Array.from({length:8},(_,index)=>({port:index+1,color:'#FFFFFF'}));
const scene=()=>({brightness:100,mode:'running',zones:[{id:'all',name:'All Ethernet ports',ports:[1,2,3,4,5,6,7,8],effect:{id:'static',params:{color:'#FFFFFF'}}}],
  rules:[],overrides:{2:{color:'#FF0000',brightness:60},6:{color:'#00FF00',brightness:80}},sfp:{},sfp_sync:{mode:'independent',source_port:1}});

test('a mixed activity group and return to SFP lighting round trip without changing saved optical settings',()=>{
  const original=scene();original.sfp={9:{mode:'off',color:'#ff0000'},10:{mode:'color',effect:'pulse',period:6}};
  original.sfp_sync={mode:'together',source_port:2};
  const runtime=saved=>({...saved,ports:ports(),sfp:Object.entries(saved.sfp).map(([port,light])=>({...light,port:Number(port)}))});
  const staged=stageEffectDraft({},config('activity_glow',[2,10],{},{clearOverrides:true}),'activity');
  const joined=lightingSnapshot(original,runtime(original),staged);
  assert.deepEqual(joined.zones.find(zone=>zone.id==='activity').ports,[2,10]);
  assert.deepEqual(joined.sfp,original.sfp);assert.deepEqual(joined.sfp_sync,original.sfp_sync);
  assert.equal(joined.overrides[2],undefined);assert.deepEqual(joined.overrides[6],original.overrides[6]);
  const detach=leaveSfpEffect({},[10]);assert.equal(portEffect(runtime(joined),detach,10),undefined);
  const left=lightingSnapshot(joined,runtime(joined),detach);
  assert.deepEqual(left.zones.find(zone=>zone.id==='activity').ports,[2]);
  assert.deepEqual(left.sfp,original.sfp);assert.deepEqual(left.sfp_sync,original.sfp_sync);
  const optical=stageEffectDraft({},config('gradient',[9]),'optical');
  const opticalOnly=lightingSnapshot(left,runtime(left),optical);
  const removed=lightingSnapshot(opticalOnly,runtime(opticalOnly),leaveSfpEffect({},[9]));
  assert.deepEqual(removed,left);
  const restored=stageEffectDraft(detach,config('activity_glow',[2,10]),'activity');
  assert.deepEqual(lightingSnapshot(joined,runtime(joined),restored),joined);
  assert.throws(()=>lightingSnapshot(joined,runtime(joined),{sfpDetached:[2]}),/available SFP port/);
  assert.throws(()=>lightingSnapshot(joined,runtime(joined),{effect:config('static',[11])}),/available lighting ports/);
});

test('port map previews an optical member’s own traffic and brightness independently of Follow Ethernet',()=>{
  const state={brightness:100,ports:[{port:1,facts:{link:true,traffic:0}}],
    sfp:[{port:10,link:true,mode:'off',brightness:40,facts:{link:true,uplink:true,traffic:1000}}],
    sfp_sync:{mode:'ethernet',source_port:1},rules:[],zones:[],overrides:{}};
  const draft={effect:config('activity_glow',[1,10],{idle_color:'#000000',active_color:'#ffffff',include_uplinks:true})};
  const clocks={previewClocks:new Map(),previewStates:new Map()};
  previewDraftColors(state,draft,0,clocks);
  const shown=previewDraftColors(state,draft,5,clocks);
  assert.equal(Math.max(...shown[1]),0);
  assert.ok(shown[10].every(channel=>channel>101&&channel<=102));
  state.rules=[{id:'alert',enabled:true,ports:[10],conditions:[],look:{style:'solid',color:'#ff0000',brightness:50}}];
  assert.deepEqual(previewDraftColors(state,draft,6,clocks)[10],[51,0,0]);
});

test('different effect scopes remain staged together without changing earlier parameters or other edits',()=>{
  const original={brightness:65,sfp:{9:{mode:'color',color:'#FF00FF'}},colors:{8:{color:'#112233',brightness:25}}};
  const first=config('breathing',[3,1,3],{color:'#0066FF'}),before=structuredClone({original,first});
  const one=stageEffectDraft(original,first,'blue-pair');
  const two=stageEffectDraft(one,config('comet',[5,7],{color:'#FF00AA'}),'pink-pair');
  assert.deepEqual(effectDrafts(two).map(effect=>[effect.id,effect.ports,effect.zoneId]),[
    ['breathing',[1,3],'blue-pair'],['comet',[5,7],'pink-pair']]);
  assert.deepEqual(effectDraftForPort(two,1).params,{color:'#0066FF'});
  assert.equal(effectDraftForPort(two,7).id,'comet');assert.equal(effectDraftForPort(two,2),undefined);
  assert.equal(two.brightness,65);assert.deepEqual(two.sfp,original.sfp);assert.deepEqual(two.colors,original.colors);
  assert.deepEqual({original,first},before);assert.equal(effectDrafts(one).length,1);
});

test('an overlapping effect changes only its target ports and keeps the other staged scopes',()=>{
  const one=stageEffectDraft({},config('breathing',[1,2,3],{color:'#0066FF'}),'first');
  const two=stageEffectDraft(one,config('gradient',[5,6]),'second');
  const before=structuredClone(two),result=stageEffectDraft(two,config('comet',[2,3,4]),'overlap');
  assert.deepEqual(effectDrafts(result).map(effect=>[effect.id,effect.ports,effect.zoneId]),[
    ['breathing',[1],'first'],['gradient',[5,6],'second'],['comet',[2,3,4],'overlap']]);
  for(const id of [2,3,4])assert.equal(effectDraftForPort(result,id).id,'comet');
  assert.equal(effectDraftForPort(result,1).id,'breathing');assert.equal(effectDraftForPort(result,6).id,'gradient');
  assert.deepEqual(two,before);
});

test('editing an exact scope retains its group identity and override choice until explicitly changed',()=>{
  const initial=stageEffectDraft({},config('comet',[2,1],{color:'#FF00AA'},{clearOverrides:true}),'stable-group');
  const edited=stageEffectDraft(initial,config('breathing',[1,2],{color:'#0066FF'}),'unused-id');
  assert.equal(effectDrafts(edited).length,1);assert.equal(edited.effects[0].zoneId,'stable-group');
  assert.equal(edited.effects[0].clearOverrides,true);assert.equal(edited.effects[0].id,'breathing');
  const retained=stageEffectDraft(edited,config('breathing',[2,1],{color:'#AA00FF'},{clearOverrides:false}),'other-id');
  assert.equal(retained.effects[0].zoneId,'stable-group');assert.equal(retained.effects[0].clearOverrides,false);
});

test('replacing fixed colors removes only targeted draft overrides and follow-effect entries',()=>{
  const draft={colors:{1:{color:'#111111'},3:{color:'#333333'},8:{color:'#888888'}},returnToEffect:[2,5],brightness:35};
  const before=structuredClone(draft);
  const result=stageEffectDraft(draft,config('breathing',[1,2,3],{}, {clearOverrides:true}),'selection');
  assert.deepEqual(result.colors,{8:{color:'#888888'}});assert.deepEqual(result.returnToEffect,[5]);assert.equal(result.brightness,35);
  assert.deepEqual(draft,before);
  const withoutClearing=stageEffectDraft(draft,config('breathing',[1,2,3],{}, {clearOverrides:false}),'selection');
  assert.deepEqual(withoutClearing.colors,draft.colors);assert.deepEqual(withoutClearing.returnToEffect,draft.returnToEffect);
});

test('legacy singular effect drafts remain readable and migrate without losing unrelated changes',()=>{
  const legacy={effect:config('breathing',[1,2],{color:'#0066FF'},{zoneId:'legacy',clearOverrides:true}),brightness:70};
  assert.equal(effectDraftForPort(legacy,2).id,'breathing');assert.equal(effectDrafts(legacy).length,1);
  const result=stageEffectDraft(legacy,config('comet',[4,6]),'new');
  assert.equal(Object.hasOwn(result,'effect'),false);assert.equal(result.brightness,70);
  assert.deepEqual(result.effects.map(effect=>[effect.id,effect.ports,effect.zoneId]),[
    ['breathing',[1,2],'legacy'],['comet',[4,6],'new']]);
  assert.equal(effectDraftForPort(result,2).clearOverrides,true);assert.equal(legacy.effect.zoneId,'legacy');
  const combined={...legacy,effects:[config('gradient',[2,3])]};
  assert.equal(effectDraftForPort(combined,2).id,'gradient');
  assert.deepEqual(effectDrafts({}),[]);
});

test('one snapshot includes both staged effects and clears only the selected applied overrides',()=>{
  const base=scene(),state={...structuredClone(base),ports:ports(),sfp:[]},before=structuredClone({base,state});
  let draft=stageEffectDraft({colors:{8:{color:'#112233',brightness:25}}},config('breathing',[1,2],{color:'#0066FF'},{clearOverrides:true}),'blue');
  draft=stageEffectDraft(draft,config('comet',[3,4],{color:'#FF00AA'},{clearOverrides:true}),'pink');
  const operations=lightingOperations(state,draft),snapshot=lightingSnapshot(base,state,draft);
  assert.equal(operations.filter(operation=>operation.key==='effect').length,2);
  assert.ok(operations.filter(operation=>operation.key==='effect').every(operation=>operation.body.clear_overrides===true));
  const zoneFor=id=>snapshot.zones.find(zone=>zone.ports.includes(id));
  assert.equal(zoneFor(1).effect.id,'breathing');assert.equal(zoneFor(2).effect.id,'breathing');
  assert.equal(zoneFor(3).effect.id,'comet');assert.equal(zoneFor(4).effect.id,'comet');assert.equal(zoneFor(5).effect.id,'static');
  assert.equal(snapshot.overrides[2],undefined);assert.deepEqual(snapshot.overrides[6],base.overrides[6]);
  assert.deepEqual(snapshot.overrides[8],{color:'#112233',brightness:25});
  assert.deepEqual({base,state},before);
});

test('editing a subset of a replacement effect keeps clearing its prior fixed color',()=>{
  const base=scene(),state={...structuredClone(base),ports:ports(),sfp:[]};
  const initial=stageEffectDraft({},config('breathing',[1,2,3],{color:'#0066FF'},{clearOverrides:true}),'original');
  const edited=stageEffectDraft(initial,config('breathing',[2],{color:'#AA00FF'}),'subset');
  const snapshot=lightingSnapshot(base,state,edited);
  assert.equal(snapshot.overrides[2],undefined);
  assert.deepEqual(snapshot.overrides[6],base.overrides[6]);
  assert.deepEqual(snapshot.zones.find(zone=>zone.ports.includes(2)).effect,{id:'breathing',params:{color:'#AA00FF'}});
  assert.deepEqual(effectDraftForPort(edited,1).params,{color:'#0066FF'});
});

test('parameter edits preserve a fixed color staged after choosing the effect',()=>{
  const base=scene(),state={...structuredClone(base),ports:ports(),sfp:[]};
  const chosen=stageEffectDraft({},config('breathing',[1,2],{color:'#0066FF'},{clearOverrides:true}),'pair');
  const manual={color:'#55BB22',brightness:40};
  const edited=stageEffectDraft({...chosen,colors:{1:manual}},config('breathing',[1,2],{color:'#AA00FF'}),'ignored');
  const snapshot=lightingSnapshot(base,state,edited);
  assert.deepEqual(snapshot.overrides[1],manual);
  assert.equal(snapshot.overrides[2],undefined);
  assert.deepEqual(snapshot.overrides[6],base.overrides[6]);
  const replaced=stageEffectDraft(edited,config('comet',[1,2],{},{clearOverrides:true}),'replacement');
  assert.equal(lightingSnapshot(base,state,replaced).overrides[1],undefined);
});

test('a partially overlapping edit preserves per-port override intent without splitting its effect geometry',()=>{
  const base=scene(),state={...structuredClone(base),ports:ports(),sfp:[]};
  let draft=stageEffectDraft({},config('gradient',[1,2,3],{},{clearOverrides:true}),'cleared');
  draft=stageEffectDraft(draft,config('gradient',[5,6],{},{clearOverrides:false}),'retained');
  draft={...draft,colors:{3:{color:'#FFFFFF',brightness:30},8:{color:'#8899AA',brightness:75}}};
  draft=stageEffectDraft(draft,config('gradient',[2,3,4,5,6],{color_a:'#FF00AA'}),'mixed');
  const snapshot=lightingSnapshot(base,state,draft),group=snapshot.zones.find(zone=>zone.ports.includes(2));
  assert.deepEqual(group.ports,[2,3,4,5,6]);
  assert.equal(snapshot.overrides[2],undefined);
  assert.deepEqual(snapshot.overrides[3],{color:'#FFFFFF',brightness:30});
  assert.deepEqual(snapshot.overrides[6],base.overrides[6]);
  assert.deepEqual(snapshot.overrides[8],{color:'#8899AA',brightness:75});
  assert.equal(effectDraftForPort(draft,1).clearOverrides,true);
  const restored=JSON.parse(JSON.stringify(draft));
  const reedited=stageEffectDraft(restored,config('gradient',[2,6],{color_a:'#0044FF'}),'smaller');
  const next=lightingSnapshot(base,state,reedited);
  assert.deepEqual(next.zones.find(zone=>zone.ports.includes(2)).ports,[2,6]);
  assert.equal(next.overrides[2],undefined);
  assert.deepEqual(next.overrides[6],base.overrides[6]);
  assert.deepEqual(next.overrides[3],snapshot.overrides[3]);
});

const appSource=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
const workspaceRequestSource=appSource.slice(appSource.indexOf('const workspaceRequest='),appSource.indexOf('const galleryState'));
const savePreviewSource=appSource.slice(appSource.indexOf('async function savePreviewProfile('),appSource.indexOf('function copperInspectorKey('));
function profileFixture({draft={},changedScene=false,changeWorkspace=false,pendingPlan=null}={}){
  const base=scene(),state={...structuredClone(base),ports:ports(),sfp:[],connected:true},calls=[];
  const session={workspace_id:'fixture-workspace-a'};
  const serverScene=structuredClone(base);if(changedScene)serverScene.brightness=75;
  const context={draft,pendingPlan,state,session,disconnected:false,draftBaseline:sceneFingerprint(base),lightingSnapshot,sceneFingerprint,
    serialize:operation=>operation(),apiRequest:async(path,method,body,options)=>{
      calls.push({path,method,body,options});
      if(path==='/api/scene'&&method==='GET'){
        if(changeWorkspace)session.workspace_id='fixture-workspace-b';
        return {scene:structuredClone(serverScene),revision:'fixture-revision'};
      }
      return {saved:true};
    }};
  vm.runInNewContext(`${workspaceRequestSource}\n${savePreviewSource}\nglobalThis.save=savePreviewProfile;`,context);
  return {save:context.save,calls,base,state,draft};
}

test('Save preview profile captures all staged scopes without applying or consuming the draft',async()=>{
  let draft=stageEffectDraft({},config('breathing',[1,2],{color:'#0066FF'},{clearOverrides:true}),'first');
  draft=stageEffectDraft(draft,config('comet',[3,4],{color:'#FF00AA'},{clearOverrides:true}),'second');
  draft.colors={8:{color:'#112233',brightness:45}};
  const fixture=profileFixture({draft}),before=structuredClone(draft);
  await fixture.save('Evening');
  assert.deepEqual(fixture.calls.map(({path,method})=>[path,method]),[['/api/scene','GET'],['/api/presets','POST']]);
  const saved=fixture.calls[1].body;
  assert.equal(saved.name,'Evening');assert.equal(saved.expected_revision,'fixture-revision');assert.equal(saved.overwrite,false);
  assert.deepEqual(saved.scene,lightingSnapshot(fixture.base,fixture.state,draft));
  assert.deepEqual(draft,before);
});

test('Save preview profile preserves the draft when its applied baseline changed',async()=>{
  const draft=stageEffectDraft({},config('breathing',[1,2]),'pair'),before=structuredClone(draft);
  const fixture=profileFixture({draft,changedScene:true});
  await assert.rejects(fixture.save('Evening'),/lighting setup changed/);
  assert.equal(fixture.calls.length,1);assert.deepEqual(draft,before);
});

test('Save preview profile cannot save a draft into a newly connected workspace',async()=>{
  const fixture=profileFixture({draft:stageEffectDraft({},config('breathing',[1,2]),'pair'),changeWorkspace:true});
  await assert.rejects(fixture.save('Evening'),/connected switch changed/);
  assert.equal(fixture.calls.length,1);
});

test('Save preview profile leaves unresolved apply and lighting-control changes for review',async()=>{
  for(const options of [{pendingPlan:{scene:{}}},{draft:{mode:'released'}}]){
    const fixture=profileFixture(options);
    await assert.rejects(fixture.save('Evening'),/Apply or discard the lighting control change/);
    assert.equal(fixture.calls.length,0);
  }
});
