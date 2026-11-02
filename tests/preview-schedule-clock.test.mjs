import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {hostScheduleDate,receiveScheduleClock,scheduleClockLabel} from '../preview/schedule-clock.mjs';
import {conditionMatches} from '../src/rules.mjs';
import {renderEffectGallery} from '../preview/effect-gallery.mjs';
import {CATALOG} from '../src/catalog.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

const state=(local_iso,zone_name='EE host zone')=>({schedule_clock:{local_iso,zone_name}});
const parts=date=>[date.getDay(),date.getHours(),date.getMinutes(),date.getSeconds(),date.getMilliseconds()];

test('host civil time ignores browser offset and advances only by monotonic elapsed time',()=>{
  const current=state('2026-10-31T23:59:58.250-05:00');receiveScheduleClock(current.schedule_clock,1000);
  assert.deepEqual(parts(hostScheduleDate(current,1000)),[6,23,59,58,250]);
  assert.deepEqual(parts(hostScheduleDate(current,3250)),[0,0,0,0,500]);
  assert.deepEqual(parts(hostScheduleDate(current,500)),[6,23,59,58,250]);
});

test('receiving the same clock object again does not reset its anchor',()=>{
  const current=state('2026-10-31T10:00:00+05:30');receiveScheduleClock(current.schedule_clock,0);
  receiveScheduleClock(current.schedule_clock,5000);
  assert.equal(hostScheduleDate(current,5500).getSeconds(),5);
  assert.equal(hostScheduleDate(current,5500).getMilliseconds(),500);
});

test('fresh host samples handle daylight-saving fallback and spring-forward without guessing zone rules',()=>{
  const fallBefore=state('2026-11-01T01:59:59-04:00','Eastern Daylight Time');receiveScheduleClock(fallBefore.schedule_clock,0);
  const fallAfter=state('2026-11-01T01:00:00-05:00','Eastern Standard Time');receiveScheduleClock(fallAfter.schedule_clock,1000);
  assert.deepEqual(parts(hostScheduleDate(fallBefore,0)).slice(0,4),[0,1,59,59]);
  assert.deepEqual(parts(hostScheduleDate(fallAfter,1500)).slice(0,4),[0,1,0,0]);
  assert.equal(scheduleClockLabel(fallAfter),'Eastern Standard Time');
  const spring=state('2026-03-08T03:00:00-04:00');receiveScheduleClock(spring.schedule_clock,0);
  assert.deepEqual(parts(hostScheduleDate(spring,500)).slice(0,4),[0,3,0,0]);
});

test('weekday and overnight conditions cross host midnight, not the browser date',()=>{
  const current=state('2026-10-31T23:59:59-05:00');receiveScheduleClock(current.schedule_clock,0);
  const before=hostScheduleDate(current,0),after=hostScheduleDate(current,1500);
  assert.equal(conditionMatches({field:'weekday',op:'in',value:['sat']},{},before),true);
  assert.equal(conditionMatches({field:'weekday',op:'in',value:['sun']},{},after),true);
  for(const date of [before,after])assert.equal(conditionMatches({field:'time',op:'between',value:['22:00','07:00']},{},date),true);
});

test('missing, offset-free and malformed clocks never claim browser-local schedule time',()=>{
  for(const current of [{},state('2026-10-31T12:00:00'),state('2026-02-30T12:00:00Z'),state('invalid')]){
    assert.ok(Number.isNaN(hostScheduleDate(current).getHours()));assert.equal(scheduleClockLabel(current),'EE computer');
    assert.equal(conditionMatches({field:'time',op:'between',value:['00:00','23:59']},{},hostScheduleDate(current)),false);
  }
});

