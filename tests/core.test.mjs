import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeHex,hexToRgb,hexToHsv,hsvToHex,rgbToHex } from '../src/color.mjs';
import { EFFECTS,LIBRARY,defaultConfig,effectColor,setEffectCatalog,effectById,speedParam,speedParamValue,speedSliderValue,previewPhase,previewColorPhase,resolvedColorParams,paramsAfterChange } from '../src/effects.mjs';
import { CATALOG } from '../src/catalog.mjs';
import { conditionMatches,matchingRule,RULE_RATE_LIMITS,ruleRate,renderLook } from '../src/rules.mjs';

test('color entry accepts shorthand, rejects malformed values, and round-trips RGB',() => {
  assert.equal(normalizeHex(' #f0a '),'#FF00AA');assert.equal(normalizeHex('abcdef'),'#ABCDEF');
  for (const invalid of ['#ffffff00','red','<script>',null]) assert.equal(normalizeHex(invalid),null);
  for (const color of ['#000000','#FFFFFF','#FF0000','#00FF00','#0000FF','#E4BD82','#79DCC0']) assert.equal(hsvToHex(...hexToHsv(color)),color);
  assert.equal(rgbToHex([-20,256,127.7]),'#00FF80');
});

test('the library includes all registered effects and every effect produces valid RGB frames',() => {
  assert.equal(LIBRARY.length,CATALOG.length);assert.equal(new Set(EFFECTS.map(e => e.id)).size,EFFECTS.length);
  for (const e of EFFECTS) for (const t of [0,.1,1.5,10,600]) for (let i = 0;i < 8;i++) assert.match(effectColor(defaultConfig(e.id),i,8,t,{link:true,traffic:100,hour:13}),/^#[\da-f]{6}$/i,`${e.id} @ ${t}`);
});

test('live effect defaults and control limits come from the connected backend',() => {
  const remote = structuredClone(CATALOG);
  const speed = remote.find(e => e.id === 'comet').params.find(p => p.key === 'speed');
  speed.default = 0.75;speed.max = 3;
  try {
    setEffectCatalog(remote);
    assert.equal(defaultConfig('comet').params.speed,0.75);
    assert.equal(effectById('comet').params.find(p => p.key === 'speed').max,3);
  } finally {setEffectCatalog(CATALOG);}
});

test('every animated effect exposes its speed and the speed control preserves its limits',() => {
  const animated = CATALOG.filter(e => !['static','daylight','traffic'].includes(e.id));
  assert.equal(animated.length,CATALOG.length - 3);
  for (const effect of animated) {
    const param = speedParam(effectById(effect.id));
    assert.ok(param,`${effect.id} speed setting`);
    for (const value of [param.min,param.default,param.max]) {
      assert.equal(speedParamValue(param,speedSliderValue(param,value)),value,`${effect.id} preserves speed`);
    }
  }
  const breathing = speedParam(effectById('breathing'));
  assert.equal(speedParamValue(breathing,breathing.max),1,'right end is fastest supported breathing');
  assert.equal(speedParamValue(breathing,breathing.min),30,'left end is slowest breathing');
});

test('every explicit color has a random switch that starts off',() => {
  for (const effect of EFFECTS) {
    const config = defaultConfig(effect.id);
    for (const param of effect.params.filter(p => p.type === 'color')) {
      assert.ok(effect.params.some(p => p.key === param.random_param),`${effect.id} ${param.key}`);
      assert.equal(config.params[param.random_param],false,`${effect.id} ${param.key}`);
    }
  }
});

test('Marquee can randomize its background while its chosen primary stays unchanged',() => {
  const config = defaultConfig('marquee');config.params.color = '#93B7E8';config.params.background = '#172E45';
  config.params = paramsAfterChange(config,'random_background',true);
  const backgrounds = [];
  for (const time of [0,4,8,16]) {
    const params = resolvedColorParams(config,time);
    assert.equal(params.color,'#93B7E8');backgrounds.push(params.background);
  }
  assert.ok(new Set(backgrounds).size > 1);
  config.params = paramsAfterChange(config,'random_background',false);
  assert.equal(resolvedColorParams(config,8).background,'#172E45');
  assert.equal(config.params.color,'#93B7E8');
});

test('generic primary randomization and legacy sparkle or flash colors remain mutually exclusive',() => {
  for (const id of ['starry_night','strobe']) {
    const config = defaultConfig(id),[generic,legacy] = effectById(id).exclusive_params[0];
    config.params.color = '#93B7E8';config.params = paramsAfterChange(config,legacy,true);
    config.params = paramsAfterChange(config,generic,true);
    assert.equal(config.params[legacy],false);assert.equal(config.params[generic],true);
    config.params = paramsAfterChange(config,legacy,true);
    assert.equal(config.params[legacy],true);assert.equal(config.params[generic],false);
    assert.equal(config.params.color,'#93B7E8');
    if (config.params.random_background !== undefined) {
      config.params = paramsAfterChange(config,'random_background',true);
      assert.equal(config.params[legacy],true,'background randomization does not disable the primary source');
    }
  }
});

test('an older backend catalog does not add unsupported random-color parameters',() => {
  const old = structuredClone(CATALOG);
  for (const effect of old) {
    effect.params = effect.params.filter(p => !p.randomizes && p.key !== 'color_transition');
    effect.params.forEach(p => {delete p.random_param;});delete effect.exclusive_params;
  }
  try {
    setEffectCatalog(old);
    const config = defaultConfig('marquee');
    assert.equal(config.params.random_background,undefined);
    assert.equal(config.params.color_transition,undefined);
    assert.deepEqual(Object.keys(config.params).sort(),effectById('marquee').params.map(p => p.key).sort());
  } finally {setEffectCatalog(CATALOG);}
});

test('color-transition edits preserve color phase independently of movement speed',() => {
  const config = defaultConfig('marquee'),motion = {},colors = {};
  config.params.random_background = true;
  const before = previewColorPhase(colors,config,12),position = previewPhase(motion,config,12);
  config.params.color_transition = 2;
  assert.equal(previewColorPhase(colors,config,12),before);
  assert.equal(previewPhase(motion,config,12),position);
  assert.equal(previewColorPhase(colors,config,13),before + .5);
  config.params.speed = 1.25;
  assert.equal(previewColorPhase(colors,config,13),before + .5);
  const a = resolvedColorParams(config,0,.99999).background,b = resolvedColorParams(config,0,1.00001).background;
  assert.ok(hexToRgb(a).every((channel,i) => Math.abs(channel - hexToRgb(b)[i]) <= 1));
});

test('overnight schedules include midnight and exclude their end',() => {
  const rule = {field:'time',op:'between',value:['22:00','07:00']};
  for (const [h,m,expected] of [[21,59,false],[22,0,true],[0,0,true],[6,59,true],[7,0,false]]) {
    const date = new Date(2026,9,29,h,m);assert.equal(conditionMatches(rule,{},date),expected);
  }
});

test('demo speed edits preserve the current animation position and can pause at zero',() => {
  const clock = {},config = defaultConfig('rainbow');
  const before = previewPhase(clock,config,12);
  config.params.speed = .2;
  assert.equal(previewPhase(clock,config,12),before);
  assert.ok(Math.abs(previewPhase(clock,config,13) - before - .2) < 1e-10);
  config.params.speed = 0;
  const stopped = previewPhase(clock,config,14);
  assert.equal(previewPhase(clock,config,50),stopped);
});

test('rainbow stars keep their color through the global preview cycle boundary',() => {
  const config = defaultConfig('starry_night');config.params.rainbow = true;
  const cycle = 1.5 + config.params.fade + 1 / config.params.rate;
  for (let port = 0;port < 8;port++) {
    const a = hexToRgb(effectColor(config,port,8,cycle - .0001)),b = hexToRgb(effectColor(config,port,8,cycle + .0001));
    assert.ok(a.every((channel,i) => Math.abs(channel - b[i]) <= 1));
  }
});

test('rule animation rates honor the conservative ranges and keep flashes visible',() => {
  for (const [style,limits] of Object.entries(RULE_RATE_LIMITS)) {
    assert.equal(ruleRate(style,99),limits.max);assert.equal(ruleRate(style,-1),limits.min);assert.equal(ruleRate(style),limits.default);
  }
  const look = {style:'flash',color:'#FFFFFF',rate:.5};
  assert.equal(renderLook(look,'#000000',.35),'#FFFFFF');
  assert.equal(renderLook(look,'#000000',.5),'#000000');
  assert.equal(renderLook({style:'effect'},'#ABCDEF',1),'#ABCDEF');
});
test('unknown port data never triggers negative or offline conditions',() => {
  const date = new Date();
  assert.equal(conditionMatches({field:'link',op:'is',value:false},{link:null},date),false);
  assert.equal(conditionMatches({field:'poe',op:'is_not',value:'poe'}, {},date),false);
});
test('rule priority, scope, all/any and enabled state are honored',() => {
  const port = {id:2,facts:{link:true,anomaly:true,poe:'poe+'}},date = new Date();
  const online = {id:'online',enabled:true,match:'all',conditions:[{field:'link',op:'is',value:true}]};
  const alert = {id:'alert',enabled:true,match:'all',conditions:[{field:'anomaly',op:'is',value:true}]};
  assert.equal(matchingRule([alert,online],port,date).id,'alert');
  assert.equal(matchingRule([{...alert,ports:[1]},online],port,date).id,'online');
  assert.equal(matchingRule([{...alert,enabled:false},online],port,date).id,'online');
  assert.equal(matchingRule([{...alert,match:'any',conditions:[{field:'link',op:'is',value:false},...alert.conditions]}],port,date).id,'alert');
});
