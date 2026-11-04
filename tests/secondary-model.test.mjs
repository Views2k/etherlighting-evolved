import test from 'node:test';
import assert from 'node:assert/strict';
import {createOperationGate, matchingProfile, profileNameError, sceneFingerprint, scopeSummary, templateForRule, validateRuleDraft, conditionAvailable, ruleRateFromCycle, rulesSnapshot, profileSfpSummary} from '../preview/secondary-model.mjs';

const scene = () => ({brightness:50,zones:[{id:'desk',name:'Desk',ports:[1,2],effect:{id:'static',name:'Static',params:{color:'#ABCDEF'}}}],
  rules:[],overrides:{'1':{color:'#123456',brightness:null}},sfp:{'9':{mode:'color',color:'#FFFFFF',brightness:80,effect:'steady',period:6,secondary_color:'#006fff',color_brightness:100,secondary_brightness:100}}});

test('profile comparison ignores telemetry but includes pins, brightness and rule precedence', () => {
  const saved = scene(), live = structuredClone(saved);
  live.sfp = Object.entries(live.sfp).map(([port,light]) => ({...light,port:Number(port),display_rgb:[50,60,70],preview:'#112233',link:{up:false}}));
  live.revision = 33; live.zones[0].effect.name = 'Localized name'; live.zones[0].effect.params.color = '#abcdef'; live.zones[0].ports.reverse();
  assert.equal(sceneFingerprint(live),sceneFingerprint(saved));
  assert.equal(matchingProfile({Evening:saved},live),'Evening');
  live.overrides['1'].brightness = 42;
  assert.equal(matchingProfile({Evening:saved},live),null);
  const a = scene(), b = scene(); a.rules = [{id:'a'},{id:'b'}]; b.rules = [{id:'b'},{id:'a'}];
  assert.notEqual(sceneFingerprint(a),sceneFingerprint(b));
});

test('profile SFP summaries describe active synchronization instead of dormant per-port settings',()=>{
  const saved={sfp:{9:{mode:'color',effect:'pulse'},10:{mode:'unmanaged',effect:'steady'}},sfp_sync:{mode:'together',source_port:5}},before=structuredClone(saved);
  assert.deepEqual(profileSfpSummary(saved),[{port:9,label:'Gentle pulse · Together'},{port:10,label:'Gentle pulse · Together'}]);
  assert.deepEqual(profileSfpSummary({...saved,sfp_sync:{mode:'ethernet',source_port:5}}),[{port:9,label:'Following port 5'},{port:10,label:'Following port 5'}]);
  assert.deepEqual(profileSfpSummary({...saved,sfp_sync:{mode:'independent'}}),[{port:9,label:'Gentle pulse'},{port:10,label:'Link status'}]);
  assert.deepEqual(profileSfpSummary({...saved,sfp:Object.entries(saved.sfp).map(([port,light])=>({...light,port:Number(port)}))}),profileSfpSummary(saved));
  assert.deepEqual(saved,before);
});

test('rule templates retain SFP scope, enabled state and existing name', () => {
  const draft = {id:'alert',name:'Rack alert',ports:[9,10],enabled:false,match:'any',conditions:[],look:{color:'#fff'}};
  const template = {name:'Anomaly',conditions:[{field:'anomaly',op:'is',value:true}],look:{color:'#ff0000'}};
  const applied = templateForRule(template,draft,[1,2,3]);
  assert.deepEqual(applied.ports,[9,10]); assert.equal(applied.name,'Rack alert'); assert.equal(applied.enabled,false); assert.equal(applied.id,'alert');
  applied.conditions[0].value = false; assert.equal(template.conditions[0].value,true);
  assert.deepEqual(templateForRule(template,{...draft,ports:[]},[1,2]).ports,[]);
  assert.equal(templateForRule(template,{...draft,name:''},[1,2]).name,'Anomaly');
  assert.equal(templateForRule(template,{...draft,name:'   '},[1,2]).name,'Anomaly');
});

test('rules cannot apply an empty scope or silently become unconditional', () => {
  const draft = {name:'Alert',ports:[9],conditions:[{field:'anomaly',op:'is',value:true}]};
  assert.equal(validateRuleDraft(draft),'');
  assert.match(validateRuleDraft({...draft,conditions:[]}),/condition/);
  assert.match(validateRuleDraft({...draft,ports:[]}),/port/);
  assert.match(validateRuleDraft({...draft,conditions:[{field:'weekday',op:'in',value:[]}]}),/value/);
});