test('Lighting, profile and ownership previews use the same host schedule in different browser timezones',()=>{
  const url=name=>new URL(`../${name}`,import.meta.url).href;
  const script=`import {readFile} from 'node:fs/promises';import vm from 'node:vm';
    import {hostScheduleDate,receiveScheduleClock} from ${JSON.stringify(url('preview/schedule-clock.mjs'))};
    import {profileColors} from ${JSON.stringify(url('preview/profile-preview.mjs'))};
    import {effectDrafts,effectDraftForPort} from ${JSON.stringify(url('preview/effect-draft.mjs'))};
    import {controlSource} from ${JSON.stringify(url('preview/lighting-state.mjs'))};
    import {matchingRule,matchingSfpRule,renderLook} from ${JSON.stringify(url('src/rules.mjs'))};
    import {defaultConfig,effectColor,previewPhase,previewColorPhase,resolvedColorParams,activityPeak,dim} from ${JSON.stringify(url('preview/effect-renderer.mjs'))};
    const effect={id:'static',params:{color:'#ffffff'}},rule={id:'night',name:'Night',enabled:true,ports:[1],match:'all',conditions:[{field:'time',op:'between',value:['22:00','07:00']}],look:{style:'solid',color:'#ff0000',brightness:100}};
    const state={schedule_clock:{local_iso:'2026-10-31T23:30:00-05:00',zone_name:'Central Daylight Time'},brightness:100,num_ports:1,ports:[{port:1,facts:{},color:'#ffffff'}],zones:[{id:'all',ports:[1],effect}],rules:[rule],overrides:{},sfp:[]};receiveScheduleClock(state.schedule_clock);
    const source=await readFile(new URL(${JSON.stringify(url('preview/app.mjs'))}),'utf8'),start=source.indexOf('function previewDraft(time)'),end=source.indexOf('function speedLegend()',start);
    const context={state,draft:{effect:{...effect,ports:[1],clearOverrides:true}},compareApplied:false,number:Number,effectDrafts,effectDraftForPort,hostScheduleDate,defaultConfig,effectColor,previewPhase,previewColorPhase,resolvedColorParams,activityPeak,dim,matchingRule,matchingSfpRule,renderLook,previewClocks:new Map(),previewStates:new Map(),sfpSync:()=>({mode:'independent'}),sfpDraftPorts:()=>[]};vm.createContext(context);vm.runInContext(source.slice(start,end),context);
    const daylight={...state,zones:[{id:'all',ports:[1],effect:defaultConfig('daylight')}],rules:[]};
    console.log(JSON.stringify({hour:hostScheduleDate(state).getHours(),lighting:context.previewDraft(0),profile:profileColors(state,state,0),source:controlSource(state,1).kind,daylight:profileColors(daylight,state,0)}));`;
  const results=['America/Chicago','Europe/Berlin','Asia/Tokyo'].map(zone=>{
    const result=spawnSync(process.execPath,['--input-type=module','-e',script],{encoding:'utf8',env:{...process.env,TZ:zone}});
    assert.equal(result.status,0,result.stderr);return JSON.parse(result.stdout);
  });
  assert.deepEqual(results[1],results[0]);assert.deepEqual(results[2],results[0]);
  assert.equal(results[0].hour,23);assert.equal(results[0].source,'rule');assert.deepEqual(results[0].lighting[1],[255,0,0]);assert.deepEqual(results[0].profile[1],[255,0,0]);
});

test('Daylight gallery exposes missing clock state and clears it when a host clock arrives',context=>{
  const dom=rendererDom(context),effect=CATALOG.find(item=>item.id==='daylight');
  const current={num_ports:1,ports:[{port:1}],zones:[{id:'all',ports:[1],effect:{id:'daylight',params:{}}}],mode:'running'};
  const cleanup=renderEffectGallery(dom.host,{effects:[effect],state:current,selectedPorts:[1]});dom.after(cleanup);
  const badge=dom.byClass(dom.host,'effect-card-state')[0];assert.equal(badge.textContent,'Waiting for EE clock');
  cleanup.update({state:{...current,...state('2026-10-31T12:00:00-05:00')}});
  assert.equal(badge.textContent,'Applied');
});
