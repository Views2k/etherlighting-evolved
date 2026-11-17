import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { effectById,LIBRARY } from '../src/effects.mjs';

const added = ['aurora','lava_lamp','plasma','ocean_swell','eclipse','meteor_shower','counterflow',
  'color_dissolve','ink_spread','alternating_fade','color_tide','palette_morph','ember_bed','fireflies','traffic_trails','activity_glow'];

test('every new effect is discoverable with its own metadata and shared color controls',() => {
  assert.equal(CATALOG.length,33);
  for (const id of added) {
    const effect = effectById(id);
    assert.equal(effect.id,id);
    assert.ok(effect.params.some(param => param.key === 'speed'),`${id} has a speed control`);
    for (const param of effect.params.filter(param => param.type === 'color')) {
      assert.ok(effect.params.some(companion => companion.key === param.random_param && companion.randomizes === param.key),`${id} ${param.key} has independent randomization`);
    }
  }
  assert.deepEqual(LIBRARY.filter(effect => effect.category === 'Data').map(effect => effect.id),['traffic','traffic_trails','activity_glow','poe_usage']);
});
