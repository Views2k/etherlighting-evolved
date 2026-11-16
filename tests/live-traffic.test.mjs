import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { CATALOG } from '../src/catalog.mjs';
import { effectColor,defaultConfig } from '../src/effects.mjs';
import { hexToRgb } from '../src/color.mjs';
import { effectColor as preciseEffectColor, rgb } from '../preview/effect-renderer.mjs';
import { profileColors } from '../preview/profile-preview.mjs';

test('Live Traffic has automatic capacity and opt-in uplink customization',() => {
  const effect = CATALOG.find(item => item.id === 'traffic');
  assert.equal(effect.name,'Live Traffic');
  assert.ok(!effect.params.some(param => param.key === 'max_mbps'));
  assert.ok(!effect.params.some(param => param.key === 'scale'));
  assert.equal(defaultConfig('traffic').params.custom_uplink,false);
});

const cases = JSON.parse(await readFile(new URL('./fixtures/live-traffic-frames.json',import.meta.url),'utf8'));
test('Live Traffic preview matches automatic capacities, smoothing, offline and uplink behavior',() => {
  for (const sequence of cases) {
    const states = Array.from({length:4},() => ({}));
    const preciseStates = Array.from({length:4},() => ({}));
    for (const frame of sequence.frames) frame.colors.forEach((expected,index) => {
      const actual = hexToRgb(effectColor(sequence.config,index,4,frame.time,{...frame.facts[index],animationState:states[index]}));
      const precise = rgb(preciseEffectColor(sequence.config,index,4,frame.time,{...frame.facts[index],animationState:preciseStates[index]}));
      hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(channel - actual[i]) <= 1,
        `${sequence.config.params.scale}, port ${index + 1}, t=${frame.time}: ${actual} vs ${expected}`));
      hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(channel - precise[i]) <= 1,
        `Precise preview, port ${index + 1}, t=${frame.time}: ${precise} vs ${expected}`));
    });
  }
});

test('Profile preview combines uplink and default brightness and keeps offline color',() => {
  const params = {...defaultConfig('traffic').params,custom_uplink:true,uplink_color:'#80C0FF',brightness_uplink_color:25,offline_color:'#FF0000'};
  const scene = {brightness:60,zones:[{id:'all',ports:[1,2],effect:{id:'traffic',params}}],overrides:{},rules:[],sfp:{}};
  const state = {ports:[{port:1,facts:{link:true,uplink:true,traffic:1000,speed:1000}},{port:2,facts:{link:false,uplink:true}}]};
  const colors = profileColors(scene,state,0);
  [128,192,255].forEach((channel,i)=>assert.ok(Math.abs(colors[1][i]-channel*0.25*0.6)<0.01));
  assert.deepEqual(colors[2],[153,0,0]);
});
