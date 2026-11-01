import test from 'node:test';
import assert from 'node:assert/strict';
import {CATALOG} from '../src/catalog.mjs';
import * as legacy from '../src/effects.mjs';
import * as preview from '../preview/effect-renderer.mjs';
import {galleryConfig} from '../preview/effect-gallery.mjs';

test('slow dim breathing retains distinct brightness samples at 120 Hz', () => {
  const config = preview.defaultConfig('breathing');
  Object.assign(config.params, {playback:'custom',period:20,color:'#102040',brightness_color:20,floor:0,ripple:false});
  const frames = Array.from({length:120}, (_,index) => preview.rgb(preview.effectColor(config,0,8,.4 + index / 120)));
  assert.ok(frames.every(color => color.every(Number.isFinite)));
  assert.ok(frames.some(color => color.some(channel => channel % 1 !== 0)));
  for (let index = 1; index < frames.length; index++) {
    assert.ok(frames[index][2] > frames[index - 1][2]);
    assert.ok(frames[index][2] - frames[index - 1][2] < .05);
  }
  const quantized = new Set(Array.from({length:120}, (_,index) => legacy.effectColor(config,0,8,.4 + index / 120)));
  assert.ok(quantized.size < frames.length / 4);
});

test('all effect previews preserve finite channel precision and leave configuration untouched', () => {
  for (const effect of CATALOG) {
    const config = preview.defaultConfig(effect.id), before = structuredClone(config);
    const animation = Array.from({length:8}, () => ({}));
    for (const time of [0,.1,.5,1,2,5]) for (let port = 0; port < 8; port++) {
      const color = preview.rgb(preview.effectColor(config,port,8,time,{link:true,traffic:150,speed:2500,hour:13,animationState:animation[port]}));
      assert.equal(color.length,3);
      assert.ok(color.every(channel => Number.isFinite(channel) && channel >= 0 && channel <= 255),effect.id);
    }
    assert.deepEqual(config,before,effect.id);
  }
});

test('gallery selection retains Marquee colors, independent brightness and random background', () => {
  const effect = CATALOG.find(item => item.id === 'marquee');
  const params = {...preview.defaultConfig('marquee').params,color:'#6C7BFF',background:'#AF5095',brightness_color:42,brightness_background:63,random_background:true,color_transition:3};
  const state = {zones:[{ports:[1,2],effect:{id:'marquee',params}},{ports:[3],effect:{id:'marquee',params:{color:'#00FF00'}}}]};
  const chosen = structuredClone(galleryConfig(effect,state,[1]));
  assert.deepEqual(chosen.params,params);
  assert.deepEqual(preview.rgb(preview.effectColor(chosen,0,8,1,{phase:0})),preview.dim(params.color,.42));
  assert.deepEqual(preview.rgb(preview.effectColor(chosen,0,8,7,{phase:0})),preview.dim(params.color,.42));
  assert.notDeepEqual(preview.effectColor(chosen,1,8,1,{phase:0}),preview.effectColor(chosen,1,8,7,{phase:0}));
  const draft = {id:'marquee',params:{...params,color:'#123456',brightness_color:28}};
  assert.deepEqual(galleryConfig(effect,state,[1],new Map([['marquee',draft]])).params,params);
  assert.deepEqual(galleryConfig(effect,state,[1],{}, {effect:{...draft,ports:[1]}}).params,draft.params);
  assert.equal(galleryConfig(effect,state,[3]).params.color,'#00FF00');
});

test('preview catalog state is isolated from legacy hex rendering', () => {
  const count = legacy.EFFECTS.length;
  try {
    preview.setEffectCatalog(CATALOG.filter(effect => effect.id === 'static'));
    assert.equal(preview.EFFECTS.length,1);
    assert.equal(legacy.EFFECTS.length,count);
    assert.match(legacy.effectColor(legacy.defaultConfig('breathing'),0,8,.5),/^#[a-f0-9]{6}$/i);
  } finally { preview.setEffectCatalog(CATALOG); }
});

test('one resolved color set per frame preserves every effect sample', () => {
  for (const effect of CATALOG) {
    const config = preview.defaultConfig(effect.id);
    for (const time of [.1,.5,1,2]) {
      const resolved = preview.resolvedColorParams(config,time), before = structuredClone(resolved);
      for (let port = 0; port < 8; port++) {
        const facts = {link:true,traffic:150,speed:2500,hour:13};
        const direct = preview.effectColor(config,port,8,time,{...facts,animationState:{}});
        const cached = preview.effectColor(config,port,8,time,{...facts,animationState:{},resolvedParams:resolved});
        assert.deepEqual(cached,direct,effect.id);
      }
      assert.deepEqual(resolved,before,effect.id);
    }
  }
});
