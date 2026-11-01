import test from 'node:test';
import assert from 'node:assert/strict';
import {CATALOG} from '../src/catalog.mjs';
import {dataPreviewFacts, effectById} from '../src/effects.mjs';
import {defaultConfig, effectColor, mix, rgb, resolvedColorParams} from '../preview/effect-renderer.mjs';

const near = (actual, expected, tolerance = 1e-8) => actual.forEach((channel, index) =>
  assert.ok(Math.abs(channel - expected[index]) <= tolerance, `${actual} != ${expected}`));
const config = () => defaultConfig('poe_usage');
const settled = (settings, facts) => {
  const animationState = {};
  effectColor(settings, 0, 1, 0, {...facts, animationState});
  return rgb(effectColor(settings, 0, 1, 30, {...facts, animationState}));
};

test('PoE Usage exposes independent colors and fade speed with automatic power scale', () => {
  const effect = CATALOG.find(item => item.id === 'poe_usage');
  assert.equal(effect.name, 'PoE Usage');
  assert.equal(effect.category, 'data');
  assert.equal(effectById(effect.id).supportsPreview, true);
  assert.equal(effect.params.find(param => param.key === 'speed').label, 'Fade speed');
  assert.ok(!effect.params.some(param => /scale|max_watts|capacity/.test(param.key)));
  for (const key of ['low_color','high_color','no_power_color']) {
    const color = effect.params.find(param => param.key === key);
    assert.equal(color.random_param, `random_${key}`);
    assert.equal(color.brightness_param, `brightness_${key}`);
  }
});

test('missing, invalid, zero and non-delivering power use the chosen no-power color', () => {
  const settings = config(); settings.params.no_power_color = '#183142';
  for (const facts of [{}, {poe:'off',poe_power:40}, {poe:null,poe_power:40}, {poe:'poe'},
    ...[0,-1,null,NaN,Infinity,'15'].map(poe_power => ({poe:'poe+',poe_power}))]) {
    near(settled(settings, facts), rgb('#183142'));
  }
});

test('power colors follow the automatic logarithmic 60-watt range', () => {
  const settings = config(); settings.params.low_color = '#113355'; settings.params.high_color = '#BBDDFF';
  for (const watts of [0.1,1,5,15,30,60,120]) {
    const expected = mix('#113355','#BBDDFF',Math.min(1,Math.log1p(watts) / Math.log1p(60)));
    near(settled(settings, {poe:'poe++',poe_power:watts}), expected);
  }
});

test('PoE usage remains visible when the data link is down or marked as uplink', () => {
  const settings = config();
  const powered = {poe:'poe++',poe_power:25};
  near(settled(settings, {...powered,link:false,uplink:true}), settled(settings, {...powered,link:true,uplink:false}));
});

test('power changes and repeated timestamps never jump to a new color', () => {
  const settings = config(), animationState = {};
  const render = (time, poe, poe_power) => rgb(effectColor(settings,0,1,time,{poe,poe_power,animationState}));
  const initial = render(0,'off',0);
  near(render(.1,'poe++',60), initial);
  const rising = render(.2,'poe++',60);
  assert.notDeepEqual(rising, initial);
  near(render(.2,'off',0), rising);
  near(render(.2,'off',0), rising);
  const falling = render(.3,'off',0);
  assert.ok(falling.every((channel,index) => Math.abs(channel-initial[index]) < Math.abs(rising[index]-initial[index])));
});

test('fade-speed changes preserve the current output before using the new speed', () => {
  const settings = config(), animationState = {}, facts = {poe:'poe++',poe_power:60,animationState};
  effectColor(settings,0,1,0,facts);
  const before = rgb(effectColor(settings,0,1,.2,facts));
  const slower = structuredClone(animationState);
  settings.params.speed = 5;
  near(rgb(effectColor(settings,0,1,.2,facts)), before);
  const fast = rgb(effectColor(settings,0,1,.3,facts));
  settings.params.speed = 1;
  const slow = rgb(effectColor(settings,0,1,.3,{...facts,animationState:slower}));
  assert.ok(fast.some((channel,index) => channel > slow[index]));
});

