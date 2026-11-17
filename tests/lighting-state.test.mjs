import test from 'node:test';
import assert from 'node:assert/strict';
import {lightingOperations,lightingSnapshot,controlSource,equalSetting,sfpSettings} from '../preview/lighting-state.mjs';
import {EFFECTS} from '../src/effects.mjs';

const base=()=>({num_ports:8,brightness:100,mode:'running',zones:[
  {id:'left',name:'Left',ports:[1,2,3,4],effect:{id:'marquee',params:{color:'#123456'}}},
  {id:'right',name:'Right',ports:[5,6,7,8],effect:{id:'breathing',params:{color:'#ABCDEF'}}},
],ports:[1,2,3,4,5,6,7,8].map(port=>({port,facts:{link:true}})),sfp:[
  {port:9,mode:'color',effect:'steady',color:'#FF0000',brightness:30,secondary_color:'#112233',period:6},
  {port:10,mode:'color',effect:'pulse',color:'#0000FF',brightness:70,secondary_color:'#334455',period:12},
],rules:[]});
const rule=(changes={})=>({id:'alarm',name:'Alarm',enabled:true,ports:[1],match:'all',conditions:[],look:{style:'solid',color:'#FF0000',brightness:50},...changes});

test('optical-only drafts allow shared SFP settings and reject imaginary Ethernet output',()=>{
  const state={...base(),num_ports:0,ports:[],zones:[],sfp:[{port:1,mode:'color'},{port:2,mode:'color'}],sfp_sync:{mode:'independent',source_port:1}};
  for(const mode of ['independent','together']) assert.equal(lightingOperations(state,{sfp_sync:{mode}})[0].body.mode,mode);
  assert.throws(()=>lightingOperations(state,{sfp_sync:{mode:'ethernet'}}),/Ethernet port/);
  assert.throws(()=>lightingOperations(state,{colors:{1:{color:'#FFFFFF'}}}),/Ethernet ports/);
  const [group]=lightingOperations(state,{effect:{id:'static',ports:[1],params:{}}});
  assert.deepEqual(group.body.zones[0].ports,[1]);
  assert.equal(lightingOperations(state,{sfp:{2:{color:'#FFFFFF'}}})[0].path,'/api/sfp/2');
});

test('draft validation follows reported copper membership rather than a numeric range',()=>{
  const state={...base(),num_ports:48,ports:[{port:3},{port:24}]};
  assert.throws(()=>lightingOperations(state,{colors:{2:{color:'#FFFFFF'}}}),/Ethernet ports/);
  assert.throws(()=>lightingOperations(state,{sfp_sync:{mode:'ethernet',source_port:2}}),/Ethernet port/);
  assert.equal(lightingOperations(state,{colors:{24:{color:'#FFFFFF'}}})[0].key,'colors');
});

test('a combined draft plans brightness, port colors and SFP writes in that order',()=>{
  const plan=lightingOperations(base(),{brightness:50,colors:{1:{color:'#123456',brightness:20}},sfp:{9:{effect:'pulse'}}});
  assert.deepEqual(plan.map(item=>[item.key,item.path]),[['brightness','/api/brightness'],['colors','/api/ports'],['sfp:9','/api/sfp/9']]);
});

test('new effect group preserves unrelated ports and regroups before setting the effect',()=>{
  const state=base(),before=structuredClone(state),draft={effect:{id:'comet',ports:[5,2],params:{color:'#3366FF'},clearOverrides:true}};
  const plan=lightingOperations(state,draft),newZones=plan[0].body.zones;
  assert.deepEqual(plan.map(item=>item.key),['group','effect']);
  assert.deepEqual(newZones[0],{...state.zones[0],ports:[1,3,4]});
  assert.deepEqual(newZones[1],{...state.zones[1],ports:[6,7,8]});
  assert.deepEqual(newZones[2].ports,[2,5]);assert.equal(plan[1].body.clear_overrides,true);
  assert.equal(plan[0].body.clear_overrides,undefined);
  assert.deepEqual(state,before);assert.deepEqual(draft.effect.ports,[5,2]);
  assert.deepEqual(plan.map(item=>item.path),['/api/zones',`/api/zones/${newZones[2].id}/effect`]);
});

test('exact groups use one effect update and generated names cannot collide with surviving groups',()=>{
  const state=base();
  let plan=lightingOperations(state,{effect:{id:'static',ports:[4,3,2,1,1],params:{color:'#FFFFFF'}}});
  assert.equal(plan.length,1);assert.equal(plan[0].path,'/api/zones/left/effect');
  state.zones[1].id='selection-1-2';
  plan=lightingOperations(state,{effect:{id:'static',ports:[1,2],params:{}}});
  const groups=plan[0].body.zones;
  assert.equal(new Set(groups.map(group=>group.id)).size,groups.length);
  assert.equal(groups.at(-1).id,'selection-1-2-2');
  assert.equal(plan[1].path,'/api/zones/selection-1-2-2/effect');
});

