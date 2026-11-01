import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { defaultConfig,effectById,LIBRARY } from '../src/effects.mjs';
import { createState,snapshot,applySnapshot } from '../src/state.mjs';
import { shellView,studioView,effectsView } from '../src/views.mjs';

const added = ['aurora','lava_lamp','plasma','ocean_swell','eclipse','meteor_shower','counterflow',
  'color_dissolve','ink_spread','alternating_fade','color_tide','palette_morph','ember_bed','fireflies','traffic_trails','activity_glow'];
const cards = html => [...html.matchAll(/data-action="effect" data-id="([^"]+)"/g)].map(match => match[1]);

test('every new effect is discoverable with its own metadata and shared color controls',() => {
  assert.equal(CATALOG.length,33);
  for (const id of added) {
    const effect = effectById(id);
    assert.equal(effect.id,id);
    assert.ok(effect.params.some(param => param.key === 'speed'),`${id} has a speed control`);
    for (const param of effect.params.filter(param => param.type === 'color')) {
      assert.ok(effect.params.some(companion => companion.key === param.random_param && companion.randomizes === param.key),`${id} ${param.key} has independent randomization`);
    }
    const state = createState();state.config = defaultConfig(id);
    const page = effectsView(state,'All',effect.name);
    assert.deepEqual(cards(page),[id]);
    assert.ok(page.includes('id="effect-speed"'));
  }
});

test('effect counts and Data filter agree with the complete library',() => {
  const state = createState();
  assert.ok(studioView(state,'#FFFFFF').includes(`All ${LIBRARY.length} effects`));
  assert.deepEqual(cards(effectsView(state)).sort(),LIBRARY.map(effect => effect.id).sort());
  assert.deepEqual(cards(effectsView(state,'Data')),['traffic','traffic_trails','activity_glow','poe_usage']);
  assert.ok(effectsView(state).includes('data-value="Data"'));
});

test('Studio keeps six stable shortcuts when choosing an effect elsewhere in the library',() => {
  const state = createState(),initial = cards(studioView(state,'#FFFFFF'));
  assert.deepEqual(initial,['gradient','breathing','marquee','aurora','lava_lamp','plasma']);
  state.config = defaultConfig('fireflies');
  const selected = studioView(state,'#FFFFFF');
  assert.deepEqual(cards(selected),initial);
  assert.ok(selected.includes('<h2>Fireflies</h2>'));
  assert.ok(selected.includes(`All ${LIBRARY.length} effects`));
});

test('new effect scenes retain chosen and independently randomized colors',() => {
  for (const id of added) {
    const state = createState(),effect = effectById(id);
    state.config = defaultConfig(id);
    for (const param of effect.params.filter(param => param.type === 'color')) {
      state.config.params[param.key] = '#2468AC';
      state.config.params[param.random_param] = true;
    }
    state.zones[0].effect = structuredClone(state.config);
    const saved = snapshot(state),restored = createState();
    applySnapshot(restored,saved);
    assert.deepEqual(restored.zones[0].effect,state.config,id);
  }
});
