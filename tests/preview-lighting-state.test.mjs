import test from 'node:test';
import assert from 'node:assert/strict';
import {fixedColorSettings,fixedColorBrightness} from '../preview/lighting-state.mjs';

const state = () => ({brightness:40,ports:[{port:1,color:'#123456'},{port:2,color:'#ABCDEF'}],
  overrides:{2:{color:'#FFEEDD',brightness:65}},sfp_sync:{mode:'independent',source_port:1},
  sfp:[{port:9,color:'#00AAFF',brightness:100,color_brightness:60},
    {port:10,color:'#FF00AA',brightness:50,color_brightness:80}]});

test('mixed fixed-color controls display the SFP color brightness they edit',()=>{
  const current=state(),draft={sfp:{9:{color_brightness:25}}},before=structuredClone({current,draft});
  assert.deepEqual(fixedColorSettings(current,9),{color:'#00AAFF',brightness:60});
  assert.deepEqual(fixedColorSettings(current,9,draft),{color:'#00AAFF',brightness:25});
  Object.assign(current.sfp[0],draft.sfp[9]);
  assert.equal(fixedColorSettings(current,9).brightness,25);
  current.sfp[0].color_brightness=before.current.sfp[0].color_brightness;
  assert.deepEqual({current,draft},before);
});

test('legacy SFP brightness is composed once, including zero and fractional products',()=>{
  const current=state();
  assert.equal(fixedColorSettings(current,10).brightness,40);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{brightness:25,color_brightness:33}}}).brightness,8);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{brightness:0}}}).brightness,0);
  assert.equal(fixedColorSettings(current,10,{sfp:{10:{color_brightness:0}}}).brightness,0);
  assert.equal(fixedColorBrightness(fixedColorSettings(current,10),current),40);
});

test('Together displays the shared SFP setting instead of the dormant indicator setting',()=>{
  const current=state(),draft={sfp_sync:{mode:'together'},sfp:{9:{color:'#112233',color_brightness:30},10:{color_brightness:5}}};
  const before=structuredClone({current,draft});
  assert.deepEqual(fixedColorSettings(current,10,draft),{color:'#112233',brightness:30});
  assert.deepEqual(fixedColorSettings(current,9,draft),fixedColorSettings(current,10,draft));
  assert.deepEqual({current,draft},before);
});

test('copper overrides remain absolute and inherited brightness remains inherited',()=>{
  const current=state(),draft={brightness:20},before=structuredClone(current);
  const inherited=fixedColorSettings(current,1,draft),pinned=fixedColorSettings(current,2,draft);
  assert.deepEqual(inherited,{color:'#123456',brightness:null});
  assert.equal(fixedColorBrightness(inherited,current,draft),20);
  assert.equal(fixedColorBrightness(inherited,current),40);
  assert.equal(fixedColorBrightness(pinned,current,draft),65);
  assert.deepEqual(fixedColorSettings(current,2,{colors:{2:{color:'#001122',brightness:null}}}),{color:'#001122',brightness:null});
  pinned.brightness=1;
  assert.deepEqual(current,before);
});

test('SFP membership follows actual port IDs and no unknown port is fabricated',()=>{
  const current=state();current.sfp[0].port=25;current.sfp[1].port=26;current.sfp_sync.mode='together';
  assert.deepEqual(fixedColorSettings(current,26),{color:'#00AAFF',brightness:60});
  assert.throws(()=>fixedColorSettings(current,9),/available port/);
});
