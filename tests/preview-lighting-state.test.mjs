import test from 'node:test';
import assert from 'node:assert/strict';
import {fixedColorSettings,fixedColorBrightness,sceneAutomationMatches,controlSource} from '../preview/lighting-state.mjs';

const state = () => ({brightness:40,ports:[{port:1,color:'#123456'},{port:2,color:'#ABCDEF'}],
  overrides:{2:{color:'#FFEEDD',brightness:65}},sfp_sync:{mode:'independent',source_port:1},
  sfp:[{port:9,color:'#00AAFF',brightness:100,color_brightness:60},
    {port:10,color:'#FF00AA',brightness:50,color_brightness:80}]});

test('mixed fixed-color controls display the SFP color brightness they edit',()=>{
  const current=state(),draft={sfp:{9:{color_brightness:25}}},before=structuredClone({current,draft});
  assert.deepEqual(fixedColorSettings(current,9),{color:'#00AAFF',brightness:60});
  assert.deepEqual(fixedColorSettings(current,9,draft),{color:'#00AAFF',brightness:25});
  Object.assign(current.sfp[0],draft.sfp[9]);
  assert.equal(fixedColorSettings(current,9).brightness,25);
  current.sfp[0].color_brightness=before.current.sfp[0].color_brightness;
  assert.deepEqual({current,draft},before);
});

test('legacy SFP brightness is composed once, including zero and fractional products',()=>{
  const current=state();
  assert.equal(fixedColorSettings(current,10).brightness,40);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{brightness:25,color_brightness:33}}}).brightness,8);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{brightness:0}}}).brightness,0);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{color_brightness:0}}}).brightness,0);
  assert.equal(fixedColorBrightness(fixedColorSettings(current,10),current),40);
});

test('Together displays the shared SFP setting instead of the dormant indicator setting',()=>{
  const current=state(),draft={sfp_sync:{mode:'together'},sfp:{9:{color:'#112233',color_brightness:30},10:{color_brightness:5}}};
  const before=structuredClone({current,draft});
  assert.deepEqual(fixedColorSettings(current,10,draft),{color:'#112233',brightness:30});
  assert.deepEqual(fixedColorSettings(current,9,draft),fixedColorSettings(current,10,draft));
  assert.deepEqual({current,draft},before);
});

test('copper overrides remain absolute and inherited brightness remains inherited',()=>{
  const current=state(),draft={brightness:20},before=structuredClone(current);
  const inherited=fixedColorSettings(current,1,draft),pinned=fixedColorSettings(current,2,draft);
  assert.deepEqual(inherited,{color:'#123456',brightness:null});
  assert.equal(fixedColorBrightness(inherited,current,draft),20);
  assert.equal(fixedColorBrightness(inherited,current),40);
  assert.equal(fixedColorBrightness(pinned,current,draft),65);
  assert.deepEqual(fixedColorSettings(current,2,{colors:{2:{color:'#001122',brightness:null}}}),{color:'#001122',brightness:null});
  pinned.brightness=1;
  assert.deepEqual(current,before);
});

test('SFP membership follows actual port IDs and no unknown port is fabricated',()=>{
  const current=state();current.sfp[0].port=25;current.sfp[1].port=26;current.sfp_sync.mode='together';
  assert.deepEqual(fixedColorSettings(current,26),{color:'#00AAFF',brightness:60});
  assert.throws(()=>fixedColorSettings(current,9),/available port/);
});

