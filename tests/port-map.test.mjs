import test from 'node:test';
import assert from 'node:assert/strict';
import {portLabel, isUplink, linkStatus, portPower, portScopeOptions, groupNameError, selectPort, selectionHint, switchTitle, LINK_SPEEDS} from '../preview/port-map.mjs';

test('a multi-client port shows every reported name in stable order without choosing a primary', () => {
  const port = {port:3,name:'2 clients',controller:{count:2,clients:[{name:'Workstation'},{name:'Home Assistant'}]}};
  const shown = portLabel(port);
  assert.deepEqual(shown.lines, ['Home Assistant','Workstation']);
  assert.equal(shown.text, 'Home Assistant, Workstation');
  assert.deepEqual(portLabel({...port,controller:{...port.controller,clients:[...port.controller.clients].reverse()}}), shown);
  assert.equal(port.controller.clients[0].name, 'Workstation');
});

test('unknown and truncated clients retain honest counts and do not invent identities', () => {
  const port = {port:3,controller:{count:4,clients:[{name:'Host'},{name:null}],clients_truncated:true}};
  assert.deepEqual(portLabel(port).lines, ['Host','Unnamed device','+2 more']);
  assert.deepEqual(portLabel({port:2,controller:{count:2,clients:[{name:'Same'},{name:'Same'}]}}).lines, ['Same','Same']);
  assert.equal(portLabel({port:2,label:'Desk'}).text, 'Desk');
  assert.equal(portLabel({port:9},true).text, 'SFP 9');
  assert.equal(portLabel({port:4}).text, 'Port 4');
  assert.equal(portLabel({port:1,name:'<img src=x onerror=alert(1)>'}).text, '<img src=x onerror=alert(1)>');
});

test('assigned controller names lead the tile while associated hostnames stay in details', () => {
  for (const portNumber of [3,10,24,28]) {
    const port = {port:portNumber,controller:{count:2,clients:[
      {name:'server',name_source:'hostname'}, {name:'Travel router',name_source:'assigned'}
    ]}};
    assert.deepEqual(portLabel(port), {lines:['Travel router'],text:'Travel router'});
    assert.deepEqual(port.controller.clients, [
      {name:'server',name_source:'hostname'}, {name:'Travel router',name_source:'assigned'}
    ]);
    port.controller.clients[0] = {name:'Home automation',name_source:'assigned'};
    assert.equal(portLabel(port).text, 'Home automation, Travel router');
    port.controller.clients[1] = {name:'travel-router',name_source:'hostname'};
    port.controller.clients[0] = {name:'server',name_source:'hostname'};
    assert.equal(portLabel(port).text, 'server, travel-router');
    port.controller.clients[0] = {name:' ',name_source:'assigned'};
    assert.equal(portLabel(port).text, 'travel-router, Unnamed device');
    port.link = {uplink:true};
    assert.equal(portLabel(port).text, 'Uplink');
  }
});

test('detected uplinks take precedence on any port and disappear when detection clears', () => {
  for (const port of [1,3,5,8,9,24]) {
    const details = {port,name:'Device',controller:{clients:[{name:'Downstream'}]}};
    for (const detection of [{link:{uplink:true}},{facts:{uplink:true}}]) {
      assert.equal(isUplink({...details,...detection}),true);
      assert.deepEqual(portLabel({...details,...detection}), {lines:['Uplink'],text:'Uplink'});
    }
    assert.equal(portLabel({...details,facts:{uplink:false}}).text,'Downstream');
  }
});

test('link reporting distinguishes missing data from disconnected and maps UniFi speed classes', () => {
  assert.deepEqual(LINK_SPEEDS, [[100,'FE'],[1000,'GbE'],[2500,'2.5 GbE'],[5000,'5 GbE'],[10000,'10 GbE'],[25000,'25 GbE'],[40000,'40 GbE'],[100000,'100 GbE']]);
  for (const port of [{},{link:null},{facts:{link:null}},{link:{link_up:null}}]) assert.equal(linkStatus(port).key,'unknown');
  assert.equal(linkStatus({link:{link_up:false},facts:{link:true}}).key,'offline');
  assert.equal(linkStatus({link:{admin_up:false,link_up:true}}).key,'disabled');
  for (const [speed,label] of LINK_SPEEDS) assert.equal(linkStatus({facts:{link:true,speed}}).label,label);
  assert.equal(linkStatus({link:{link_up:true,speed_mbps:10}}).key,100);
  assert.equal(linkStatus({link:{link_up:true,speed_mbps:25000}}).label,'25 GbE');
  assert.equal(linkStatus({link:true,facts:{speed:10000}}).label,'10 GbE');
});

