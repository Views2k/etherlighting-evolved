import test from 'node:test';
import assert from 'node:assert/strict';
import {compareScenes,sceneFromState} from '../preview/scene-review.mjs';
import {findPorts,createPortHistory} from '../preview/port-tools.mjs';
import {selectionFits} from '../preview/saved-library.mjs';
import {previewDraftColors} from '../preview/draft-preview.mjs';
import {stageEffectDraft} from '../preview/effect-draft.mjs';
import {setEffectCatalog,defaultConfig} from '../preview/effect-renderer.mjs';
import {CATALOG} from '../src/catalog.mjs';
setEffectCatalog(CATALOG);
const clone=structuredClone;
const zone=(id,ports,color='#ff0000')=>({id,name:id,ports,effect:{id:'static',params:{color}}});
const scene=()=>({version:3,topology:{version:1,copper_ports:[1,2,3,4],sfp_ports:[]},brightness:100,
  zones:[zone('rack',[1,2,3,4])],rules:[],overrides:{},sfp:{},sfp_sync:{mode:'independent',source_port:1}});
const port=(id,traffic=0,up=true,speed=10000)=>({port:id,facts:{link:up,speed,traffic},link:{link_up:up,speed_mbps:speed},
  color:'#ff0000',display_rgb:[255,0,0],source:{kind:'zone',id:'rack'}});
const state=()=>({...scene(),ports:[1,2,3,4].map(id=>port(id)),sfp:[],num_ports:4,connected:true,output:{gamma:1}});
const closeRgb=(actual,expected)=>actual.forEach((channel,index)=>assert.ok(Math.abs(channel-expected[index])<=1,`${actual} vs ${expected}`));

test('scene review reapplies brightness while preserving another client group rename',()=>{
  const base=scene(),current=clone(base),draft=clone(base);current.zones[0].name='Cameras';draft.brightness=81;
  const inputs=clone({base,current,draft}),result=compareScenes(base,current,draft);
  assert.equal(result.conflicts.length,0);assert.equal(result.scene.brightness,81);assert.equal(result.scene.zones[0].name,'Cameras');assert.equal(result.changed,true);
  assert.deepEqual({base,current,draft},inputs);
});

test('scene review exposes overlapping changes and obeys the explicit selected winner',()=>{
  const base=scene(),current=clone(base),draft=clone(base);current.brightness=60;draft.brightness=25;
  const keep=compareScenes(base,current,draft),replace=compareScenes(base,current,draft,{preferDraft:true});
  assert.deepEqual(keep.conflicts.map(c=>c.path),['brightness']);assert.equal(keep.scene.brightness,60);assert.equal(keep.changed,false);
  assert.equal(replace.scene.brightness,25);
});

test('scene review preserves independent additions and detects deletion against editing',()=>{
  const base=scene(),current=clone(base),draft=clone(base);
  base.zones.push(zone('spare',[]));current.zones=clone(base.zones);draft.zones=clone(base.zones);
  current.zones[1].name='Renamed';draft.zones.pop();current.overrides[1]={color:'#00ff00'};draft.overrides[2]={color:'#0000ff'};
  const kept=compareScenes(base,current,draft),removed=compareScenes(base,current,draft,{preferDraft:true});
  assert.deepEqual(kept.conflicts.map(c=>c.path),['zones.spare']);assert.equal(kept.scene.zones.length,2);
  assert.equal(removed.scene.zones.length,1);assert.deepEqual(removed.scene.overrides,{1:{color:'#00ff00'},2:{color:'#0000ff'}});
});

test('scene review treats rule order as one decision instead of combining competing priorities',()=>{
  const base=scene();base.rules=[{id:'first'},{id:'second'}];const current=clone(base),draft=clone(base);
  current.rules.reverse();draft.rules[0].name='Changed';
  const result=compareScenes(base,current,draft);assert.equal(result.conflicts[0].path,'rules');assert.deepEqual(result.scene.rules,current.rules);
});

