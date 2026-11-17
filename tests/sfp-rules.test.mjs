import test from 'node:test';
import assert from 'node:assert/strict';
import { matchingSfpRule } from '../src/rules.mjs';

function anomaly(ports = [9]) {
  return {id:'sfp-alert',name:'Anomaly detected',conditions:[{field:'anomaly',op:'is',value:true}],match:'all',ports,enabled:true,
    look:{color:'#FF0000',style:'solid',brightness:null,rate:1}};
}

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