test('PoE telemetry never turns unknown watts into a measured zero or off state', () => {
  for (const port of [{},{poe:null},{poe:{}}]) assert.equal(portPower(port).description,'Not reported');
  assert.equal(portPower({poe:{delivering:false,power_w:null}}).description,'Not supplying power');
  for (const power_w of [null,undefined,NaN,Infinity,-1,'4']) assert.equal(portPower({poe:{delivering:true,power_w}}).description,'Power usage not reported');
  assert.equal(portPower({poe:{delivering:true,power_w:0}}).watts,'0.0 W');
  assert.equal(portPower({poe:{delivering:true,power_w:17.78}}).watts,'17.8 W');
});

test('scope labels cannot confuse the legacy copper group with all physical ports', () => {
  const ports = Array.from({length:8},(_,i)=>({port:i+1}));
  const state = {ports,sfp:[{port:9},{port:10}],zones:[{id:'all',name:'All ports',ports:ports.map(port=>port.port)}]};
  let options = portScopeOptions(state);
  assert.deepEqual(options.map(option=>option.label), ['All ports','All Ethernet ports','SFP ports']);
  assert.equal(options[0].ports.length,10); assert.equal(options[1].ports.length,8);
  state.zones.push({id:'custom',name:'All ports',ports:[1,2]});
  options = portScopeOptions(state);
  assert.equal(options.find(option=>option.value==='custom').label,'Group: All ports');
  assert.equal(state.zones[1].name,'All ports');
  state.zones[0].name='My setup';
  assert.ok(portScopeOptions(state).some(option=>option.label==='My setup'));
  assert.deepEqual(portScopeOptions({ports,sfp:[],zones:[]}).map(option=>option.label),['All ports']);
});

test('group names stop at the backend limit before submitting a mutation', () => {
  assert.equal(groupNameError('A'.repeat(40)),'');
  for (const length of [41,60]) assert.match(groupNameError('A'.repeat(length)), /40 characters/);
  assert.match(groupNameError('   '), /Enter a name/);
});

test('selection supports a solo port and additive pointer or keyboard interaction without mutating the previous set', () => {
  const ports = [1,2,3,4,9,10], selected = new Set([1,2]);
  assert.deepEqual(selectPort({ports,selected,port:3}), {ports:[3],anchor:3});
  assert.deepEqual(selectPort({ports,selected,port:3,additive:true}), {ports:[1,2,3],anchor:3});
  assert.deepEqual(selectPort({ports,selected,port:2,additive:true}), {ports:[1],anchor:2});
  assert.deepEqual([...selected], [1,2]);
  assert.deepEqual(selectPort({ports,selected,anchor:1,port:99}), {ports:[1,2],anchor:1});
});

test('Shift selects the visible range in either direction while retaining its anchor', () => {
  const ports = [1,2,3,4,9,10], selected = new Set([1,3]);
  assert.deepEqual(selectPort({ports,selected,anchor:3,port:10,range:true}), {ports:[3,4,9,10],anchor:3});
  assert.deepEqual(selectPort({ports,selected,anchor:9,port:2,range:true}), {ports:[2,3,4,9],anchor:9});
  assert.deepEqual(selectPort({ports,selected,anchor:3,port:9,range:true,additive:true}), {ports:[1,3,4,9],anchor:3});
  assert.deepEqual(selectPort({ports,selected,anchor:null,port:4,range:true}), {ports:[4],anchor:4});
});

test('selection hints reflect the platform and expose the tap-based mode', () => {
  assert.match(selectionHint('MacIntel'), /^Command-click/);
  assert.match(selectionHint('macOS'), /^Command-click/);
  assert.match(selectionHint('Win32'), /^Ctrl-click/);
  assert.match(selectionHint('Linux'), /^Ctrl-click/);
  assert.match(selectionHint('Win32',true), /^Select ports to add or remove them/);
  for(const platform of ['iPhone','iPad','Android','Win32','MacIntel']) {
    assert.match(selectionHint(platform,false,true),/Use Select multiple/);
    assert.equal(selectionHint(platform,true,true),'Tap ports to add or remove them.');
  }
  assert.match(selectionHint('iPhone'),/^Tap/);
});

test('a reported controller name fills a missing switch model without inventing an identity',()=>{
  assert.equal(switchTitle({switch:{model:'Model'},controller:{switch_name:'Office'}}),'Model');
  assert.equal(switchTitle({switch:{model:' '},controller:{switch_name:'Office'}}),'Office');
  assert.equal(switchTitle({controller:{switch_name:null}}),'UniFi switch');
});

test('scope options use reported ports rather than assuming eight copper and two SFP', () => {
  const state = {ports:[{port:2},{port:8},{port:24}],sfp:[{port:25},{port:26},{port:27},{port:28}],zones:[]};
  assert.deepEqual(portScopeOptions(state).map(option=>option.ports), [[2,8,24,25,26,27,28],[2,8,24],[25,26,27,28]]);
});
