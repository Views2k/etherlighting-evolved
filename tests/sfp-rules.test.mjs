import test from 'node:test';
import assert from 'node:assert/strict';
import { makeRule,matchingSfpRule,rulePortLabel } from '../src/rules.mjs';

function anomaly(ports = [9]) {
  return {...makeRule('anomaly'),id:'sfp-alert',ports,look:{color:'#FF0000',style:'solid',brightness:null,rate:1}};
}

test('rule port labels identify scopes consistently',() => {
  assert.equal(rulePortLabel(null),'All copper ports');
  assert.equal(rulePortLabel([9,10]),'Port 9, Port 10');
  assert.equal(rulePortLabel([1,9],[9]),'Port 1, SFP 9');
});

test('SFP rules match only when they explicitly target the port and apply to its mode',() => {
  const rule = anomaly(),item = {port:9,mode:'color',color:'#0000FF',anomaly:true,link:true,brightness:50,color_brightness:100,facts:{}};
  const rules = [anomaly(null),rule,anomaly()];
  assert.equal(matchingSfpRule(rules,item),rule);
  item.anomaly = null;assert.equal(matchingSfpRule(rules,item),null);
  item.anomaly = true;item.mode = 'off';assert.equal(matchingSfpRule([rule],item),null);
  item.mode = 'unmanaged';rule.look.style = 'effect';assert.equal(matchingSfpRule([rule],item),null);
  item.mode = 'color';assert.equal(matchingSfpRule([rule],item),rule);
  rule.enabled = false;assert.equal(matchingSfpRule([rule],item),null);
});
