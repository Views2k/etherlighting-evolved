import test from 'node:test';
import assert from 'node:assert/strict';
import { createState,loadState,persistState,snapshot,applySnapshot,sanitizeSfp,SfpEdits,SFP_DEFAULTS,sfpSettings,sfpControlStatus,demoSfp } from '../src/state.mjs';
import { LatestQueue } from '../src/api.mjs';
import { chassis,sfpLightingView,studioView,sfpIndicatorColor,SFP_SPEED } from '../src/views.mjs';
import { speedParamValue,speedSliderValue } from '../src/effects.mjs';

const defaults = () => createState().sfp;
function deferred() {let resolve;const promise = new Promise(done => {resolve = done;});return {promise,resolve};}
function storage() {const data = new Map();return {getItem:key => data.get(key),setItem:(key,value) => data.set(key,value)};}

test('SFP controls are separate from the eight effect ports and default to Link status',() => {
  const state = createState();
  assert.deepEqual(state.sfp.map(({port,mode,color,brightness}) => ({port,mode,color,brightness})),[
    {port:9,mode:'unmanaged',color:'#FFFFFF',brightness:100},{port:10,mode:'unmanaged',color:'#FFFFFF',brightness:100}
  ]);
  assert.deepEqual(state.selected,[1,2,3,4,5,6,7,8]);
  assert.deepEqual(state.zones[0].ports,state.selected);
  assert.equal(state.ports.length,8);
  assert.doesNotMatch(chassis(state),/data-port="(?:9|10)"/);
  const html = studioView(state,'#FFFFFF');
  assert.match(html,/id="sfp-controls"/);
  assert.match(html,/SFP 9/);assert.match(html,/SFP 10/);
  assert.equal(sfpLightingView({...state,sfp:[]}),'');
  assert.equal(sfpLightingView({...state,sfp:undefined}),'');
});

test('lighting choices, color and brightness are named and offline is not an anomaly',() => {
  const state = createState();state.sfp[0].mode = 'color';state.sfp[0].brightness = 23;
  const html = sfpLightingView(state);
  assert.match(html,/value="unmanaged"[^>]*>Link status/);
  assert.match(html,/value="color" selected>Color/);
  assert.match(html,/value="off"[^>]*>Off/);
  assert.match(html,/aria-label="SFP 9 port brightness" aria-valuetext="23%"/);
  assert.doesNotMatch(html.match(/<input id="sfp-brightness-9"[^>]*>/)[0],/disabled/);
  assert.match(html,/data-sfp-color-controls hidden/);
  assert.match(html,/Link offline/);
  assert.equal([...html.matchAll(/data-sfp-anomaly hidden/g)].length,2);
  state.sfp[1].anomaly = true;
  assert.match(sfpLightingView(state),/data-sfp-anomaly >Anomaly detected/);
  state.sfp[0].link = null;
  assert.match(sfpLightingView(state),/Link unknown/);
});