test('mixed Ethernet/SFP draft coalesces partial SFP changes without copying one port over another',()=>{
  const state=base(),draft={effect:{id:'comet',ports:[1,2],params:{}},colors:{3:{color:'#DDEEFF'}},sfp:{9:{effect:'two_color',period:8},10:{color_brightness:25}}};
  const plan=lightingOperations(state,draft),sfpWrites=plan.filter(item=>item.key.startsWith('sfp:'));
  assert.equal(sfpWrites.length,2);
  assert.deepEqual(sfpWrites[0].body,{effect:'two_color',period:8});
  assert.deepEqual(sfpWrites[1].body,{color_brightness:25});
  const actual=structuredClone(state.sfp);
  for(const {path,body} of sfpWrites)Object.assign(actual.find(item=>path.endsWith('/'+item.port)),body);
  assert.equal(actual[0].color,'#FF0000');assert.equal(actual[1].color,'#0000FF');
  assert.equal(actual[0].brightness,30);assert.equal(actual[1].brightness,70);
  assert.equal(actual[0].effect,'two_color');assert.equal(actual[1].effect,'pulse');
  assert.deepEqual(draft.sfp[9],{effect:'two_color',period:8});
});

test('planning rejects crossed or unknown port scopes and never treats an invalid mode as release',()=>{
  const state=base();
  for(const ports of [[],[1,11],[0],[1.5],['1']])assert.throws(()=>lightingOperations(state,{effect:{id:'static',ports,params:{}}}),/port/);
  const [mixed]=lightingOperations(state,{effect:{id:'static',ports:[1,9],params:{}}});
  assert.deepEqual(mixed.body.zones.at(-1).ports,[1,9]);
  assert.throws(()=>lightingOperations(state,{colors:{9:{color:'#FFFFFF'}}}),/Ethernet ports/);
  assert.throws(()=>lightingOperations(state,{returnToEffect:[9]}),/Ethernet ports/);
  assert.throws(()=>lightingOperations(state,{sfp:{11:{mode:'color'}}}),/available SFP/);
  assert.throws(()=>lightingOperations(state,{mode:'unknown'}),/lighting mode/);
});

test('settings snapshots omit telemetry and compare object values independent of key order',()=>{
  const settings=sfpSettings({...base().sfp[0],display_rgb:[1,2,3],rule:{id:'alarm'},source:{kind:'rule'},control:'active'});
  assert.deepEqual(Object.keys(settings).sort(),['brightness','color','effect','mode','period','secondary_color']);
  assert.equal(equalSetting([{color:'#123456',brightness:50},{brightness:50,color:'#123456'}]),true);
  assert.equal(equalSetting([[1,2],[2,1]]),false);assert.equal(equalSetting([]),false);
  assert.equal(equalSetting([0,null]),false);
});

test('copper control source follows manual-over-rule-over-effect precedence',()=>{
  const state=base();state.rules=[rule()];
  assert.equal(controlSource(state,1).kind,'rule');
  state.overrides={1:{color:'#FFFFFF'}};
  assert.equal(controlSource(state,1).kind,'color');
  delete state.overrides;state.ports[0].source={kind:'override'};
  assert.equal(controlSource(state,1).kind,'color');
  state.ports[0].source={kind:'zone',id:'left'};
  assert.equal(controlSource(state,1,[{id:'marquee',name:'Marquee'}]).label,'Marquee');
  state.ports[0].source={kind:'none'};assert.equal(controlSource(state,1).kind,'off');
  assert.equal(controlSource(state,999).kind,'unknown');
});

test('reported rule ownership wins over a later browser clock or incomplete rule list',()=>{
  const state=base();state.rules=[rule({conditions:[{field:'link',op:'is',value:false}]})];
  state.ports[0].source={kind:'rule',id:'alarm',name:'Alarm'};
  assert.equal(controlSource(state,1).kind,'rule');
  state.rules=[];assert.equal(controlSource(state,1).label,'Alarm');
  state.sfp[0].rule={id:'remote',name:'SFP alarm'};
  assert.equal(controlSource(state,9).label,'SFP alarm');
  state.sfp[0].mode='off';assert.equal(controlSource(state,9).kind,'off');
});

test('SFP fallback requires explicit scope and respects link-managed and off semantics',()=>{
  const state=base();state.sfp[0].mode='unmanaged';state.rules=[rule({ports:null})];
  assert.equal(controlSource(state,9).kind,'link');
  state.rules=[rule({ports:[9]})];assert.equal(controlSource(state,9).kind,'rule');
  state.rules[0].look.style='effect';assert.equal(controlSource(state,9).kind,'link');
  state.sfp[0].mode='color';assert.equal(controlSource(state,9).kind,'rule');
  state.sfp[0].rule=null;assert.equal(controlSource(state,9).kind,'sfp');
  state.sfp[0].mode='off';assert.equal(controlSource(state,9).kind,'off');
});

