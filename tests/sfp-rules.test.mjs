import test from 'node:test';
import assert from 'node:assert/strict';
import { createState,sanitizeSfp,SfpEdits,sfpSettings,sfpControlStatus,snapshot,applySnapshot } from '../src/state.mjs';
import { makeRule,matchingSfpRule,rulePortLabel } from '../src/rules.mjs';
import { ruleEditor,ruleScope,rulePortsForScope,ruleScopeControl } from '../src/rule-editor.mjs';
import { sfpActiveRule,sfpPreview,sfpLightingView,rulesView } from '../src/views.mjs';

function anomaly(ports = [9]) {
  return {...makeRule('anomaly'),id:'sfp-alert',ports,look:{color:'#FF0000',style:'solid',brightness:null,rate:1}};
}

test('rule scopes target only the available SFP ports and preserve copper selections',() => {
  const state = createState(),rule = anomaly();state.selected = [2,4];
  assert.equal(ruleScope(rule,state),'sfp-9');
  assert.deepEqual(rulePortsForScope('sfp-10',rule,state),[10]);
  assert.deepEqual(rulePortsForScope('sfp-all',rule,state),[9,10]);
  assert.deepEqual(rulePortsForScope('selected',rule,state),[2,4]);
  assert.equal(rulePortsForScope('all',rule,state),null);
  rule.ports = [1,7];
  assert.equal(ruleScope(rule,state),'selected');
  assert.deepEqual(rulePortsForScope('selected',rule,state),[1,7]);
  rule.ports = [1,9];
  assert.deepEqual(rulePortsForScope('selected',rule,state),[1,9]);
  rule.ports = [9,10];assert.equal(ruleScope(rule,state),'sfp-all');
  state.sfp = [state.sfp[0]];
  assert.doesNotMatch(ruleScopeControl(anomaly(),state),/value="sfp-10"|value="sfp-all"/);
  assert.deepEqual(rulePortsForScope('sfp-10',rule,state),[]);
  state.sfp = [];
  assert.doesNotMatch(ruleScopeControl(anomaly(),state),/value="sfp-/);
});

test('rule editor explains SFP restoration and identifies scopes consistently',() => {
  const state = createState(),rule = anomaly([9,10]);state.rules = [rule];
  const html = ruleEditor(rule,state);
  assert.match(html,/value="sfp-all" selected>Both SFP ports/);
  assert.match(html,/return to their regular lighting when the condition clears/);
  assert.match(html,/Off keeps the light off/);
  assert.match(html,/Port anomaly/);
  assert.match(rulesView(state),/SFP 9, SFP 10/);
  assert.equal(rulePortLabel(null),'All copper ports');
  assert.equal(rulePortLabel([9,10]),'Port 9, Port 10');
});

test('SFP preview uses explicit first-match rules and restores the base color when clear',() => {
  const state = createState(),item = state.sfp[0],rule = anomaly();
  Object.assign(item,{mode:'color',color:'#0000FF',anomaly:true,brightness:50,color_brightness:100});
  state.brightness = 80;state.rules = [anomaly(null),rule,anomaly()];
  assert.equal(matchingSfpRule(state.rules,item).id,rule.id);
  assert.equal(sfpPreview(state,item),'#660000');
  item.anomaly = false;
  assert.equal(sfpActiveRule(state,item),null);
  assert.equal(sfpPreview(state,item),'#000066');
  item.anomaly = null;assert.equal(matchingSfpRule(state.rules,item),null);
  item.anomaly = true;rule.enabled = false;state.rules = [rule];
  assert.equal(sfpActiveRule(state,item),null);
});

test('an anomaly temporarily overrides Link status while Off stays off',() => {
  const state = createState(),item = state.sfp[0],rule = anomaly();
  state.rules = [rule];state.brightness = 100;item.anomaly = true;
  assert.equal(sfpPreview(state,item),'#FF0000');
  item.anomaly = false;assert.equal(sfpPreview(state,item),'#51555C');
  item.anomaly = true;item.mode = 'off';assert.equal(sfpActiveRule(state,item),null);
  assert.equal(sfpPreview(state,item),'#000000');
  item.mode = 'unmanaged';rule.look.style = 'effect';
  assert.equal(sfpActiveRule(state,item),null);
  item.mode = 'color';assert.equal(sfpActiveRule(state,item),rule);
});

test('live SFP rules and previews remain authoritative through frame updates and clearing',() => {
  const state = createState(),base = state.sfp[0],edits = new SfpEdits([base]);
  state.mode = 'live';state.rules = [anomaly()];
  const remote = {...base,rule:{id:'sfp-alert',name:'Blocked uplink'},preview:'#7A0304',revision:2,acknowledged_revision:2,
    facts:{stp_state:'blocking',anomaly:true,speed:10000,unrecognized:'discard'},control:{state:'active',owned:true}};
  edits.receive([remote],{factsOnly:true});state.sfp = edits.values;
  assert.equal(sfpControlStatus(state.sfp[0]).text,'');
  assert.deepEqual(state.sfp[0].rule,remote.rule);
  assert.deepEqual(state.sfp[0].facts,{stp_state:'blocking',anomaly:true,speed:10000});
  assert.equal(sfpPreview(state,state.sfp[0],0),'#7A0304');
  assert.equal(sfpPreview(state,state.sfp[0],100),'#7A0304');
  assert.match(sfpLightingView(state),/Rule: Blocked uplink/);
  state.released = true;
  assert.equal(sfpActiveRule(state,state.sfp[0]),null);
  assert.equal(sfpPreview(state,state.sfp[0]),'#51555C');
  state.released = false;
  assert.equal(sfpSettings(state.sfp[0]).rule,undefined);
  edits.receive([{...base,rule:null,preview:null,revision:3,acknowledged_revision:3}],{factsOnly:true});
  assert.equal(edits.values[0].rule,undefined);assert.equal(edits.values[0].preview,undefined);
  assert.equal(sfpActiveRule(state,edits.values[0]),null);
});

test('scenes preserve SFP-targeted rules without saving live output metadata',() => {
  const state = createState();state.rules = [anomaly([9,10])];
  state.sfp[0].rule = {id:'sfp-alert',name:'Alert'};state.sfp[0].preview = '#FF0000';
  const saved = snapshot(state);assert.deepEqual(saved.rules[0].ports,[9,10]);
  assert.equal(saved.sfp['9'].rule,undefined);assert.equal(saved.sfp['9'].preview,undefined);
  const restored = createState();applySnapshot(restored,saved);
  assert.deepEqual(restored.rules[0].ports,[9,10]);
  assert.equal(sanitizeSfp([{port:9,rule:{id:8,name:'invalid'},preview:'invalid'}])[0].rule,undefined);
});
