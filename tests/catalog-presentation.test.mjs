import test from 'node:test';
import assert from 'node:assert/strict';
import { CATALOG } from '../src/catalog.mjs';
import { defaultConfig,effectById,effectColor,setEffectCatalog } from '../src/effects.mjs';
import { createState } from '../src/state.mjs';
import { effectCard } from '../src/views.mjs';
import { groupEditor } from '../src/group-views.mjs';

const added = ['aurora','lava_lamp','plasma','ocean_swell','eclipse','meteor_shower','counterflow',
  'color_dissolve','ink_spread','alternating_fade','color_tide','palette_morph','ember_bed','fireflies','traffic_trails'];

test('new effect cards have distinct descriptions and animated preview frames',() => {
  const state = createState(),descriptions = new Set();
  for (const id of added) {
    const effect = effectById(id),config = defaultConfig(id);
    descriptions.add(effect.description);
    assert.notEqual(effect.description,effectById('static').description,id);
    assert.equal(effect.supportsPreview,true,id);
    assert.match(effectCard(effect,state),new RegExp(`data-effect-preview="${id}"`));
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
    const effect = effectById(definition.id),html = effectCard(effect,createState());
    assert.equal(effect.name,definition.name);
    assert.equal(effect.description,definition.description);
    assert.equal(effect.category,'Data');
    assert.deepEqual(effect.colors,['#123456','#abcdef']);
    assert.equal(effect.supportsPreview,false);
    assert.doesNotMatch(html,/data-effect-preview|Display a single color/);
    assert.match(html,/A color moves along each port\./);
  } finally {setEffectCatalog(CATALOG);}
});

test('backend presentation values are escaped and invalid colors do not reach styles',() => {
  const definition = {id:'future" data-extra="bad',name:'<img src=x>',description:'<script>bad()</script>',category:'animated',
    params:[{key:'color',type:'color',default:'red;display:none'}]};
  try {
    setEffectCatalog([...CATALOG,definition]);
    const effect = effectById(definition.id),state = createState(),card = effectCard(effect,state);
    assert.deepEqual(effect.colors,['#98A0AC','#68717D']);
    assert.match(card,/&lt;img src=x&gt;/);
    assert.match(card,/&lt;script&gt;bad\(\)&lt;\/script&gt;/);
    assert.doesNotMatch(card,/<img|<script|display:none|data-extra="bad/);
    const editor = groupEditor(state);
    assert.match(editor,/&lt;img src=x&gt;/);
    assert.doesNotMatch(editor,/<img|data-extra="bad/);
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
