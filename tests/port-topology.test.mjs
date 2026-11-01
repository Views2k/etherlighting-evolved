import test from 'node:test';
import assert from 'node:assert/strict';
import {lightingPorts, sceneTopology, previewGrid, portRanges} from '../preview/port-topology.mjs';
import {portScopeOptions} from '../preview/port-map.mjs';

test('reported lighting IDs take precedence over physical port metadata', () => {
  const state = {num_ports:24, ports:[{port:3},{port:1}], sfp:[],
    hardware_profile:{copper_ports:[1,2,3,4], optical_ports:[25,26], stack_ports:[27,28]}};
  assert.deepEqual(lightingPorts(state), {copper:[1,3], sfp:[]});
  assert.deepEqual(lightingPorts({hardware_profile:state.hardware_profile}), {copper:[], sfp:[]});
});

test('optical-only switches do not acquire eight imaginary Ethernet ports', () => {
  const state = {num_ports:0, sfp_ports:Array.from({length:32}, (_,i) => i+1)};
  assert.deepEqual(lightingPorts(state), {copper:[], sfp:state.sfp_ports});
  const scopes = portScopeOptions(state);
  assert.deepEqual(scopes.map(scope => scope.label), ['All ports']);
  assert.equal(scopes[0].ports.length, 32);
});

test('invalid and duplicated IDs never become selectable output ports', () => {
  assert.deepEqual(lightingPorts({num_ports:2,sfp_ports:[0,2,3,3,4,257,'5']}), {copper:[1,2],sfp:[3,4]});
  assert.deepEqual(lightingPorts({num_ports:-2,sfp:[]}), {copper:[],sfp:[]});
  assert.deepEqual(lightingPorts({switch:{num_ports:0,sfp_ports:[1,2]}}), {copper:[],sfp:[1,2]});
});

test('saved topology requires unique and disjoint port roles', () => {
  const topology={version:1,model:'sample',copper_ports:[2,1],sfp_ports:[3,4]};
  assert.deepEqual(sceneTopology({topology}), {copper:[1,2],sfp:[3,4]});
  for (const patch of [{version:2},{copper_ports:[1,1]},{sfp_ports:[2,3]},{sfp_ports:[0]},{sfp_ports:null}]) {
    assert.equal(sceneTopology({topology:{...topology,...patch}}), null);
  }
  assert.deepEqual(sceneTopology({topology:{version:1,copper_ports:[],sfp_ports:[1,2]}}), {copper:[],sfp:[1,2]});
});

test('dense previews balance complete rows without changing the number of displayed ports', () => {
  assert.deepEqual(previewGrid(1), {rows:1,columns:1});
  assert.deepEqual(previewGrid(16), {rows:2,columns:8});
  for (const count of [24,48,52,54,64]) {
    const {rows,columns} = previewGrid(count);
    assert.ok(columns<=12 && rows*columns>=count && rows*columns-count<rows);
  }
});

test('long selections use exact ranges and preserve gaps and single ports',()=>{
  assert.equal(portRanges([4]),'4');
  assert.equal(portRanges([9,10]),'9, 10');
  assert.equal(portRanges([5,1,2,3,8,9,10,10]),'1–3, 5, 8–10');
  assert.equal(portRanges(Array.from({length:48},(_,i)=>i+1)),'1–48');
});
