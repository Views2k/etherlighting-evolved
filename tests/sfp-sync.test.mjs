import test from 'node:test';
import assert from 'node:assert/strict';
import {sfpSync, sfpLight, effectiveSfpLight, editableSfpLight, sfpEditTargets, sfpDraftPorts} from '../preview/sfp-sync.mjs';
import {lightingOperations, applyLightingPlan, controlSource, sfpSettings} from '../preview/lighting-state.mjs';
import {sceneFingerprint, matchingProfile} from '../preview/secondary-model.mjs';
import {sfpGalleryStatus, sfpPreviewColor} from '../preview/effect-gallery.mjs';

const scene = () => ({
  num_ports:8, mode:'running', brightness:80, rules:[], zones:[], overrides:{},
  sfp_sync:{mode:'independent',source_port:1},
  sfp:[
    {port:9,mode:'color',effect:'two_color',color:'#80C0FF',secondary_color:'#EF78C7',brightness:50,color_brightness:80,secondary_brightness:40,period:8,link:true,anomaly:false,facts:{link:true,anomaly:false},rule:null},
    {port:10,mode:'color',effect:'pulse',color:'#FFD090',secondary_color:'#336699',brightness:75,color_brightness:60,secondary_brightness:20,period:12,link:false,anomaly:true,facts:{link:false,anomaly:true},rule:null},
  ],
});
const alertRule = () => ({id:'sfp-alert',name:'SFP alert',enabled:true,ports:[10],match:'all',conditions:[{field:'anomaly',op:'is',value:true}],look:{style:'solid',color:'#FF0000',brightness:50}});
const near = (actual, expected, tolerance=1e-8) => actual.forEach((channel,index) => assert.ok(Math.abs(channel-expected[index])<=tolerance));

test('older scenes default to independent lights and drafts merge only their chosen sync fields', () => {
  assert.deepEqual(sfpSync(), {mode:'independent',source_port:1});
  const state=scene(); state.sfp_sync={mode:'ethernet',source_port:5};
  const draft={sfp_sync:{source_port:3}}, before=structuredClone({state,draft});
  assert.deepEqual(sfpSync(state,draft), {mode:'ethernet',source_port:3});
  assert.deepEqual({state,draft}, before);
});

test('Together uses port 9 lighting settings while retaining each port’s own telemetry and rule', () => {
  const state=scene(); state.sfp_sync.mode='together';
  state.sfp[1].rule={id:'sfp-alert',name:'SFP alert'};
  const before=structuredClone(state), light=effectiveSfpLight(state,10);
  assert.deepEqual(sfpSettings(light), sfpSettings(state.sfp[0]));
  assert.equal(light.port,10);
  assert.deepEqual(light.rule,state.sfp[1].rule);
  assert.deepEqual(light.facts,state.sfp[1].facts);
  assert.deepEqual(light.link,state.sfp[1].link);
  assert.deepEqual(state,before);
});

test('switching draft sync modes restores saved independent port 10 settings without mutating the scene', () => {
  const state=scene(), before=structuredClone(state);
  const draft={sfp_sync:{mode:'together'},sfp:{9:{color:'#123456',period:4}}};
  assert.equal(effectiveSfpLight(state,10,draft).color,'#123456');
  draft.sfp_sync.mode='independent';
  assert.deepEqual(sfpSettings(effectiveSfpLight(state,10,draft)),sfpSettings(state.sfp[1]));
  assert.equal(effectiveSfpLight(state,9,draft).color,'#123456');
  assert.deepEqual(state,before);
});

test('saved-profile object maps and live arrays resolve the same SFP settings', () => {
  const live=scene(), saved={...structuredClone(live),sfp:Object.fromEntries(live.sfp.map(light=>[light.port,sfpSettings(light)]))};
  saved.sfp_sync.mode=live.sfp_sync.mode='together';
  for(const port of [9,10]) assert.deepEqual(sfpSettings(effectiveSfpLight(saved,port)),sfpSettings(effectiveSfpLight(live,port)));
  assert.deepEqual(sfpLight(saved,10),{...sfpSettings(live.sfp[1]),port:10});
});

test('Together edits target only the shared light and preserve the independent companion configuration', () => {
  const state=scene(), draft={sfp_sync:{mode:'together'}};
  assert.deepEqual(sfpEditTargets(state,[10],draft),[9]);
  assert.deepEqual(sfpEditTargets(state,[9,10],draft),[9]);
  assert.deepEqual(sfpEditTargets(state,[10],{sfp_sync:{mode:'independent'}}),[10]);
  const copied=structuredClone(state.sfp[1]);
  draft.sfp={};
  for(const port of sfpEditTargets(state,[9,10],draft)) draft.sfp[port]={...sfpSettings(editableSfpLight(state,port,draft)),color:'#123456'};
  assert.equal(Object.hasOwn(draft.sfp,10),false);
  assert.deepEqual(state.sfp[1],copied);
});