const onlineRule=(ports=[2])=>({id:'online',name:'Online color',enabled:true,ports,match:'all',
  conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#FFFFFF'}});
const acceptedScene=()=>({overrides:{},rules:[onlineRule()],sfp:{9:{mode:'color'},10:{mode:'color'}},sfp_sync:{mode:'independent',source_port:1}});

test('accepted-scene automation feedback ignores stale copper override frames after an override is cleared',()=>{
  const current=state(),scene=acceptedScene();
  Object.assign(current.ports[1],{facts:{link:true},override:{color:'#FF0000'},source:{kind:'override'}});
  current.rules=[onlineRule()];
  const before=structuredClone({current,scene});
  assert.deepEqual(sceneAutomationMatches(scene,current,[2,'2',99]).map(match=>[match.port,match.rule.name]),[[2,'Online color']]);
  assert.equal(controlSource(current,2).kind,'color');
  assert.deepEqual({current,scene},before);
});

test('accepted manual colors take precedence over stale rule output in Apply feedback',()=>{
  const current=state(),scene=acceptedScene();
  Object.assign(current.ports[1],{facts:{link:true},source:{kind:'rule',id:'online'}});
  current.overrides={};current.rules=[onlineRule()];scene.overrides[2]={color:'#0066FF',brightness:70};
  assert.deepEqual(sceneAutomationMatches(scene,current,[2]),[]);
  assert.equal(controlSource(current,2).kind,'rule');
});

test('Apply feedback evaluates accepted rule scope, enabled state, priority, and current facts',()=>{
  const current=state(),scene=acceptedScene();
  current.ports[1].facts={link:true};current.ports[1].source={kind:'rule',id:'old'};
  current.rules=[{...onlineRule(),id:'old'}];
  scene.rules=[{...onlineRule(),enabled:false},{...onlineRule([1]),id:'other-port'},onlineRule(),{...onlineRule(),id:'lower'}];
  assert.equal(sceneAutomationMatches(scene,current,[2])[0].rule.id,'online');
  current.ports[1].facts.link=false;
  assert.deepEqual(sceneAutomationMatches(scene,current,[2]),[]);
  scene.rules=[];current.ports[1].facts.link=true;
  assert.deepEqual(sceneAutomationMatches(scene,current,[2]),[]);
});

test('SFP feedback uses accepted modes and explicit SFP rule scope instead of stale frame rules',()=>{
  const current=state(),scene=acceptedScene();
  current.sfp.forEach(port=>Object.assign(port,{link:true,mode:'off',rule:{id:'stale'}}));
  scene.rules=[onlineRule(null),{...onlineRule([9,10]),id:'sfp'}];
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]).map(match=>[match.port,match.rule.id]),[[9,'sfp'],[10,'sfp']]);
  scene.sfp[9].mode='off';
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]).map(match=>match.port),[10]);
  scene.rules=[onlineRule(null)];
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]),[]);
});

test('shared and Ethernet-follow SFP modes use accepted settings while retaining each port facts',()=>{
  const current=state(),scene=acceptedScene();
  current.sfp.forEach(port=>Object.assign(port,{link:true,mode:'color'}));scene.rules=[onlineRule([9,10])];
  scene.sfp[9].mode='off';scene.sfp_sync.mode='together';
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]),[]);
  scene.sfp[9].mode='color';scene.sfp[10].mode='off';current.sfp[0].link=false;
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]).map(match=>match.port),[10]);
  scene.sfp[9].mode='off';scene.sfp_sync.mode='ethernet';current.sfp[0].link=true;
  assert.deepEqual(sceneAutomationMatches(scene,current,[9,10]).map(match=>match.port),[9,10]);
});

test('unmanaged SFP feedback matches solid automation but not a pass-through effect',()=>{
  const current=state(),scene=acceptedScene();current.sfp[0].link=true;scene.sfp[9].mode='unmanaged';
  scene.rules=[onlineRule([9])];
  assert.equal(sceneAutomationMatches(scene,current,[9]).length,1);
  scene.rules[0].look.style='effect';
  assert.deepEqual(sceneAutomationMatches(scene,current,[9]),[]);
  scene.sfp_sync.mode='ethernet';
  assert.equal(sceneAutomationMatches(scene,current,[9]).length,1);
});

test('automation feedback uses supplied schedule time and does not invent a match when clock is absent',()=>{
  const current=state(),scene=acceptedScene();
  scene.rules=[{...onlineRule(),conditions:[{field:'time',op:'between',value:['20:00','23:00']}]}];
  assert.equal(sceneAutomationMatches(scene,current,[2],new Date(2026,0,1,21)).length,1);
  assert.deepEqual(sceneAutomationMatches(scene,current,[2],new Date(2026,0,1,12)),[]);
  assert.deepEqual(sceneAutomationMatches(scene,current,[2]),[]);
});
