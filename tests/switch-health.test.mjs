import test from 'node:test';
import assert from 'node:assert/strict';
import {switchHealth} from '../preview/switch-health.mjs';

test('switch health keeps load average separate from sampled CPU usage', () => {
  const health = switchHealth({connected:true,stats:{commands_per_second:28,command_budget:30,
    ack_p95_ms:2.25,switch_cpu_idle:.82,switch_load:.97,cpu_sample_age_ms:1250}});
  const rows = Object.fromEntries(health.rows);
  assert.equal(rows['CPU usage · last sample'],'18%');
  assert.equal(rows['Load average'],'0.97');
  assert.equal(rows['LED commands'],'28 / 30 per second');
  assert.equal(rows['CPU sample age'],'1.3 s');
  assert.equal(rows['Acknowledgment · 95th percentile'],'2.25 ms');
});

test('missing or invalid measurements are not reported as zero', () => {
  for (const invalid of [undefined,null,-1,NaN,Infinity,'0']) {
    const rows = Object.fromEntries(switchHealth({stats:{switch_cpu_idle:invalid,switch_load:invalid,ack_p95_ms:invalid}}).rows);
    assert.equal(rows['CPU usage · last sample'],'—');
    assert.equal(rows['Load average'],'—');
    assert.equal(rows['Acknowledgment · 95th percentile'],'—');
  }
  assert.equal(Object.fromEntries(switchHealth({stats:{switch_cpu_idle:1,switch_load:0,cpu_sample_age_ms:0}}).rows)['CPU usage · last sample'],'0%');
});

test('a failed CPU sample cannot present retained readings as current', () => {
  const health = switchHealth({connected:true,stats:{switch_cpu_idle:.8,switch_load:.97,cpu_sample_age_ms:null}});
  assert.equal(Object.fromEntries(health.rows)['CPU usage · last sample'],'—');
  assert.equal(Object.fromEntries(health.rows)['Load average'],'—');
  assert.equal(health.status,'Switch readings');
});

test('disconnected and terminal reconnect states never claim live health', () => {
  assert.match(switchHealth({connected:true},true).status,/disconnected/);
  assert.match(switchHealth({connected:false}).status,/Last reported/);
  assert.match(switchHealth({connected:false,reconnect_required:true}).status,/Reconnect/);
});

test('resource rows show reported sensor names, memory and writable storage with units',()=>{
  const rows=Object.fromEntries(switchHealth({connected:true,stats:{health_sample_age_ms:2400,switch_health:{
    temperatures:[{label:'Board',celsius:44.125},{label:'CPU',celsius:52.25}],
    memory:{used_bytes:256*1024**2,total_bytes:1024**3},
    storage:[{mount:'/persistent',used_bytes:128*1024**2,total_bytes:512*1024**2}]
  }}}).rows);
  assert.equal(rows['Temperature · Board'],'44.1 °C');assert.equal(rows['Temperature · CPU'],'52.3 °C');
  assert.equal(rows['Memory used'],'256 MiB / 1 GiB · 25%');assert.equal(rows['Storage · /persistent'],'128 MiB / 512 MiB · 25%');
  assert.equal(rows['Resource sample age'],'2.4 s');
});

test('missing sensors and invalid resource sizes are not invented or shown as zero',()=>{
  const rows=Object.fromEntries(switchHealth({stats:{switch_health:{temperatures:[],memory:{total_bytes:10,used_bytes:20},storage:[]}}}).rows);
  assert.equal(rows.Temperature,'Not reported');assert.equal(rows['Memory used'],'Not reported');assert.equal(rows['Storage used'],'Not reported');
  assert.ok(!Object.hasOwn(rows,'Temperature · CPU'));assert.ok(!Object.hasOwn(rows,'Temperature · Drive'));
  assert.match(switchHealth({connected:true,stats:{health_error:'unavailable'}}).status,/Last|last/);
});