test('empty or Ethernet-only selection never stages SFP output in any sync mode', () => {
  for(const mode of ['independent','together','ethernet']){
    const state=scene();state.sfp_sync.mode=mode;
    for(const selection of [[],[1,2,3,4,5,6,7,8],[11],['9']])assert.deepEqual(sfpEditTargets(state,selection),[],`${mode}: ${selection}`);
    assert.deepEqual(sfpEditTargets(state,[1,10,10]),mode==='together'?[9]:[10]);
    const draft={colors:{1:{color:'#1234ff',brightness:100}},sfp:{}};
    for(const port of sfpEditTargets(state,[],draft))draft.sfp[port]={mode:'color',color:'#1234ff'};
    assert.deepEqual(lightingOperations(state,draft).map(operation=>operation.path),['/api/ports']);
  }
});

test('shared SFP targets and preview settings use reported IDs on larger port maps', () => {
  for(const [num_ports,ids] of [[16,[17,18]],[24,[25,26,27,28]]]){
    const state=scene();state.num_ports=num_ports;state.sfp_sync.mode='together';
    state.sfp=ids.map((port,index)=>({...structuredClone(state.sfp[index%2]),port})).reverse();
    const original=structuredClone(state),first=state.sfp.find(light=>light.port===ids[0]);
    for(const id of ids){
      const light=effectiveSfpLight(state,id);
      assert.deepEqual(sfpSettings(light),sfpSettings(first));
      assert.equal(light.port,id);
      assert.deepEqual(light.facts,state.sfp.find(item=>item.port===id).facts);
    }
    assert.deepEqual(sfpEditTargets(state,[ids.at(-1)]),[ids[0]]);
    const draft={sfp:{[ids[0]]:{color:'#123456'}}};
    assert.deepEqual(sfpDraftPorts(state,draft),ids);
    assert.equal(effectiveSfpLight(state,ids.at(-1),draft).color,'#123456');
    assert.deepEqual(sfpDraftPorts(state,{sfp_sync:{mode:'together'}}),ids);
    assert.deepEqual(sfpDraftPorts(state,{sfp:{9:{color:'#abcdef'}}}),[]);
    assert.deepEqual(state,original);
    const saved={...state,sfp:Object.fromEntries(state.sfp.map(light=>[light.port,sfpSettings(light)]))};
    assert.deepEqual(sfpEditTargets(saved,[ids.at(-1)]),[ids[0]]);
    assert.equal(effectiveSfpLight(saved,ids.at(-1),draft).color,'#123456');
    assert.deepEqual(sfpDraftPorts(saved,draft),ids);
  }
});

test('SFP preview scope follows actual synchronized or independently edited indicators', () => {
  const state=scene();state.sfp=[{...state.sfp[0],port:25},{...state.sfp[1],port:26}];
  assert.deepEqual(sfpDraftPorts(state,{sfp:{9:{color:'#ffffff'},26:{color:'#abcdef'}}}),[26]);
  state.sfp_sync={mode:'ethernet',source_port:3};
  assert.deepEqual(sfpDraftPorts(state,{colors:{3:{color:'#123456'}}}),[25,26]);
  assert.deepEqual(sfpDraftPorts(state,{effect:{ports:[2,3]}}),[25,26]);
  assert.deepEqual(sfpDraftPorts(state,{returnToEffect:[3]}),[25,26]);
  assert.deepEqual(sfpDraftPorts(state,{colors:{2:{color:'#123456'}}}),[]);
});

test('missing SFP indicators never synthesize ports from sync settings or drafts', () => {
  for(const sfp of [[],{},undefined]){
    const state={sfp,sfp_sync:{mode:'together',source_port:1}};
    assert.deepEqual(sfpEditTargets(state,[9,10]),[]);
    assert.deepEqual(sfpDraftPorts(state,{sfp_sync:{mode:'together'},sfp:{9:{color:'#ffffff'}}}),[]);
  }
});

test('editing flattens the legacy SFP brightness into both colors once', () => {
  const state=scene(), before=structuredClone(state), editable=editableSfpLight(state,9);
  assert.equal(editable.brightness,100);
  assert.equal(editable.color_brightness,40);
  assert.equal(editable.secondary_brightness,20);
  for(const time of [0,1,2,4,6]) near(sfpPreviewColor(editable,'two_color',time,state.brightness),sfpPreviewColor(state.sfp[0],'two_color',time,state.brightness));
  const draft={sfp:{9:{...sfpSettings(editable),color:'#234567'}}};
  assert.deepEqual(sfpSettings(editableSfpLight(state,9,draft)),draft.sfp[9]);
  assert.deepEqual(state,before);
});