test('slow fades retain a distinct fractional color at every 120-Hz sample', () => {
  const settings = config(); settings.params.speed = .1;
  const animationState = {}, facts = {poe:'poe++',poe_power:60,animationState};
  effectColor(settings,0,1,0,facts);
  const frames = Array.from({length:120}, (_,index) => rgb(effectColor(settings,0,1,(index+1)/120,facts)));
  assert.equal(new Set(frames.map(color => color.join(','))).size, 120);
  assert.ok(frames.every(color => color.some(channel => channel % 1 !== 0)));
  for (let index = 1; index < frames.length; index++) {
    assert.ok(frames[index].every((channel,k) => Math.abs(channel-frames[index-1][k]) < .4));
  }
});

test('power samples for gallery demonstrations are smooth, bounded and include idle ports', () => {
  const first = dataPreviewFacts(8,3), next = dataPreviewFacts(8,3+1/120);
  assert.ok(first.some(facts => facts.poe === 'off' && facts.poe_power === 0));
  assert.ok(first.some(facts => facts.poe !== 'off' && facts.poe_power > 0));
  for (let index=0; index<first.length; index++) {
    assert.ok(first[index].poe_power >= 0 && first[index].poe_power <= 60);
    assert.ok(Math.abs(next[index].poe_power-first[index].poe_power) < .2);
  }
  assert.notDeepEqual(first.map(facts => facts.poe_power), dataPreviewFacts(8,10).map(facts => facts.poe_power));
});

test('custom colors, per-color brightness and random colors retain the shared renderer contract', () => {
  const settings = config();
  Object.assign(settings.params, {low_color:'#153859',high_color:'#FDBA7D',brightness_low_color:23,brightness_high_color:61,random_low_color:true});
  const before = structuredClone(settings), watts = 10, animationState = {}, facts = {poe:'poe+',poe_power:watts,animationState};
  effectColor(settings,0,1,0,facts);
  const actual = rgb(effectColor(settings,0,1,30,facts)), colors = resolvedColorParams(settings,30);
  near(actual, mix(colors.low_color,colors.high_color,Math.log1p(watts)/Math.log1p(60)));
  assert.deepEqual(settings, before);
});

test('preview matches backend precise output through rising, falling and reversing power readings', () => {
  const frames = [
    {time:0,powers:[0,1,15,60],colors:[[17,24,39],[17,24,39],[17,24,39],[17,24,39]]},
    {time:.1,powers:[0,1,15,60],colors:[[17,24,39],[30.40449762217222,44.608876230236405,70.71037666761436],[42.984700968999405,41.03671972434721,66.36166439957533],[51.081057074283684,38.737754410501054,63.56292401750176]]},
    {time:.4,powers:[0,1,15,60],colors:[[17,24,39],[59.48614092537154,89.32073372022971,139.5074244385261],[99.3596451273527,77.99862758880295,125.72399088722398],[125.0213995747646,70.71195657287117,116.8532609547853]]},
    {time:1,powers:[0,1,15,60],colors:[[17,24,39],[87.82358791031669,132.88841928776847,206.54396269750677],[154.29196015202783,114.01468395987516,183.5672414287671],[197.0696161700613,101.86794212759406,168.77990354599012]]},
    {time:1.5,powers:[60,15,1,0],colors:[[17,24,39],[97.14804390249361,147.22439553048866,228.60238070516414],[172.36747536227622,125.86579153573558,202.60060192894298],[220.7771303054945,112.11984013210572,185.86640022017622]]},
    {time:2.5,powers:[60,15,1,0],colors:[[197.0696161700613,101.86794212759406,168.77990354599012],[169.4299702603627,137.28876594999036,219.37850538389185],[117.16871348071665,152.12838207260594,237.44412501142386],[55.48852833095865,40.64368792690104,66.7394798781684]]},
  ];
  const settings = config(), states = Array.from({length:4}, () => ({}));
  for (const frame of frames) frame.powers.forEach((poe_power,index) => {
    near(rgb(effectColor(settings,index,4,frame.time,{poe:'poe++',poe_power,animationState:states[index]})), frame.colors[index]);
  });
});
