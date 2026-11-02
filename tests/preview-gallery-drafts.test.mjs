import test from 'node:test';
import assert from 'node:assert/strict';
import {CATALOG} from '../src/catalog.mjs';
import {galleryConfig, galleryStatus} from '../preview/effect-gallery.mjs';

const marquee = CATALOG.find(effect => effect.id === 'marquee');
const breathing = CATALOG.find(effect => effect.id === 'breathing');
const scene = () => ({mode:'running',num_ports:8,brightness:100,
  zones:[{id:'all',ports:[1,2,3],effect:{id:'static',params:{color:'#FFFFFF'}}}],
  ports:[1,2,3].map(port => ({port,source:{kind:'zone',id:'all'}}))});

test('gallery retains the separate effect and color for each draft target', () => {
  const state=scene(),draft={effects:[
    {id:'marquee',ports:[1],params:{color:'#4DA6FF',background:'#E472B5'}},
    {id:'breathing',ports:[2],params:{color:'#C08BFF'}},
  ]};
  assert.equal(galleryConfig(marquee,state,[1],{},draft).params.color,'#4DA6FF');
  assert.equal(galleryConfig(breathing,state,[2],{},draft).params.color,'#C08BFF');
  assert.equal(galleryStatus(marquee,state,[1],draft).label,'Previewing');
  assert.equal(galleryStatus(breathing,state,[2],draft).label,'Previewing');
  assert.equal(galleryStatus(marquee,state,[1,2],draft).label,'Previewing on 1 port');
  assert.equal(galleryStatus(marquee,state,[3],draft).label,'');
});

test('manual color and retained overrides take precedence over an effect in a multi-target draft', () => {
  const state=scene();state.overrides={1:{color:'#FFFFFF'}};
  const draft={effects:[{id:'marquee',ports:[1,2],params:{color:'#4DA6FF'},clearOverrides:false}],colors:{2:{color:'#FF0000'}}};
  assert.equal(galleryStatus(marquee,state,[1],draft).label,'Fixed color active');
  assert.equal(galleryStatus(marquee,state,[2],draft).label,'Fixed color preview');
  draft.effects[0].clearOverrides=true;
  assert.equal(galleryStatus(marquee,state,[1],draft).label,'Previewing');
  assert.equal(galleryStatus(marquee,state,[2],draft).label,'Fixed color preview');
});