test('legacy brightness conversion is bounded by one half percentage point and handles zero', () => {
  const state=scene();
  Object.assign(state.sfp[0],{brightness:33,color_brightness:37,secondary_brightness:89});
  const editable=editableSfpLight(state,9);
  assert.equal(editable.color_brightness,12);
  assert.equal(editable.secondary_brightness,29);
  assert.ok(Math.abs(editable.color_brightness-37*.33)<=.5);
  assert.ok(Math.abs(editable.secondary_brightness-89*.33)<=.5);
  state.sfp[0].brightness=0;
  assert.equal(editableSfpLight(state,9).color_brightness,0);
  assert.equal(editableSfpLight(state,9).secondary_brightness,0);
});

test('Apply prepares shared settings before enabling Together and retries only the unconfirmed sync', async () => {
  const state=scene(), draft={sfp:{9:{color:'#123456',brightness:100,color_brightness:40}},sfp_sync:{mode:'together'}};
  const before=structuredClone({state,draft}), plan=lightingOperations(state,draft), sent=[];
  assert.deepEqual(plan.map(item=>item.path),['/api/sfp/9','/api/sfp/sync']);
  assert.deepEqual(plan[1].body,{mode:'together',source_port:1});
  assert.deepEqual({state,draft},before);
  await assert.rejects(applyLightingPlan(plan,async path=>{sent.push(path);if(path==='/api/sfp/sync')throw new Error('Interrupted');}),/Interrupted/);
  assert.deepEqual(plan.map(item=>item.path),['/api/sfp/sync']);
  await applyLightingPlan(plan,async path=>{sent.push(path);});
  assert.deepEqual(sent,['/api/sfp/9','/api/sfp/sync','/api/sfp/sync']);
  assert.equal(plan.length,0);
});

test('invalid sync mode, source type, range and unknown fields are rejected before any request', () => {
  const state=scene();
  for(const sfp_sync of [{mode:'invalid'},{mode:null},{source_port:0},{source_port:9},{source_port:'1'},{source_port:true},{source_port:1.5},{mode:'together',extra:true},[],'together']) {
    assert.throws(()=>lightingOperations(state,{sfp_sync}),undefined,JSON.stringify(sfp_sync));
  }
});

test('saved profiles distinguish Follow Ethernet source changes and retain sync after JSON round-trip', () => {
  const state=scene(); state.sfp_sync={mode:'ethernet',source_port:3};
  const profile=JSON.parse(JSON.stringify(state));
  assert.equal(matchingProfile({Evening:profile},state),'Evening');
  state.sfp_sync.source_port=5;
  assert.notEqual(sceneFingerprint(profile),sceneFingerprint(state));
  assert.equal(matchingProfile({Evening:profile},state),null);
  delete profile.sfp_sync;
  assert.equal(sceneFingerprint(profile),sceneFingerprint({...profile,sfp_sync:{mode:'independent',source_port:1}}));
});

test('gallery statuses describe shared, staged and followed output without claiming the inactive effect', () => {
  const state=scene(); state.sfp_sync.mode='together';
  assert.deepEqual(sfpGalleryStatus({id:'two_color'},state,[9,10]),{label:'Applied',selected:true});
  assert.deepEqual(sfpGalleryStatus({id:'pulse'},state,[9,10]),{label:'',selected:false});
  state.sfp[1].rule={id:'sfp-alert',name:'SFP alert'};
  assert.deepEqual(sfpGalleryStatus({id:'two_color'},state,[9,10]),{label:'Applied to 1 of 2',selected:false});
  const draft={sfp:{9:{effect:'pulse'}}};
  assert.deepEqual(sfpGalleryStatus({id:'pulse'},state,[9,10],draft),{label:'Previewing',selected:true});
  state.sfp_sync={mode:'ethernet',source_port:3};
  assert.deepEqual(sfpGalleryStatus({id:'two_color'},state,[9,10]),{label:'',selected:false});
});

test('shared Off suppresses fallback rules just as the backend does, while Follow Ethernet permits them', () => {
  const state=scene(); state.sfp_sync.mode='together'; state.sfp[0].mode='off';
  delete state.sfp[1].rule;
  state.rules=[alertRule()];
  assert.equal(controlSource(state,10).kind,'off');
  state.sfp_sync={mode:'ethernet',source_port:3};
  assert.equal(controlSource(state,10).kind,'rule');
  state.sfp[1].mode='off';
  assert.equal(controlSource(state,10).kind,'rule');
  state.sfp[1].rule=null;
  assert.deepEqual(controlSource(state,10),{kind:'sync',label:'Following port 3'});
});