test('Together control source uses the lowest actual SFP ID and preserves target rule ownership',()=>{
  for(const first of [17,25]){
    const state=base();state.num_ports=first-1;state.sfp_sync={mode:'together',source_port:1};
    state.sfp=[{port:first+1,mode:'unmanaged',effect:'steady'},
      {port:first,mode:'color',effect:'pulse',color:'#123456'}];
    assert.equal(controlSource(state,first+1).label,'Shared Gentle pulse');
    state.rules=[rule({ports:[first+1],look:{style:'effect'}})];
    assert.equal(controlSource(state,first+1).kind,'rule');
    assert.equal(controlSource(state,first).kind,'sfp');
    state.sfp[1].mode='off';
    assert.equal(controlSource(state,first+1).label,'SFP lights off');
    state.sfp[1].mode='unmanaged';
    assert.equal(controlSource(state,first+1).kind,'link');
    assert.equal(controlSource(state,9).kind,'unknown');
  }
});

const snapshot=state=>({version:2,brightness:state.brightness,zones:structuredClone(state.zones),rules:structuredClone(state.rules),
  overrides:{1:{color:'#123456',brightness:20},2:{color:'#654321',brightness:null},8:{color:'#FEDCBA',brightness:80}},
  sfp:Object.fromEntries(state.sfp.map(light=>[light.port,sfpSettings(light)])),sfp_sync:{mode:'independent',source_port:1}});

test('atomic lighting snapshot preserves unrelated groups, rules, pins and SFP settings without mutating inputs',()=>{
  const state=base();state.rules=[rule()];const before=snapshot(state),draft={brightness:60,effect:{id:'comet',ports:[1,2],params:{color:'#334455'},clearOverrides:true},colors:{3:{color:'#ABCDEF',brightness:35}},sfp:{9:{color_brightness:40}},sfp_sync:{mode:'together'}};
  const original=structuredClone({before,state,draft}),result=lightingSnapshot(before,state,draft);
  assert.deepEqual({before,state,draft},original);assert.notEqual(result,before);
  assert.equal(result.version,2);assert.equal(result.brightness,60);assert.deepEqual(result.rules,before.rules);
  assert.deepEqual(result.zones.find(zone=>zone.id==='right'),before.zones[1]);
  assert.deepEqual(result.zones.find(zone=>zone.id==='left').ports,[3,4]);
  assert.equal(result.zones.filter(zone=>zone.ports.includes(1)).length,1);
  assert.deepEqual(result.zones.at(-1).effect,{id:'comet',params:{color:'#334455'}});
  assert.equal(result.overrides[1],undefined);assert.equal(result.overrides[2],undefined);
  assert.deepEqual(result.overrides[8],before.overrides[8]);assert.deepEqual(result.overrides[3],draft.colors[3]);
  assert.deepEqual(result.sfp[9],{...before.sfp[9],color_brightness:40});assert.deepEqual(result.sfp[10],before.sfp[10]);
  assert.deepEqual(result.sfp_sync,{mode:'together',source_port:1});
});

test('parameter edits retain fixed pins unless explicit effect assignment requests their replacement',()=>{
  const state=base(),before=snapshot(state),effect={id:'marquee',ports:[1,2,3,4],params:{color:'#112233'}};
  const adjusted=lightingSnapshot(before,state,{effect});
  assert.deepEqual(adjusted.overrides,before.overrides);assert.equal(adjusted.zones.length,before.zones.length);
  const assigned=lightingSnapshot(before,state,{effect:{...effect,clearOverrides:true}});
  assert.deepEqual(Object.keys(assigned.overrides),['8']);
  assert.equal(lightingOperations(state,{effect})[0].body.clear_overrides,false);
});

test('snapshot uses authoritative base groups, preserves additional scene fields and safely handles encoded group IDs',()=>{
  const state=base(),before=snapshot(state);before.metadata={futureSetting:true};
  before.zones[0].id='group with / and %';before.zones[0].name='Current name';before.zones[1].effect.params.color='#777777';
  const result=lightingSnapshot(before,state,{effect:{id:'static',ports:[1,2,3,4],params:{color:'#FFFFFF'}}});
  assert.equal(result.zones[0].id,'group with / and %');assert.equal(result.zones[0].name,'Current name');
  assert.deepEqual(result.zones[1],before.zones[1]);assert.deepEqual(result.metadata,{futureSetting:true});
});

