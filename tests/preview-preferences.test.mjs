import test from 'node:test';
import assert from 'node:assert/strict';
import {createPreferenceStore} from '../preview/preferences.mjs';

function memoryStorage(initial=null){let data=initial;return{getItem:()=>data,setItem:(_,value)=>{data=value;},value:()=>data};}

test('favorite colors and effects persist independently across store instances',()=>{
  const storage=memoryStorage(),first=createPreferenceStore(storage);
  assert.deepEqual(first.toggleFavoriteColor('#4da6ff'),['#4DA6FF']);
  assert.deepEqual(first.toggleFavoriteEffect('marquee'),['marquee']);
  first.toggleFavoriteEffect('sfp:pulse');
  const second=createPreferenceStore(storage);
  assert.deepEqual(second.getFavoriteColors(),['#4DA6FF']);
  assert.deepEqual(second.getFavoriteEffects(),['marquee','sfp:pulse']);
  second.toggleFavoriteColor('#4DA6FF');assert.deepEqual(first.getFavoriteColors(),[]);
});

test('recent effects are deduplicated, most recent first and bounded',()=>{
  const store=createPreferenceStore(memoryStorage());
  for(let index=0;index<20;index++)store.recordRecentEffect(`effect_${index}`);
  store.recordRecentEffect('effect_12');
  assert.equal(store.getRecentEffects().length,12);
  assert.equal(store.getRecentEffects()[0],'effect_12');
  assert.equal(store.getRecentEffects().filter(id=>id==='effect_12').length,1);
});

test('palettes support named replacement and deletion with validated colors',()=>{
  const store=createPreferenceStore(memoryStorage());
  store.savePalette(' Evening ',['#4da6ff','#E472B5','#4DA6FF','wrong']);
  assert.deepEqual(store.getPalettes(),[{name:'Evening',colors:['#4DA6FF','#E472B5']}]);
  store.savePalette('evening',['#102040']);
  assert.deepEqual(store.getPalettes(),[{name:'evening',colors:['#102040']}]);
  store.deletePalette('evening');assert.deepEqual(store.getPalettes(),[]);
  assert.throws(()=>store.savePalette('',[]),/palette name/);
});

test('corrupt or unrecognized data cannot introduce settings beyond local preferences',()=>{
  for(const raw of ['not json','null','[]'])assert.deepEqual(createPreferenceStore(memoryStorage(raw)).getFavoriteEffects(),[]);
  const storage=memoryStorage(JSON.stringify({favoriteEffects:['marquee','marquee',{},'bad id'],favoriteColors:['#abcdef','invalid'],password:'must not persist',palettes:[null,{name:'OK',colors:['#123456']}]}));
  const store=createPreferenceStore(storage);store.recordRecentEffect('comet');
  const result=JSON.parse(storage.value());
  assert.equal(Object.hasOwn(result,'password'),false);
  assert.deepEqual(result.favoriteEffects,['marquee']);
  assert.deepEqual(result.favoriteColors,['#ABCDEF']);
  assert.throws(()=>store.toggleFavoriteEffect('../path'),/Invalid/);
  assert.throws(()=>store.toggleFavoriteColor('red'),/Invalid/);
});

test('storage failure is actionable and never reports a persisted change',()=>{
  const store=createPreferenceStore({getItem:()=>null,setItem:()=>{throw new Error('Denied');}});
  assert.throws(()=>store.toggleFavoriteEffect('marquee'),/Could not save preferences/);
  assert.deepEqual(store.getFavoriteEffects(),[]);
});

test('full favorites and palette collections reject additions without dropping existing items',()=>{
  const store=createPreferenceStore(memoryStorage());
  for(let index=0;index<24;index++)store.toggleFavoriteColor('#'+index.toString(16).padStart(6,'0'));
  const before=store.getFavoriteColors();
  assert.throws(()=>store.toggleFavoriteColor('#FF0000'),/Remove a favorite/);
  assert.deepEqual(store.getFavoriteColors(),before);
  for(let index=0;index<20;index++)store.savePalette(`Palette ${index}`,['#123456']);
  assert.throws(()=>store.savePalette('Another',['#FFFFFF']),/Remove a palette/);
  store.savePalette('Palette 0',['#FFFFFF']);
  assert.equal(store.getPalettes().length,20);
});
