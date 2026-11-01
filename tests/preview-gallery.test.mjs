import test from 'node:test';
import assert from 'node:assert/strict';
import {CATALOG} from '../src/catalog.mjs';
import {galleryConfig,galleryStatus,sfpGalleryStatus,sfpPreviewColor} from '../preview/effect-gallery.mjs';
import {dim} from '../preview/effect-renderer.mjs';

const effect=CATALOG.find(item=>item.id==='marquee');
const scene=()=>({mode:'running',num_ports:8,brightness:51,zones:[{id:'all',ports:[1,2],effect:{id:'marquee',params:{color:'#4DA6FF',background:'#E472B5'}}}],ports:[{port:1,source:{kind:'zone',id:'all'}},{port:2,source:{kind:'zone',id:'all'}}]});
const near=(actual,expected)=>actual.forEach((channel,index)=>assert.ok(Math.abs(channel-expected[index])<1e-8));

test('applied gallery state follows actual manual and automation ownership',()=>{
  const state=scene();
  assert.equal(galleryStatus(effect,state,[1,2]).label,'Applied');
  state.ports[0].override={color:'#FFFFFF'};
  assert.equal(galleryStatus(effect,state,[1]).label,'Fixed color active');
  assert.deepEqual(galleryStatus(effect,state,[1,2]),{label:'Applied to 1 of 2',selected:false});
  delete state.ports[0].override;state.ports[0].source={kind:'rule',name:'Offline'};
  assert.deepEqual(galleryStatus(effect,state,[1]),{label:'Automation active',selected:false});
  assert.equal(galleryStatus(effect,state,[1,2]).label,'Applied to 1 of 2');
});

test('mixed configuration and remembered palettes never claim current output',()=>{
  const state=scene();
  state.zones[0].ports=[1];state.zones.push({ports:[2],effect:{id:'marquee',params:{color:'#FF0000'}}});
  assert.deepEqual(galleryStatus(effect,state,[1,2]),{label:'Mixed settings',selected:false});
  const remembered=new Map([['marquee',{id:'marquee',params:{color:'#00FF00'}}]]);
  assert.equal(galleryConfig(effect,state,[1],remembered).params.color,'#4DA6FF');
  assert.equal(galleryConfig(effect,state,[2],remembered).params.color,'#FF0000');
  assert.equal(galleryConfig(effect,{zones:[]},[1],remembered).params.color,'#00FF00');
});

test('only an actual pending edit is labeled Previewing',()=>{
  const state=scene(),draft={effect:{id:'marquee',ports:[1,2],params:{color:'#00FF00'}}};
  assert.deepEqual(galleryStatus(effect,state,[1,2],draft),{label:'Previewing',selected:true});
  assert.equal(galleryConfig(effect,state,[1],{},draft).params.color,'#00FF00');
  draft.colors={1:{color:'#0000FF'}};
  assert.equal(galleryStatus(effect,state,[1],draft).label,'Fixed color preview');
  assert.equal(galleryStatus(effect,state,[]).selected,false);
  state.mode='released';assert.equal(galleryStatus(effect,state,[1]).label,'Saved effect');
});

test('parameter-only drafts keep fixed-color ownership until the override is explicitly cleared',()=>{
  const state=scene();state.overrides={1:{color:'#FF0000'}};
  const draft={effect:{id:'marquee',ports:[1,2],params:{color:'#00FF00'},clearOverrides:false}};
  assert.deepEqual(galleryStatus(effect,state,[1],draft),{label:'Fixed color active',selected:false});
  assert.deepEqual(galleryStatus(effect,state,[1,2],draft),{label:'Previewing on 1 port',selected:true});
  draft.effect.clearOverrides=true;
  assert.deepEqual(galleryStatus(effect,state,[1,2],draft),{label:'Previewing',selected:true});
  draft.effect.clearOverrides=false;draft.returnToEffect=[1];
  assert.deepEqual(galleryStatus(effect,state,[1,2],draft),{label:'Previewing',selected:true});
  draft.colors={1:{color:'#0000FF'}};
  assert.equal(galleryStatus(effect,state,[1],draft).label,'Fixed color preview');
});