test('scene snapshot excludes transient telemetry from a draft baseline',()=>{
  const current=state();current.ports[0].controller={clients:[{name:'Device'}]};current.telemetry={sequence:1};current.identify={port:1};
  const snapshot=sceneFromState(current);assert.equal(snapshot.telemetry,undefined);assert.equal(snapshot.identify,undefined);
  assert.deepEqual(snapshot.zones,current.zones);assert.deepEqual(snapshot.topology,current.topology);
});

test('port search finds every associated client even when its displayed label has an assigned name',()=>{
  const current=state();current.ports[0].name='Rack switch';current.ports[0].controller={name:'Desk drop',clients:[
    {name:'Office camera',name_source:'assigned'},{name:'Work Laptop',name_source:'hostname'}]};
  current.sfp=[{port:9,name:'Fiber uplink'}];const before=clone(current);
  assert.deepEqual(findPorts(current,'work LAPTOP').map(p=>p.port),[1]);assert.deepEqual(findPorts(current,'desk drop').map(p=>p.port),[1]);
  assert.deepEqual(findPorts(current,'sfp 9').map(p=>p.port),[9]);assert.deepEqual(findPorts(current,'port 3').map(p=>p.port),[3]);
  assert.equal(findPorts(current,'not present').length,0);assert.equal(findPorts(current,'').length,5);assert.deepEqual(current,before);
});

const sample=(sequence,seconds,{generation='one',traffic=0,up=true,speed=10000}={})=>({connected:true,
  telemetry:{generation,sequence,sampled_at:new Date(Date.UTC(2026,10,4,0,0,seconds)).toISOString(),stale:false},ports:[port(1,traffic,up,speed)],sfp:[]});

test('history keeps missing samples distinct from zero and does not duplicate telemetry frames',()=>{
  const history=createPortHistory();history.update(sample(1,0,{traffic:null}));history.update(sample(2,5,{traffic:0}));history.update(sample(2,5,{traffic:0}));
  assert.deepEqual(history.get(1).samples.map(p=>p.value),[null,0]);assert.equal(history.get(1).events.length,0);
});

test('monitor restarts record a gap without inventing a link-down event',()=>{
  const history=createPortHistory();history.update(sample(1,0));history.update(sample(2,5,{up:false}),true);
  history.update(sample(1,10,{generation:'two',up:false}));
  assert.deepEqual(history.get(1).events.map(e=>e.text),['Monitoring resumed']);
  assert.notEqual(history.get(1).samples[0].segment,history.get(1).samples[1].segment);
  history.update(sample(2,15,{generation:'two',up:true,speed:2500}));assert.match(history.get(1).events[0].text,/Disconnected.*2.5 GbE/);
});

test('connection interruption within one observer generation breaks the trend line',()=>{
  const history=createPortHistory();history.update(sample(1,0));history.update(sample(2,5),true);history.update(sample(3,15));
  assert.equal(history.get(1).events[0].text,'Monitoring resumed');
  assert.notEqual(history.get(1).samples[0].segment,history.get(1).samples[1].segment);
});

test('history bounds samples to the last minute and clears data when the selected switch changes',()=>{
  const history=createPortHistory();for(let i=0;i<30;i++)history.update(sample(i+1,i*5));
  assert.ok(history.get(1).samples.length<=13);history.clear();assert.deepEqual(history.get(1),{samples:[],events:[]});
});

test('saved selections are constrained to the exact copper and optical layout',()=>{
  const current=state(),item={value:{ports:[1,3],topology:current.topology}};
  assert.equal(selectionFits(item,current),true);assert.equal(Boolean(selectionFits(item,{...current,sfp:[{port:9}]})),false);
  assert.equal(Boolean(selectionFits({value:{ports:[1],topology:null}},current)),false);
});

