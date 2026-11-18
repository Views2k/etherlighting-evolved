import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { defaultConfig,effectById,effectColor,effectConfigForSelection,paramsAfterChange,shareBreathingPace,
  resolvedColorParams,previewPhase,speedParam,speedParamValue,speedSliderValue,speedText,setEffectCatalog,breathingPeriod } from '../src/effects.mjs';

test('old Breathing scenes remain Custom while a new supported selection uses shared synchronized pace',() => {
  const state = {mode:'live',zones:[{id:'all',ports:[1,2,3,4,5,6,7,8],effect:{id:'gradient',params:{}}}]};
  assert.equal(effectConfigForSelection('breathing',state).params.playback,'custom');
  state.switch = {native_breathing:true};
  assert.equal(effectConfigForSelection('breathing',state).params.playback,'synchronized');
  assert.equal(effectConfigForSelection('breathing',state).params.pace,2);
  const other = defaultConfig('breathing');Object.assign(other.params,{playback:'synchronized',pace:3});
  state.zones.push({id:'other',ports:[8],effect:other});
  assert.equal(effectConfigForSelection('breathing',state).params.pace,3);
  assert.equal(effectConfigForSelection('comet',state).params.playback,undefined);
  const oldCatalog = structuredClone(CATALOG).map(effect => effect.id === 'breathing'
    ? {...effect,params:effect.params.filter(param => !['playback','pace'].includes(param.key))} : effect);
  try {setEffectCatalog(oldCatalog);assert.equal(effectConfigForSelection('breathing',state).params.playback,undefined);}
  finally {setEffectCatalog(CATALOG);}
});

test('playback controls preserve chosen colors and inactive Custom settings',() => {
  const config = defaultConfig('breathing');
  Object.assign(config.params,{color:'#2468AC',brightness_color:35,period:7.5,floor:.25,ripple:true,random_color:true,color_transition:11});
  const before = structuredClone(config.params);
  const zones = [{id:'all',ports:[1,2,3,4,5,6,7,8],effect:{id:'gradient',params:{}}}];
  config.params = paramsAfterChange(config,'playback','synchronized',zones);
  config.params = paramsAfterChange(config,'pace',3,zones);
  config.params = paramsAfterChange(config,'playback','custom');
  assert.deepEqual(config.params,{...before,pace:3});
});

test('all three synchronized speeds increase left to right and use their nominal preview periods',() => {
  const config = defaultConfig('breathing');Object.assign(config.params,{playback:'synchronized',color:'#2468AC',brightness_color:50,random_color:true,ripple:true,floor:.9});
  const param = speedParam(effectById('breathing'),config.params);
  for (const [pace,period,label] of [[1,3.08,'Slow'],[2,2,'Medium'],[3,1,'Fast']]) {
    config.params.pace = pace;
    assert.equal(breathingPeriod(pace),period);assert.equal(speedText(param,pace),label);
    assert.equal(speedParamValue(param,speedSliderValue(param,pace)),pace);
    for (const time of [0,period / 4,period / 2,period * .75,period]) {
      const colors = Array.from({length:8},(_,index) => effectColor(config,index,8,time));
      assert.equal(new Set(colors).size,1);
      assert.equal(resolvedColorParams(config,time).color,'#123456');
      if (time === 0 || time === period) assert.equal(colors[0],'#000000');
      if (time === period / 2) assert.equal(colors[0],'#123456');
    }
  }
});

test('changing shared pace affects synchronized groups and preserves other group settings',() => {
  const a = defaultConfig('breathing'),b = defaultConfig('breathing'),custom = defaultConfig('breathing');
  Object.assign(a.params,{playback:'synchronized',pace:1,color:'#2468AC'});
  Object.assign(b.params,{playback:'synchronized',pace:1,color:'#ABCDEF',brightness_color:35});
  const zones = [{id:'a',effect:a},{id:'b',effect:b},{id:'custom',effect:custom},{id:'gradient',effect:defaultConfig('gradient')}];
  const updated = {...a,params:paramsAfterChange(a,'pace',3,zones)},shared = shareBreathingPace(zones,updated);
  assert.deepEqual(shared.map(zone => zone.effect.params.pace),[3,3,2,undefined]);
  assert.equal(shared[1].effect.params.color,'#ABCDEF');assert.equal(shared[1].effect.params.brightness_color,35);
  assert.equal(zones[0].effect.params.pace,1,'the previous state remains available for recovery');
  const joining = paramsAfterChange(custom,'playback','synchronized',shared);
  assert.equal(joining.pace,3);
  const clock = {},phaseA = previewPhase(clock,shared[0].effect,3.2),phaseB = previewPhase(clock,shared[1].effect,3.2);
  assert.equal(phaseA,phaseB);
});

test('legacy Custom previews retain their timing and random color behavior',() => {
  const config = defaultConfig('breathing');Object.assign(config.params,{period:7.5,floor:.25,ripple:true,random_color:true});
  const legacy = structuredClone(config);delete legacy.params.playback;delete legacy.params.pace;
  for (const time of [0,.125,1.5,7.5,30]) for (let index = 0;index < 8;index++) {
    assert.equal(effectColor(legacy,index,8,time),effectColor(config,index,8,time));
  }
});
