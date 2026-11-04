import test from 'node:test';
import assert from 'node:assert/strict';
import { activityPeak,dataPreviewFacts,defaultConfig,effectColor } from '../src/effects.mjs';
import { hexToRgb } from '../src/color.mjs';

test('uplink traffic does not set the activity scale',() => {
  assert.equal(activityPeak([{link:true,traffic:20},{link:true,uplink:true,traffic:10000},
    {link:false,traffic:500},{link:null,traffic:1000},{link:true,traffic:NaN}]),20);
});

test('uplinks keep the same appearance as an idle device in each data effect',() => {
  for (const id of ['traffic','traffic_trails','activity_glow']) {
    const config = defaultConfig(id),uplink = {},idle = {};
    for (const t of [0,.25,1,3,10]) {
      const base = {link:true,peakTraffic:100};
      assert.equal(effectColor(config,0,8,t,{...base,uplink:true,traffic:10000,animationState:uplink}),
        effectColor(config,0,8,t,{...base,uplink:false,traffic:0,animationState:idle}),`${id} at ${t}`);
    }
  }
});

test('a device retains its full activity level with a busy uplink beside it',() => {
  const config = defaultConfig('activity_glow'),state = {};
  const facts = [{link:true,traffic:100},{link:true,uplink:true,traffic:10000}];
  for (const t of [0,.1,.5,1,2,5]) effectColor(config,0,2,t,{...facts[0],peakTraffic:activityPeak(facts),animationState:state});
  assert.ok(state.level > .99);
});

test('opted-in uplinks use their measured traffic in all three previews',() => {
  const facts = [{link:true,uplink:true,traffic:1000},{link:true,traffic:20},{link:false,traffic:9000}];
  assert.equal(activityPeak(facts,true),1000);
  for (const id of ['traffic','traffic_trails','activity_glow']) {
    const config = defaultConfig(id);
    config.params.include_uplinks = true;
    const render = (port,animationState,t) => effectColor(config,0,2,t,{...port,peakTraffic:1000,animationState});
    const optical = {}, device = {}, quiet = {};
    let active, idle;
    for (const t of [0,.25,1,3]) {
      active = render(facts[0],optical,t);
      assert.equal(active,render({...facts[0],uplink:false},device,t),id);
      idle = render({...facts[0],traffic:0},quiet,t);
    }
    assert.notEqual(active,idle,id);
  }
});

test('illustrative activity previews rank ports and change over time',() => {
  const config = defaultConfig('activity_glow'),states = Array.from({length:8},() => ({}));
  const frames = [0,.25,1,4,8].map(time => {
    const facts = dataPreviewFacts(8,time),peakTraffic = activityPeak(facts);
    return facts.map((port,index) => effectColor(config,index,8,time,{...port,peakTraffic,animationState:states[index]}));
  });
  assert.ok(new Set(frames[2]).size > 3);
  assert.notDeepEqual(frames[2],frames[4]);
});

test('a speed edit preserves the current level before changing the fade rate',() => {
  const config = defaultConfig('activity_glow'),state = {},facts = {link:true,traffic:1,peakTraffic:1,animationState:state};
  effectColor(config,0,1,0,facts);
  const before = effectColor(config,0,1,.2,facts);
  config.params.speed = 4;
  assert.equal(effectColor(config,0,1,.2,facts),before);
  const after = hexToRgb(effectColor(config,0,1,.3,facts));
  assert.ok(after.some((value,index) => value > hexToRgb(before)[index]));
});
