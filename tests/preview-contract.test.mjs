import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { effectColor,resolvedColorParams,activityPeak } from '../src/effects.mjs';
import { hexToRgb } from '../src/color.mjs';
import { CATALOG } from '../src/catalog.mjs';

const frames = JSON.parse(await readFile(new URL('./fixtures/effect-frames.json',import.meta.url),'utf8'));
test('deterministic browser previews match backend frames across options and port counts',() => {
  const expectedIds = CATALOG.filter(effect => !['starry_night','lightning','traffic','traffic_trails','activity_glow','poe_usage'].includes(effect.id)).map(effect => effect.id);
  assert.deepEqual([...new Set(frames.map(frame => frame.config.id))].sort(),expectedIds.sort());
  for (const frame of frames) frame.colors.forEach((expected,index) => {
    const actual = hexToRgb(effectColor(frame.config,index,frame.count,frame.time,{hour:frame.hour}));
    hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(actual[i] - channel) <= 1,`${frame.config.id} ${JSON.stringify(frame.config.params)} port ${index + 1}/${frame.count} time ${frame.time}: ${actual} vs ${expected}`));
  });
});

const powerSequences = JSON.parse(await readFile(new URL('./fixtures/poe-usage-frames.json',import.meta.url),'utf8'));
test('PoE Usage previews match backend power changes, repeated timestamps and fades', () => {
  assert.deepEqual([...new Set(powerSequences.map(sequence=>sequence.count))],[1,8,16]);
  for (const sequence of powerSequences) {
    const states = Array.from({length:sequence.count},()=>({}));
    for (const frame of sequence.frames) frame.colors.forEach((expected,index)=>{
      const actual = hexToRgb(effectColor(sequence.config,index,sequence.count,frame.time,{...frame.facts[index],animationState:states[index]}));
      hexToRgb(expected).forEach((channel,i)=>assert.ok(Math.abs(actual[i]-channel)<=1,
        `PoE Usage port ${index+1}/${sequence.count} at ${frame.time}: ${actual} vs ${expected}`));
    });
  }
});

const glowSequences = JSON.parse(await readFile(new URL('./fixtures/activity-glow-frames.json',import.meta.url),'utf8'));
test('Activity Glow matches backend ranking, fades, uplink exclusion and time boundaries',() => {
  assert.deepEqual([...new Set(glowSequences.map(sequence => sequence.count))],[1,8,16]);
  for (const sequence of glowSequences) {
    const states = Array.from({length:sequence.count},() => ({}));
    for (const frame of sequence.frames) frame.colors.forEach((expected,index) => {
      const actual = hexToRgb(effectColor(sequence.config,index,sequence.count,frame.time,{...frame.facts[index],peakTraffic:activityPeak(frame.facts),animationState:states[index]}));
      hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(actual[i] - channel) <= 1,`Activity Glow port ${index + 1}/${sequence.count} time ${frame.time}: ${actual} vs ${expected}`));
    });
  }
});

const colorSources = JSON.parse(await readFile(new URL('./fixtures/color-sources.json',import.meta.url),'utf8'));
test('every shared color source matches backend HSV fades without changing fixed parameters',() => {
  assert.equal(new Set(colorSources.map(frame => frame.config.id)).size,CATALOG.filter(effect => effect.params.some(param => param.type === 'color')).length);
  for (const frame of colorSources) {
    const before = structuredClone(frame.config),resolved = resolvedColorParams(frame.config,frame.time);
    for (const [key,expected] of Object.entries(frame.colors)) {
      const actual = hexToRgb(resolved[key]);
      hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(actual[i] - channel) <= 1,`${frame.config.id} ${key} at ${frame.time}: ${actual} vs ${expected}`));
    }
    assert.deepEqual(frame.config,before,'resolving a frame must not overwrite saved fixed colors');
  }
});

const trafficSequences = JSON.parse(await readFile(new URL('./fixtures/traffic-trails-frames.json',import.meta.url),'utf8'));
test('Traffic Trails preview matches backend attack, release, offline and unknown-link frames',() => {
  assert.deepEqual([...new Set(trafficSequences.map(sequence => sequence.count))],[1,8,16]);
  for (const sequence of trafficSequences) {
    const states = Array.from({length:sequence.count},() => ({}));
    for (const frame of sequence.frames) frame.colors.forEach((expected,index) => {
      const actual = hexToRgb(effectColor(sequence.config,index,sequence.count,frame.time,{...frame.facts[index],animationState:states[index]}));
      hexToRgb(expected).forEach((channel,i) => assert.ok(Math.abs(actual[i] - channel) <= 1,`Traffic Trails port ${index + 1}/${sequence.count} time ${frame.time}: ${actual} vs ${expected}`));
    });
  }
});