test('SFP indicators show the chosen color and brightness independently of link status',() => {
  const state = createState();state.brightness = 100;Object.assign(state.sfp[0],{mode:'color',color:'#2468AC',brightness:50,link:false,anomaly:true});
  assert.equal(sfpIndicatorColor(state.sfp[0]),'#123456');
  let html = sfpLightingView(state);
  assert.match(html,/<h3><span class="sfp-indicator"[^>]*style="background-color:#123456"><\/span>SFP 9<\/h3>/);
  assert.match(html,/background-color:#51555C/);
  state.sfp[0].mode = 'off';assert.equal(sfpIndicatorColor(state.sfp[0]),'#000000');
  html = sfpLightingView(state);assert.match(html,/background-color:#000000/);
  state.sfp[0].mode = 'color';state.sfp[0].brightness = 0;assert.equal(sfpIndicatorColor(state.sfp[0]),'#000000');
  state.sfp[0].brightness = 100;assert.equal(sfpIndicatorColor(state.sfp[0]),'#2468AC');
});

test('SFP data is validated and unsupported ports never become controls',() => {
  assert.deepEqual(sanitizeSfp(undefined),[]);
  assert.deepEqual(sanitizeSfp([{port:0},{port:257},{port:'9'}]),[]);
  assert.deepEqual(sanitizeSfp([{port:1},{port:11},{port:49},{port:50}]).map(port=>port.port),[1,11,49,50]);
  const entries = sanitizeSfp([{port:9,mode:'invalid',color:'<script>',brightness:Infinity,link:'yes'},
    {port:9,mode:'off'},{port:10,mode:'color',color:'#2468ac',brightness:-1,anomaly:true}]);
  assert.equal(entries.length,2);
  assert.deepEqual(entries[0],{port:9,...SFP_DEFAULTS,link:null,anomaly:null,revision:0,acknowledged_revision:0,control:{state:'idle',owned:false}});
  assert.equal(entries[1].color,'#2468AC');assert.equal(entries[1].brightness,0);
  assert.deepEqual(sanitizeSfp([{port:9,brightness:35.6},{port:10,brightness:101.4}]).map(item => item.brightness),[36,100]);
  const edits = new SfpEdits(defaults());
  for (const patch of [{},{port:1},{brightness:101},{brightness:1.5},{brightness:'35'},{brightness:true},{brightness:NaN},{color:'bad-color'},{mode:'blink'}]) assert.equal(edits.edit(9,patch),null);
  assert.equal(edits.edit(8,{mode:'color'}),null);
  assert.equal(edits.pending.size,0);
});

test('coalesced requests retain mode, color and brightness for both SFP ports',async () => {
  const edits = new SfpEdits(defaults()),held = deferred(),sent = [];
  let remote = defaults();
  const queue = new LatestQueue(async (port,request) => {
    sent.push(structuredClone(request));
    if (sent.length === 1) await held.promise;
    remote = remote.map(item => item.port === port ? {...item,...request.body} : item);
    edits.acknowledge(request,remote);
  },error => {throw error;});
  const update = (port,patch) => {const request = edits.edit(port,patch);queue.push(port,request);};
  update(9,{mode:'color'});update(9,{color:'#2468AC'});update(9,{brightness:24});
  update(10,{mode:'off'});update(10,{color:'#ABCDEF'});update(10,{brightness:0});
  held.resolve();await queue.idle();
  assert.equal(sent.length,3);
  assert.deepEqual(sent[1].body,{...SFP_DEFAULTS,mode:'color',color:'#2468AC',brightness:24});
  assert.deepEqual(sent[2].body,{...SFP_DEFAULTS,mode:'off',color:'#ABCDEF',brightness:0});
  assert.equal(edits.pending.size,0);
  assert.equal(edits.values[0].brightness,24);assert.equal(edits.values[1].mode,'off');
});

test('websocket refreshes update facts without replacing pending edits',() => {
  const edits = new SfpEdits(defaults());
  const first = edits.edit(9,{mode:'color'});
  const latest = edits.edit(9,{color:'#2468AC',brightness:12});
  const server = defaults();server[0].link = false;server[0].anomaly = true;
  edits.receive(server);
  assert.deepEqual(edits.values[0],{...server[0],mode:'color',color:'#2468AC',brightness:12,pending:true});
  server[0].mode = 'color';
  edits.acknowledge(first,server);
  assert.equal(edits.values[0].pending,true);assert.equal(edits.values[0].brightness,12);
  server[0] = {...server[0],...latest.body};edits.acknowledge(latest,server);
  edits.receive(defaults(),{factsOnly:true});
  assert.equal(edits.values[0].brightness,12);assert.equal(edits.values[0].color,'#2468AC');
  assert.equal(edits.values[0].link,true);assert.equal(edits.values[0].pending,false);
});

test('a failed request rolls back pending ports and can resync authoritative values',async () => {
  const edits = new SfpEdits(defaults()),held = deferred(),errors = [];
  const queue = new LatestQueue(async () => {await held.promise;throw new Error('rejected');},error => {
    errors.push(error.message);edits.rollback();
    const authoritative = defaults();authoritative[0].mode = 'off';edits.receive(authoritative);
  });
  queue.push(9,edits.edit(9,{mode:'color',color:'#2468AC'}));
  queue.push(10,edits.edit(10,{mode:'off'}));
  held.resolve();await queue.idle();
  assert.deepEqual(errors,['rejected']);assert.equal(queue.pending.size,0);
  assert.equal(edits.values[0].mode,'off');assert.equal(edits.values[0].color,'#FFFFFF');
  assert.equal(edits.values[1].mode,'unmanaged');assert.equal(edits.pending.size,0);
  edits.edit(9,{mode:'color'});assert.equal(edits.rollback()[0].mode,'off');
});

test('a response from an old session cannot replace the current SFP state',() => {
  const edits = new SfpEdits(defaults()),old = edits.edit(9,{mode:'color',brightness:8});
  edits.reset([]);
  assert.equal(edits.acknowledge(old,defaults()),false);assert.deepEqual(edits.values,[]);
  edits.reset(defaults());
  assert.equal(edits.acknowledge(old,[{port:9,mode:'off'}]),false);
  assert.equal(edits.values[0].mode,'unmanaged');
});

test('demo restart and new scenes preserve SFP settings without serializing pending state',() => {
  const state = createState(),saved = storage();
  Object.assign(state.sfp[0],{mode:'color',color:'#2468AC',brightness:35,pending:true});
  Object.assign(state.sfp[1],{mode:'off',color:'#ABCDEF',brightness:60});
  assert.equal(persistState(state,saved),true);
  const restored = loadState(saved);
  assert.equal(restored.sfp[0].pending,undefined);assert.equal(restored.sfp[0].brightness,35);
  const scene = snapshot(state);
  assert.deepEqual(scene.sfp,{'9':{...SFP_DEFAULTS,mode:'color',color:'#2468AC',brightness:35},'10':{...SFP_DEFAULTS,mode:'off',color:'#ABCDEF',brightness:60}});
  state.sfp[0].link = false;state.sfp[0].anomaly = true;
  state.sfp[0].brightness = 100;applySnapshot(state,scene);
  assert.equal(state.sfp[0].brightness,35);assert.equal(state.sfp[0].link,false);assert.equal(state.sfp[0].anomaly,true);
  assert.deepEqual(state.selected,[1,2,3,4,5,6,7,8]);
});

test('old scenes release SFP to Link status and cannot add SFP controls to unsupported devices',() => {
  const state = createState(),scene = snapshot(state);delete scene.sfp;
  state.sfp[0].mode = 'color';state.sfp[1].mode = 'off';applySnapshot(state,scene);
  assert.deepEqual(state.sfp.map(item => item.mode),['unmanaged','unmanaged']);
  state.sfp = [];applySnapshot(state,{...scene,sfp:{'9':{mode:'color',color:'#2468AC',brightness:35}}});
  assert.deepEqual(state.sfp,[]);
});

test('SFP effect settings validate ranges and preserve independent colors through save and restore',() => {
  const edits = new SfpEdits(defaults());
  for (const patch of [{effect:'strobe'},{period:1.9},{period:30.1},{period:NaN},{period:'6'},
    {color_brightness:1.5},{secondary_brightness:101},{secondary_brightness:true},{secondary_color:'invalid'},
    {constructor:1},{revision:1},{control:{state:'active'}}]) assert.equal(edits.edit(9,patch),null);
  const edit = edits.edit(9,{mode:'color',effect:'two_color',period:2.5,color:'#2468AC',secondary_color:'#AC6824',color_brightness:25,secondary_brightness:75});
  const expected = {...SFP_DEFAULTS,...edit.body};
  assert.deepEqual(edit.body,expected);
  edits.acknowledge(edit,demoSfp(edits.values));
  const state = createState();state.sfp = edits.values;
  const saved = storage();persistState(state,saved);const restored = loadState(saved);
  assert.deepEqual(sfpSettings(restored.sfp[0]),expected);
  const scene = snapshot(state);applySnapshot(restored,scene);assert.deepEqual(sfpSettings(restored.sfp[0]),expected);
  assert.equal(scene.sfp['9'].control,undefined);assert.equal(scene.sfp['9'].revision,undefined);
  const old = {...scene,sfp:{'9':{mode:'color',color:'#123456',brightness:50}}};
  applySnapshot(restored,old);assert.equal(restored.sfp[0].effect,'steady');assert.equal(restored.sfp[0].period,6);
});

test('SFP effect controls reveal speed and secondary color only where relevant',() => {
  const state = createState();state.sfp = [state.sfp[0]];state.sfp[0].mode = 'color';
  for (const [effect,label] of [['steady','Steady'],['pulse','Gentle pulse'],['color_cycle','Color cycle'],['two_color','Two-color fade']]) {
    state.sfp[0].effect = effect;const html = sfpLightingView(state);
    assert.match(html,new RegExp(`value="${effect}" selected>${label}`));
    assert.equal(/data-sfp-speed-control hidden/.test(html),effect === 'steady');
    assert.equal(/data-sfp-secondary hidden/.test(html),effect !== 'two_color');
    assert.match(html,/aria-label="SFP 9 primary brightness"/);
    assert.match(html,/aria-label="SFP 9 secondary brightness"/);
    assert.match(html,/data-sfp-level-key="brightness"/);
  }
  for (const period of [2,2.5,6,30]) assert.equal(speedParamValue(SFP_SPEED,speedSliderValue(SFP_SPEED,period)),period);
  assert.equal(speedParamValue(SFP_SPEED,30),2);assert.equal(speedParamValue(SFP_SPEED,2),30);
});

test('SFP previews apply each color brightness and master brightness once',() => {
  const item = {...defaults()[0],mode:'color',effect:'two_color',color:'#804020',secondary_color:'#204080',
    color_brightness:50,secondary_brightness:25,brightness:50,period:6};
  assert.equal(sfpIndicatorColor(item,0,50),'#100804');
  assert.equal(sfpIndicatorColor(item,3,50),'#020408');
  assert.equal(sfpIndicatorColor({...item,effect:'pulse'},0,100),'#201008');
  assert.equal(sfpIndicatorColor({...item,effect:'pulse'},3,100),'#000000');
  assert.equal(sfpIndicatorColor({...item,effect:'pulse'},6,100),'#201008');
  assert.equal(sfpIndicatorColor({...item,effect:'color_cycle',color:'#000000'},1,100),'#000000');
  assert.notEqual(sfpIndicatorColor({...item,effect:'color_cycle'},0),sfpIndicatorColor({...item,effect:'color_cycle'},1));
  assert.equal(sfpIndicatorColor(item,1,0),'#000000');
});

test('an echoed setting remains pending until its hardware revision is acknowledged',() => {
  const edits = new SfpEdits(defaults()),request = edits.edit(9,{mode:'color',effect:'pulse'});
  let remote = defaults().map(item => item.port === 9 ? {...item,...request.body,revision:4,acknowledged_revision:3,control:{state:'pending',owned:true}} : item);
  edits.acknowledge(request,remote);
  assert.equal(edits.values[0].pending,false);
  assert.equal(sfpControlStatus(edits.values[0]).text,'Applying…');
  remote = remote.map(item => item.port === 9 ? {...item,acknowledged_revision:4,control:{state:'active',owned:true}} : item);
  edits.receive(remote,{factsOnly:true});assert.equal(sfpControlStatus(edits.values[0]).text,'');
  edits.receive(remote.map(item => ({...item,acknowledged_revision:3,control:{state:'pending',owned:true}})),{factsOnly:true});
  assert.equal(sfpControlStatus(edits.values[0]).text,'');
  edits.receive(remote.map(item => ({...item,control:{state:'uncertain',owned:true,error:'private diagnostic detail'}})),{factsOnly:true});
  assert.deepEqual(sfpControlStatus(edits.values[0]),{text:'Change not confirmed.',retry:true});
  const html = sfpLightingView({sfp:edits.values,brightness:100});
  assert.doesNotMatch(html,/private diagnostic detail/);assert.match(html,/data-action="sfp-retry" data-id="9" >Retry/);
});

test('newer effect edits survive older acknowledgments and frame updates',() => {
  const edits = new SfpEdits(defaults());
  const first = edits.edit(9,{mode:'color',effect:'pulse'});
  const latest = edits.edit(9,{effect:'two_color',period:2,secondary_color:'#123456',secondary_brightness:15});
  const remote = defaults().map(item => item.port === 9 ? {...item,...first.body,revision:5,acknowledged_revision:5,control:{state:'active',owned:true}} : item);
  edits.acknowledge(first,remote);
  assert.equal(edits.values[0].effect,'two_color');assert.equal(edits.values[0].secondary_brightness,15);
  assert.equal(edits.values[0].pending,true);
  const next = remote.map(item => item.port === 9 ? {...item,...latest.body,revision:6,acknowledged_revision:6} : item);
  edits.acknowledge(latest,next);edits.receive(remote);
  assert.equal(edits.values[0].effect,'two_color');assert.equal(edits.values[0].period,2);
  edits.reset(defaults());assert.equal(edits.values[0].effect,'steady');assert.equal(edits.values[0].revision,0);
});
