import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { defaultConfig,effectById,effectColor,setEffectCatalog } from '../src/effects.mjs';

const added = ['aurora','lava_lamp','plasma','ocean_swell','eclipse','meteor_shower','counterflow',
  'color_dissolve','ink_spread','alternating_fade','color_tide','palette_morph','ember_bed','fireflies','traffic_trails'];

test('new effect cards have distinct descriptions and animated preview frames',() => {
  const descriptions = new Set();
  for (const id of added) {
    const effect = effectById(id),config = defaultConfig(id);
    descriptions.add(effect.description);
    assert.notEqual(effect.description,effectById('static').description,id);
    assert.equal(effect.supportsPreview,true,id);
    const frames = [0,1,3,7].map(t => Array.from({length:8},(_,index) => effectColor(config,index,8,t)).join(','));
    assert.ok(new Set(frames).size > 1,`${id} preview changes over time`);
  }
  assert.equal(descriptions.size,added.length);
});

test('future backend effects keep their metadata without a fabricated static preview',() => {
  const definition = {id:'future_effect',name:'New current',description:'A color moves along each port.',category:'data',
    params:[{key:'color',type:'color',default:'#123456'},{key:'background',type:'color',default:'#abcdef'}]};
  try {
    setEffectCatalog([...CATALOG,definition]);
    const effect = effectById(definition.id);
    assert.equal(effect.name,definition.name);
    assert.equal(effect.description,definition.description);
    assert.equal(effect.category,'Data');
    assert.deepEqual(effect.colors,['#123456','#abcdef']);
    assert.equal(effect.supportsPreview,false);
  } finally {setEffectCatalog(CATALOG);}
});

test('backend presentation values with invalid colors do not reach styles',() => {
  const definition = {id:'future" data-extra="bad',name:'<img src=x>',description:'<script>bad()</script>',category:'animated',
    params:[{key:'color',type:'color',default:'red;display:none'}]};
  try {
    setEffectCatalog([...CATALOG,definition]);
    assert.deepEqual(effectById(definition.id).colors,['#98A0AC','#68717D']);
  } finally {setEffectCatalog(CATALOG);}
});

test('unknown identifiers never resolve to inherited presentation properties',() => {
  try {
    setEffectCatalog([...CATALOG,{id:'constructor',name:'Future effect',description:'Its own description.',category:'__proto__',params:[]}]);
    const effect = effectById('constructor');
    assert.equal(effect.supportsPreview,false);
    assert.equal(effect.category,'Other');
    assert.equal(effect.description,'Its own description.');
  } finally {setEffectCatalog(CATALOG);}
});
