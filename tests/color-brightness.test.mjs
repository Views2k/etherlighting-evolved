import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { defaultConfig,effectById,effectColor,resolvedColorParams,paramsAfterChange,setEffectCatalog } from '../src/effects.mjs';
import { dim,hexToRgb } from '../src/color.mjs';
import { effectControls,paramText } from '../src/views.mjs';
import { createState,snapshot,applySnapshot } from '../src/state.mjs';

test('every explicit effect color has an independent brightness companion',() => {
  for (const effect of CATALOG) for (const color of effect.params.filter(param => param.type === 'color')) {
    const brightness = effect.params.find(param => param.key === color.brightness_param);
    assert.equal(brightness?.dims,color.key,`${effect.id} ${color.key}`);
    assert.equal(brightness.type,'float');
    assert.equal(brightness.default,100);
    assert.equal(brightness.min,0);
    assert.equal(brightness.max,100);
    assert.equal(brightness.unit,'%');
  }
});

test('primary and background brightness are applied independently before blending',() => {
  const config = defaultConfig('marquee');
  Object.assign(config.params,{color:'#80C040',background:'#204080',spacing:4,
    brightness_color:50,brightness_background:25});
  const before = structuredClone(config),resolved = resolvedColorParams(config,0);
  assert.equal(resolved.color,'#406020');
  assert.equal(resolved.background,'#081020');
  assert.equal(effectColor(config,0,8,0),'#406020');
  assert.equal(effectColor(config,2,8,0),'#081020');
  assert.deepEqual(config,before);
});

test('random colors retain their own brightness without replacing chosen colors',() => {
  for (const effect of CATALOG) for (const color of effect.params.filter(param => param.type === 'color')) {
    const config = defaultConfig(effect.id);
    Object.assign(config.params,{[color.key]:'#2468AC',[color.random_param]:true});
    const before = structuredClone(config),full = resolvedColorParams(config,3.5)[color.key];
    config.params[color.brightness_param] = 37;
    assert.equal(resolvedColorParams(config,3.5)[color.key],dim(full,.37),`${effect.id} ${color.key}`);
    assert.equal(config.params[color.key],before.params[color.key]);
    assert.equal(config.params[color.random_param],true);
    config.params[color.brightness_param] = 0;
    assert.equal(resolvedColorParams(config,3.5)[color.key],'#000000');
  }
});

test('missing brightness values preserve every legacy effect preview exactly',() => {
  for (const effect of CATALOG) {
    const full = defaultConfig(effect.id),legacy = structuredClone(full);
    for (const param of effect.params.filter(param => param.dims)) delete legacy.params[param.key];
    for (const time of [0,.125,1.5,4.7]) for (let port = 0;port < 8;port++) {
      assert.equal(effectColor(legacy,port,8,time),effectColor(full,port,8,time),`${effect.id} port ${port + 1}`);
    }
  }
});

test('legacy random Starry Night and Strobe colors honor primary brightness',() => {
  for (const [id,random] of [['starry_night','rainbow'],['strobe','random_colors']]) {
    const config = defaultConfig(id);
    Object.assign(config.params,{[random]:true,brightness_color:0});
    if ('background' in config.params) config.params.background = '#000000';
    for (const time of [.125,.5,1.5,4.7]) for (let port = 0;port < 8;port++) {
      assert.equal(effectColor(config,port,8,time),'#000000',`${id} port ${port + 1}`);
    }
  }
  const strobe = defaultConfig('strobe');
  Object.assign(strobe.params,{random_colors:true,rate:1});
  const full = effectColor(strobe,0,8,.25);
  strobe.params.brightness_color = 25;
  assert.deepEqual(hexToRgb(effectColor(strobe,0,8,.25)),hexToRgb(dim(full,.25)));
});

test('brightness sits with its color and remains usable while that color is randomized',() => {
  const state = createState();state.config = defaultConfig('marquee');
  Object.assign(state.config.params,{brightness_color:50,brightness_background:25,random_background:true});
  const html = effectControls(state);
  for (const key of ['brightness_color','brightness_background']) {
    assert.equal([...html.matchAll(new RegExp(`id="param-${key}"`,'g'))].length,1);
    const input = html.match(new RegExp(`<input id="param-${key}"[^>]*>`))[0];
    assert.doesNotMatch(input,/disabled|readonly/);
    assert.match(input,/type="range" min="0" max="100" step="1"/);
  }
  assert.ok(html.indexOf('data-param="color"') < html.indexOf('data-param="brightness_color"'));
  assert.ok(html.indexOf('data-param="brightness_color"') < html.indexOf('data-param="background"'));
  assert.match(html,/<input[^>]*data-param="background"[^>]*disabled/);
  assert.match(html,/id="out-brightness_background"[^>]*>25%<\/output>/);
  assert.equal(paramText(effectById('marquee').params.find(param => param.key === 'brightness_color'),50),'50%');
});

test('changing and saving color brightness retains other colors and independent controls',() => {
  const state = createState();state.config = defaultConfig('marquee');
  Object.assign(state.config.params,{color:'#2468AC',random_background:true,brightness_background:70});
  state.config.params = paramsAfterChange(state.config,'brightness_color',25);
  state.zones[0].effect = structuredClone(state.config);
  const restored = createState();applySnapshot(restored,snapshot(state));
  assert.deepEqual(restored.zones[0].effect,state.config);
  assert.equal(restored.zones[0].effect.params.color,'#2468AC');
  assert.equal(restored.zones[0].effect.params.brightness_background,70);
  assert.equal(restored.zones[0].effect.params.random_background,true);
});

test('an older catalog keeps its existing color controls without unsupported brightness fields',() => {
  const older = structuredClone(CATALOG).map(effect => ({...effect,params:effect.params.filter(param => !param.dims)
    .map(({brightness_param,...param}) => param)}));
  try {
    setEffectCatalog(older);
    const state = createState();state.config = defaultConfig('marquee');
    const html = effectControls(state);
    assert.match(html,/data-param="color"/);
    assert.match(html,/data-key="random_background"/);
    assert.doesNotMatch(html,/color-brightness|brightness_color/);
  } finally {setEffectCatalog(CATALOG);}
});