test('snapshot scopes return-to-effect and manual edits independently with fixed colors winning explicitly',()=>{
  const state=base(),before=snapshot(state),result=lightingSnapshot(before,state,{returnToEffect:[1,2],colors:{2:{color:'#000000',brightness:null}}});
  assert.equal(result.overrides[1],undefined);assert.deepEqual(result.overrides[2],{color:'#000000',brightness:null});
  assert.deepEqual(result.overrides[8],before.overrides[8]);assert.deepEqual(result.zones,before.zones);
});

test('empty and equal-brightness drafts keep the complete scene unchanged and mode is separate from configuration',()=>{
  const state=base(),before=snapshot(state);
  assert.deepEqual(lightingSnapshot(before,state,{}),before);
  assert.deepEqual(lightingOperations(state,{brightness:100}),[]);
  assert.deepEqual(lightingSnapshot(before,state,{brightness:100,mode:'released'}),before);
  assert.deepEqual(lightingOperations(state,{brightness:100,mode:'released'}).map(operation=>operation.key),['mode']);
});

test('restored drafts reject malformed structure, nonfinite and out-of-range values before planning',()=>{
  const state=base();
  for(const draft of [null,[],1,{unknown:true},{brightness:null},{brightness:'50'},{brightness:NaN},{brightness:Infinity},{brightness:-1},{brightness:101},{brightness:1.5},
    {effect:null},{effect:{}},{effect:{id:'unknown',ports:[1]}},{effect:{id:'static',ports:[1],params:null}},{effect:{id:'static',ports:[1],params:[]}},
    {effect:{id:'static',ports:[1],params:{unknown:1}}},{effect:{id:'static',ports:[1],params:{color:'red'}}},
    {effect:{id:'breathing',ports:[1],params:{period:0}}},{effect:{id:'static',ports:[1],params:{random_color:1}}},
    {effect:{id:'daylight',ports:[1],params:{palette:'missing'}}},{effect:{id:'static',ports:[1],clearOverrides:'true'}},
    {colors:null},{colors:[]},{colors:{1:{color:'#abc'}}},{colors:{1:{color:'#123456',brightness:101}}},{colors:{'01':{color:'#123456'}}},
    {colors:{1:{color:'#123456',extra:true}}},{returnToEffect:null},{returnToEffect:false},{mode:null}]) {
    assert.throws(()=>lightingOperations(state,draft),undefined,JSON.stringify(draft));
  }
});

test('restored SFP settings validate enums, color channels, duration and individual brightness',()=>{
  const state=base();
  for(const settings of [null,[],{extra:true},{mode:'invalid'},{effect:'comet'},{color:'blue'},{secondary_color:'#gggggg'},
    {brightness:1.5},{brightness:101},{color_brightness:-1},{secondary_brightness:null},{period:1},{period:31},{period:NaN}]) {
    assert.throws(()=>lightingOperations(state,{sfp:{9:settings}}),undefined,JSON.stringify(settings));
  }
  assert.throws(()=>lightingOperations(state,{sfp:{'09':{mode:'off'}}}),/available SFP/);
  assert.throws(()=>lightingOperations(state,{sfp:null}),/SFP lighting changes/);
  assert.throws(()=>lightingOperations(state,{sfp_sync:null}),/SFP sync/);
  assert.deepEqual(lightingOperations(state,{sfp:{9:{mode:'color',effect:'two_color',color:'#abcdef',secondary_color:'#012345',brightness:0,color_brightness:100,secondary_brightness:25,period:2}}})[0].body,
    {mode:'color',color:'#abcdef',brightness:0,effect:'two_color',secondary_color:'#012345',period:2,color_brightness:100,secondary_brightness:25});
});

test('mutually exclusive effect options cannot both survive restored drafts',()=>{
  assert.throws(()=>lightingOperations(base(),{effect:{id:'strobe',ports:[1],params:{random_color:true,random_colors:true}}}),/conflicting/);
});

test('every active catalog effect accepts its complete declared default parameters',()=>{
  const state=base(),before=snapshot(state);
  for (const effect of EFFECTS) {
    const params=Object.fromEntries(effect.params.map(param=>[param.key,structuredClone(param.default)]));
    const result=lightingSnapshot(before,state,{effect:{id:effect.id,ports:[1,2,3,4],params}});
    assert.deepEqual(result.zones[0].effect,{id:effect.id,params},effect.id);
  }
});

test('snapshot rejects an unavailable base or SFP settings missing from the authoritative scene',()=>{
  const state=base();for(const before of [null,{},[],{zones:[],rules:[],overrides:{},sfp:[]}])assert.throws(()=>lightingSnapshot(before,state,{}),/Refresh/);
  const before=snapshot(state);delete before.sfp[9];assert.throws(()=>lightingSnapshot(before,state,{sfp:{9:{mode:'off'}}}),/SFP light changed/);
});
