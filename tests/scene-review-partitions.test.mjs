import test from 'node:test';
import assert from 'node:assert/strict';
import {compareScenes} from '../preview/scene-review.mjs';

const copy=structuredClone;
const group=(id,ports,effect={id:'static',params:{color:'#ff0000',brightness_color:100}})=>({id,name:id,ports,effect});
const baseScene=()=>({version:3,topology:{version:1,copper_ports:[1,2,3,4],sfp_ports:[]},brightness:100,
  zones:[group('all',[1,2,3,4])],rules:[],overrides:{},sfp:{},sfp_sync:{mode:'independent',source_port:1}});
function assertPartition(scene){
  const ports=scene.zones.flatMap(zone=>zone.ports);
  assert.equal(new Set(ports).size,ports.length,'each port must belong to at most one group');
  assert.deepEqual([...ports].sort((a,b)=>a-b),[1,2,3,4],'these fixtures retain all four assigned ports');
}

for(const preferDraft of [false,true]){
  test(`concurrent splits preserve one valid group partition with preferDraft=${preferDraft}`,()=>{
    const base=baseScene(),current=copy(base),draft=copy(base);
    current.zones=[group('all',[2,3,4]),group('client-a',[1])];
    draft.zones=[group('all',[1,3,4]),group('client-b',[2])];draft.brightness=42;
    const inputs=copy({base,current,draft}),result=compareScenes(base,current,draft,{preferDraft});
    assertPartition(result.scene);assert.ok(result.conflicts.length);
    assert.deepEqual(result.scene.zones,preferDraft?draft.zones:current.zones);
    assert.equal(result.scene.brightness,42);assert.deepEqual({base,current,draft},inputs);
  });

  test(`consolidation against renamed deleted group keeps a valid partition with preferDraft=${preferDraft}`,()=>{
    const base=baseScene();base.zones=[group('left',[1,2]),group('right',[3,4])];
    const current=copy(base),draft=copy(base);
    current.zones[1].name='Camera ports';draft.zones=[group('left',[1,2,3,4])];draft.brightness=61;
    const result=compareScenes(base,current,draft,{preferDraft});
    assertPartition(result.scene);assert.ok(result.conflicts.length);
    assert.deepEqual(result.scene.zones,preferDraft?draft.zones:current.zones);
    assert.equal(result.scene.brightness,61);
  });

  test(`renamed group against concurrent consolidation keeps a valid partition with preferDraft=${preferDraft}`,()=>{
    const base=baseScene();base.zones=[group('left',[1,2]),group('right',[3,4])];
    const current=copy(base),draft=copy(base);
    current.zones=[group('left',[1,2,3,4])];draft.zones[1].name='Camera ports';current.brightness=57;
    const result=compareScenes(base,current,draft,{preferDraft});
    assertPartition(result.scene);assert.ok(result.conflicts.length);
    assert.deepEqual(result.scene.zones,preferDraft?draft.zones:current.zones);
    assert.equal(result.scene.brightness,57);
  });

  test(`changing effect type cannot combine parameters from different effects with preferDraft=${preferDraft}`,()=>{
    const base=baseScene(),current=copy(base),draft=copy(base);
    current.zones[0].effect={id:'rainbow',params:{speed:1}};
    draft.zones[0].effect.params.color='#0000ff';draft.brightness=39;
    const result=compareScenes(base,current,draft,{preferDraft});
    assert.ok(result.conflicts.length);
    assert.deepEqual(result.scene.zones[0].effect,preferDraft?draft.zones[0].effect:current.zones[0].effect);
    assert.equal(result.scene.brightness,39);assertPartition(result.scene);
  });

  test(`draft effect type and remote parameter edit remain one choice with preferDraft=${preferDraft}`,()=>{
    const base=baseScene(),current=copy(base),draft=copy(base);
    current.zones[0].effect.params.color='#00ff00';
    draft.zones[0].effect={id:'rainbow',params:{speed:1}};
    const result=compareScenes(base,current,draft,{preferDraft});
    assert.ok(result.conflicts.length);
    assert.deepEqual(result.scene.zones[0].effect,preferDraft?draft.zones[0].effect:current.zones[0].effect);
  });
}

test('same-effect disjoint parameter edits and a rename still merge without a conflict',()=>{
  const base=baseScene(),current=copy(base),draft=copy(base);
  current.zones[0].name='Rack';current.zones[0].effect.params.color='#00ff00';
  draft.zones[0].effect.params.brightness_color=33;
  const result=compareScenes(base,current,draft);
  assert.equal(result.conflicts.length,0);assert.equal(result.scene.zones[0].name,'Rack');
  assert.deepEqual(result.scene.zones[0].effect,{id:'static',params:{color:'#00ff00',brightness_color:33}});
  assertPartition(result.scene);
});


test('valid special group IDs remain data during a recursive merge',()=>{
  const base=baseScene();base.zones[0].id='__proto__';
  const current=copy(base),draft=copy(base);
  current.zones[0].name='Renamed rack';draft.zones[0].effect.params.color='#0000ff';
  const result=compareScenes(base,current,draft);
  assert.equal(result.conflicts.length,0);assert.equal(result.scene.zones.length,1);
  assert.equal(result.scene.zones[0].id,'__proto__');assert.equal(result.scene.zones[0].name,'Renamed rack');
  assert.equal(result.scene.zones[0].effect.params.color,'#0000ff');assertPartition(result.scene);
});