test('SFP status distinguishes custom output, rules, link control and drafts',()=>{
  const state={mode:'running',num_ports:8,sfp:[{port:9,mode:'color',effect:'pulse'},{port:10,mode:'unmanaged',effect:'pulse'}]};
  assert.equal(sfpGalleryStatus({id:'pulse'},state,[9,10]).label,'Applied to 1 of 2');
  state.sfp[0].rule={id:'alert'};
  assert.deepEqual(sfpGalleryStatus({id:'pulse'},state,[9]),{label:'Automation active',selected:false});
  assert.equal(sfpGalleryStatus({id:'pulse'},state,[10]).label,'');
  assert.equal(sfpGalleryStatus({id:'pulse'},state,[10],{sfp:{10:{mode:'color',effect:'pulse'}}}).label,'Previewing');
  assert.equal(sfpGalleryStatus({id:'pulse'},state,[9,10],{sfp:{10:{mode:'off'}}}).label,'Automation active');
  delete state.sfp[0].rule;state.sfp[0].color='#FF0000';state.sfp[1]={port:10,mode:'color',effect:'pulse',color:'#0000FF'};
  assert.deepEqual(sfpGalleryStatus({id:'pulse'},state,[9,10]),{label:'Mixed settings',selected:false});
});

test('Together gallery drafts follow actual sorted SFP IDs instead of a fixed port number',()=>{
  for(const first of [17,25]){
    const state={mode:'running',num_ports:first-1,sfp_sync:{mode:'together'},
      sfp:[{port:first+1,mode:'unmanaged',effect:'steady'},{port:first,mode:'color',effect:'pulse'}]};
    const both=[first,first+1],effect={id:'pulse'};
    assert.equal(sfpGalleryStatus(effect,state,both).label,'Applied');
    assert.equal(sfpGalleryStatus(effect,state,[first+1],{sfp:{[first]:{period:4}}}).label,'Previewing');
    assert.equal(sfpGalleryStatus(effect,state,both,{sfp:{[first]:{period:4}}}).label,'Previewing');
    assert.equal(sfpGalleryStatus(effect,state,both,{sfp:{[first+1]:{period:4}}}).label,'Applied');
    assert.equal(sfpGalleryStatus(effect,state,[9],{sfp:{9:{mode:'color',effect:'pulse'}}}).label,'');
  }
});

test('legacy SFP previews use each selected color brightness, SFP brightness and default brightness',()=>{
  const settings={color:'#4DA6FF',secondary_color:'#E472B5',color_brightness:40,secondary_brightness:80,brightness:50,period:6};
  const legacy=(effect,time,master)=>sfpPreviewColor(settings,effect,time,master,2.2,false);
  near(legacy('steady',0,51),dim(settings.color,.4*.5*.51));
  near(legacy('pulse',0,51),dim(settings.color,.4*.5*.51));
  near(legacy('pulse',3,51),[0,0,0]);
  near(legacy('two_color',3,51),dim(settings.secondary_color,.8*.5*.51));
  near(legacy('color_cycle',0,0),[0,0,0]);
});

test('slow SFP fade retains fractional samples and continuous cycle boundaries',()=>{
  const settings={color:'#102040',color_brightness:20,period:30};
  const samples=Array.from({length:120},(_,index)=>sfpPreviewColor(settings,'pulse',.4+index/120));
  assert.ok(samples.some(color=>color.some(channel=>channel%1!==0)));
  for(let index=1;index<samples.length;index++)assert.ok(samples[index][2]<samples[index-1][2]);
  for(const effect of ['pulse','two_color','color_cycle']){
    const before=sfpPreviewColor(settings,effect,29.99999),after=sfpPreviewColor(settings,effect,30.00001);
    assert.ok(before.every((channel,index)=>Math.abs(channel-after[index])<.001),effect);
  }
});