test('splitting a spatial group recomputes colors on ports left behind',()=>{
  const current=state();current.zones[0].effect={id:'gradient',params:{color_a:'#ff0000',color_b:'#0000ff',speed:0}};
  current.ports.forEach((p,i)=>p.display_rgb=[255*(1-i/3),0,255*i/3]);
  const draft=stageEffectDraft({}, {...defaultConfig('static'),ports:[2],clearOverrides:true},'selection');
  const before=clone({current,draft}),colors=previewDraftColors(current,draft,0);
  assert.deepEqual(Object.keys(colors),['1','2','3','4']);closeRgb(colors[3],[128,0,127]);closeRgb(colors[1],[255,0,0]);closeRgb(colors[4],[0,0,255]);
  assert.deepEqual({current,draft},before);
});

test('previewing split geometry preserves fixed color and first matching rule priority',()=>{
  const current=state();current.zones[0].effect={id:'gradient',params:{color_a:'#ff0000',color_b:'#0000ff',speed:0}};
  current.overrides[1]={color:'#00ff00',brightness:60};current.ports[0].override=current.overrides[1];current.ports[0].display_rgb=[0,153,0];
  current.rules=[{id:'rule',enabled:true,ports:[3],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ffffff',brightness:40}}];
  const draft=stageEffectDraft({}, {...defaultConfig('static'),ports:[2],clearOverrides:true},'selection');
  const colors=previewDraftColors(current,draft,0);closeRgb(colors[1],[0,153,0]);closeRgb(colors[3],[102,102,102]);
});

const optical=(id,extra={})=>({port:id,mode:'unmanaged',effect:'steady',color:'#ff0000',brightness:100,color_brightness:100,display_rgb:[255,0,0],facts:{link:true},link:true,...extra});

test('SFP Follow Ethernet previews master dimming despite dormant native Link status',()=>{
  const current=state();current.sfp=[optical(9),optical(10)];current.sfp_sync={mode:'ethernet',source_port:1};
  const colors=previewDraftColors(current,{brightness:50},0);closeRgb(colors[1],[127.5,0,0]);closeRgb(colors[9],colors[1]);closeRgb(colors[10],colors[1]);
});

test('grouped SFP previews brightness using the group source despite dormant native mode',()=>{
  const current=state();current.sfp=[optical(9)];current.zones[0].ports.push(9);
  closeRgb(previewDraftColors(current,{brightness:50},0)[9],[127.5,0,0]);
});

test('SFP rules inherit master brightness unless the rule explicitly pins its level',()=>{
  const current=state();current.sfp=[optical(9,{mode:'color'})];
  current.rules=[{id:'alert',enabled:true,ports:[9],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ff0000'}}];
  closeRgb(previewDraftColors(current,{brightness:50},0)[9],[127.5,0,0]);
  current.rules[0].look.brightness=100;closeRgb(previewDraftColors(current,{brightness:50},0)[9],[255,0,0]);
});


test('reapplying the same effect previews removal of an individual fixed color',()=>{
  const current=state();current.overrides[1]={color:'#00ff00',brightness:100};current.ports[0].override=current.overrides[1];current.ports[0].display_rgb=[0,255,0];
  const draft={effect:{...current.zones[0].effect,ports:[1,2,3,4],clearOverrides:true}};
  const colors=previewDraftColors(current,draft,0);assert.ok(colors[1],'cleared fixed color needs a preview');closeRgb(colors[1],[255,0,0]);
});


test('keeping a concurrently edited group after deletion also preserves valid port membership',()=>{
  const base=scene();base.zones=[zone('rack',[1,2]),zone('spare',[3,4])];
  const current=clone(base),draft=clone(base);current.zones[1].name='Renamed';
  draft.zones=[zone('rack',[1,2,3,4])];draft.brightness=81;
  const kept=compareScenes(base,current,draft),replaced=compareScenes(base,current,draft,{preferDraft:true});
  assert.ok(kept.conflicts.length>0);
  const keptPorts=kept.scene.zones.flatMap(group=>group.ports);
  assert.equal(new Set(keptPorts).size,keptPorts.length,'keeping an overlap must not assign a port twice');
  assert.deepEqual(kept.scene.zones,current.zones);assert.equal(kept.scene.brightness,81);
  assert.deepEqual(replaced.scene.zones,draft.zones);
});