test('bulk scope summaries distinguish Ethernet, SFP, all and empty', () => {
  assert.equal(scopeSummary([1,2,9,10],[1,2],[9,10]),'All ports');
  assert.equal(scopeSummary(null,[1,2],[9,10]),'All Ethernet ports');
  assert.equal(scopeSummary([9,10],[1,2],[9,10]),'All SFP ports');
  assert.equal(scopeSummary([],[1,2],[9,10]),'No ports selected');
});

test('profile name validation agrees with the store character and length rules', () => {
  for (const name of ['Evening','Café 2','Desk_v2.0-test','A'.repeat(64),'夜間 ²']) assert.equal(profileNameError(name),'');
  for (const name of ['', '  ','.','..',' . ',' .. ', 'A'.repeat(65),'Desk / Night','Desk: Night','Desk\nNight','Desk💡']) assert.notEqual(profileNameError(name),'');
});

test('unavailable rule fields and operators require replacement without rejecting valid condition values',()=>{
  const specs=[{key:'anomaly',operators:['is','is_not']}],draft={name:'Alert',ports:[1],conditions:[{field:'anomaly',op:'is',value:true}]};
  assert.equal(conditionAvailable(draft.conditions[0],specs),true);assert.equal(validateRuleDraft(draft,specs),'');
  for(const condition of [null,{field:'old_field',op:'is',value:true},{field:'anomaly',op:'old_operator',value:true}]){
    assert.equal(conditionAvailable(condition,specs),false);assert.match(validateRuleDraft({...draft,conditions:[condition]},specs),/unavailable/);
  }
});

test('unchanged cycle controls retain the exact saved rate, including faster legacy settings',()=>{
  for(const rate of [.123456789,.333333333333,2])assert.equal(ruleRateFromCycle(rate,String(1/rate),String(1/rate)),rate);
  assert.equal(ruleRateFromCycle(.5,'2','2.00'),.5);assert.equal(ruleRateFromCycle(.5,'2','4'),.25);
});

test('rule snapshot replacement preserves the loaded scene and does not mutate the draft',()=>{
  const before=scene(),rules=[{id:'rule-b',name:'Second'},{id:'rule-a',name:'First'}],saved=structuredClone({before,rules});
  const result=rulesSnapshot(before,rules);assert.deepEqual({...result,rules:[]},before);assert.deepEqual(result.rules,rules);
  assert.deepEqual({before,rules},saved);result.rules[0].name='Changed';assert.equal(rules[0].name,'Second');
  for(const invalid of [[{id:'same'},{id:'same'}],[{name:'No ID'}],[null]])assert.throws(()=>rulesSnapshot(before,invalid),/Refresh/);
});

test('pending writes block duplicate actions and canLeave waits for completion', async () => {
  let release, writes = 0; const busy = [], errors = [];
  const gate = createOperationGate(value => busy.push(value),error => errors.push(error));
  const pending = gate.run(async () => { writes++; await new Promise(resolve => release = resolve); });
  const duplicate = gate.run(() => writes++); assert.equal(pending,duplicate);
  let left = false; const leaving = gate.settle().then(() => left = true);
  await Promise.resolve(); assert.equal(writes,1); assert.equal(left,false);
  release(); await leaving; assert.deepEqual(busy,[true,false]); assert.equal(left,true); assert.deepEqual(errors,[]);
  await gate.run(() => { throw new Error('Failed'); }); assert.equal(errors.length,1);
  gate.close(); await gate.run(() => writes++); assert.equal(writes,1); assert.equal(await gate.settle(),false);
});

test('saved optical group effects take precedence over the saved Follow Ethernet description',()=>{
  const saved={version:3,topology:{version:1,copper_ports:[1],sfp_ports:[9,10]},sfp_sync:{mode:'ethernet',source_port:1},
    zones:[{ports:[1,10],effect:{id:'activity_glow'}}],sfp:{9:{mode:'off'},10:{mode:'off'}}};
  assert.deepEqual(profileSfpSummary(saved),[{port:9,label:'Following port 1'},{port:10,label:'Activity Glow'}]);
});
