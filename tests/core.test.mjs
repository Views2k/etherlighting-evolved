import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeHex,hexToRgb,hexToHsv,hsvToHex,rgbToHex } from '../src/color.mjs';
import { EFFECTS,LIBRARY,defaultConfig,effectColor,setEffectCatalog,effectById,speedParam,speedParamValue,speedSliderValue,previewPhase,previewColorPhase,resolvedColorParams,paramsAfterChange } from '../src/effects.mjs';
import { studioView,effectsView,inspectorView,outputSummary,sourceForPort,paramText,loginView,settingsView,conditionText } from '../src/views.mjs';
import { CATALOG } from '../src/catalog.mjs';
import { conditionMatches,matchingRule,RULE_RATE_LIMITS,ruleRate,renderLook } from '../src/rules.mjs';
import { createState,persistState,loadState,snapshot,applySnapshot,STORAGE_KEY,persistView,restoreView,VIEW_STORAGE_KEY } from '../src/state.mjs';
import { Backend,LatestQueue,EffectTransaction,applyZoneEffect } from '../src/api.mjs';
import { groupEditor } from '../src/group-views.mjs';
import { assignGroup,removeGroup,replaceGroups } from '../src/groups.mjs';
import { ruleEditor,readRuleLook } from '../src/rule-editor.mjs';

test('color entry accepts shorthand, rejects malformed values, and round-trips RGB',() => {
  assert.equal(normalizeHex(' #f0a '),'#FF00AA');assert.equal(normalizeHex('abcdef'),'#ABCDEF');
  for (const invalid of ['#ffffff00','red','<script>',null]) assert.equal(normalizeHex(invalid),null);
  for (const color of ['#000000','#FFFFFF','#FF0000','#00FF00','#0000FF','#E4BD82','#79DCC0']) assert.equal(hsvToHex(...hexToHsv(color)),color);
  assert.equal(rgbToHex([-20,256,127.7]),'#00FF80');
});

test('saving login is a visible opt-in and never stored with browser workspace data',async () => {
  const state = createState(),html = loginView(state);
  assert.match(html,/<label class="check-label save-login">/);
  assert.match(html,/Save login on this computer/);
  assert.ok(html.indexOf('id="save-login"') < html.indexOf('<details class="connection-options"'));
  assert.doesNotMatch(html,/<input id="save-login"[^>]*checked/);
  assert.match(loginView({...state,savedLogin:true}),/<input id="save-login"[^>]*checked/);
  const calls = [],backend = new Backend();
  backend.request = async (...args) => {calls.push(args);return {logged_in:true};};
  await backend.login('test-switch.invalid','test-user','test-password');
  await backend.login('test-switch.invalid','test-user','test-password',true);
  assert.equal(calls[0][2].remember,false);assert.equal(calls[1][2].remember,true);
  const stored = new Map(),storage = {getItem:key => stored.get(key),setItem:(key,value) => stored.set(key,value)};
  state.savedLogin = true;state.password = 'test-password';persistState(state,storage);persistView(state,storage);
  for (const value of stored.values()) {assert.ok(!value.includes('test-password'));assert.ok(!value.includes('savedLogin'));}
});

test('saved-login removal uses its own endpoint and is offered only for a saved connected login',async () => {
  const state = createState();state.mode = 'live';
  assert.ok(!settingsView(state).includes('data-action="forget-login"'));
  assert.ok(settingsView({...state,savedLogin:true}).includes('data-action="forget-login"'));
  assert.ok(!settingsView({...state,mode:'simulator',savedLogin:true}).includes('data-action="forget-login"'));
  const backend = new Backend(),calls = [];
  backend.request = async (...args) => {calls.push(args);return {logged_in:true,remembered:null};};
  assert.equal((await backend.forgetLogin()).logged_in,true);
  assert.deepEqual(calls,[['/session/forget','POST']]);
});

test('rule brightness explicitly inherits the visible master level or saves a custom value',() => {
  const state = createState();state.brightness = 73;
  const draft = {id:'test',name:'Test',match:'all',ports:null,conditions:[{field:'link',op:'is',value:true}],look:{color:'#FFFFFF',style:'pulse',brightness:null,rate:.25}};
  const html = ruleEditor(draft,state);
  assert.match(html,/<input name="brightness"[^>]*value="73"[^>]*disabled/);
  assert.match(html,/<input name="inheritBrightness"[^>]*checked/);
  assert.ok(html.includes('Use master brightness'));
  const fields = new FormData();fields.set('color','#FFFFFF');fields.set('style','pulse');fields.set('rate','.25');fields.set('inheritBrightness','on');
  assert.equal(readRuleLook(fields).brightness,null);
  fields.delete('inheritBrightness');fields.set('brightness','65');assert.equal(readRuleLook(fields).brightness,65);
  fields.set('brightness','0');assert.equal(readRuleLook(fields).brightness,0);
  draft.look.brightness = 65;assert.match(ruleEditor(draft,state),/<input name="brightness"[^>]*value="65"[^>]*required/);
});

test('rule condition labels display readable names and common groups',() => {
  assert.match(conditionText({field:'weekday',op:'in',value:['mon','tue','wed','thu','fri']}),/Weekdays/);
  assert.match(conditionText({field:'weekday',op:'in',value:['sun','sat']}),/Weekends/);
  assert.match(conditionText({field:'weekday',op:'in',value:['wed','mon']}),/Mon, Wed/);
  assert.match(conditionText({field:'weekday',op:'not_in',value:['sun']}),/excludes Sun/);
  const draft = {id:'poe-labels',name:'PoE labels',match:'all',ports:null,conditions:[{field:'poe',op:'in',value:['poe+']}],look:{color:'#FFFFFF',style:'solid',brightness:null}};
  const html = ruleEditor(draft,createState());
  assert.match(html,/value="off"[^>]*>No PoE<\/label>/);
  assert.match(html,/value="poe\+" checked>POE\+<\/label>/);
  assert.doesNotMatch(html,/>poe\+<\/label>/);
});
test('the library includes all registered effects and every effect produces valid RGB frames',() => {
  assert.equal(LIBRARY.length,CATALOG.length);assert.equal(new Set(EFFECTS.map(e => e.id)).size,EFFECTS.length);
  for (const e of EFFECTS) for (const t of [0,.1,1.5,10,600]) for (let i = 0;i < 8;i++) assert.match(effectColor(defaultConfig(e.id),i,8,t,{link:true,traffic:100,hour:13}),/^#[\da-f]{6}$/i,`${e.id} @ ${t}`);
});

test('live effect defaults and control limits come from the connected backend',async () => {
  const api = new Backend(),requests = [];
  const remote = structuredClone(CATALOG);
  const speed = remote.find(e => e.id === 'comet').params.find(p => p.key === 'speed');
  speed.default = 0.75;speed.max = 3;
  api.request = async path => {requests.push(path);return path === '/effects' ? remote : {};};
  try {
    await api.read();setEffectCatalog(api.effects);
    assert.equal(defaultConfig('comet').params.speed,0.75);
    assert.equal(effectById('comet').params.find(p => p.key === 'speed').max,3);
    await api.read();assert.equal(requests.filter(p => p === '/effects').length,1);
    api.close();await api.read();assert.equal(requests.filter(p => p === '/effects').length,2);
  } finally {setEffectCatalog(CATALOG);}
});
test('every animated effect exposes its speed before advanced controls in both views',() => {
  const animated = CATALOG.filter(e => !['static','daylight','traffic'].includes(e.id));
  assert.equal(animated.length,CATALOG.length - 3);
  for (const effect of animated) {
    const state = createState();state.config = defaultConfig(effect.id);
    const param = speedParam(effectById(effect.id));
    assert.ok(param,`${effect.id} speed setting`);
    for (const view of [studioView(state,'#FFFFFF'),effectsView(state)]) {
      assert.equal((view.match(/id="effect-speed"/g) || []).length,1,effect.id);
      const advanced = view.indexOf('<details class="advanced-controls"');
      assert.ok(advanced === -1 || view.indexOf('id="effect-speed"') < advanced,effect.id);
      assert.equal(view.includes(`data-param="${param.key}"`),false,'no duplicate speed slider');
    }
    for (const value of [param.min,param.default,param.max]) {
      assert.equal(speedParamValue(param,speedSliderValue(param,value)),value,`${effect.id} preserves speed`);
    }
  }
  const breathing = speedParam(effectById('breathing'));
  assert.equal(speedParamValue(breathing,breathing.max),1,'right end is fastest supported breathing');
  assert.equal(speedParamValue(breathing,breathing.min),30,'left end is slowest breathing');
});

test('every explicit color has one inline random switch and color timing appears only when needed',() => {
  for (const effect of EFFECTS) {
    const state = createState();state.config = defaultConfig(effect.id);
    if(effect.id==='traffic')state.config.params.custom_uplink=true;
    const colors = effect.params.filter(p => p.type === 'color');
    for (const html of [studioView(state,'#FFFFFF'),effectsView(state)]) {
      for (const param of colors) {
        assert.equal((html.match(new RegExp(`data-key="${param.random_param}"`,'g')) || []).length,1,effect.id);
        assert.equal(state.config.params[param.random_param],false);
      }
      assert.ok(!html.includes('data-param="color_transition"'),effect.id);
    }
    if (colors.length) {
      state.config.params = paramsAfterChange(state.config,colors[0].random_param,true);
      const html = effectsView(state);
      assert.equal((html.match(/data-param="color_transition"/g) || []).length,1);
      assert.match(html,new RegExp(`<input id="param-${colors[0].key}"[^>]*disabled`));
    }
  }
});

test('Marquee can randomize its background while its chosen primary stays editable and unchanged',() => {
  const state = createState();state.config = defaultConfig('marquee');state.config.params.color = '#93B7E8';
  state.config.params.background = '#172E45';
  state.config.params = paramsAfterChange(state.config,'random_background',true);
  state.zones[0].effect = structuredClone(state.config);
  const html = effectsView(state);
  assert.doesNotMatch(html,/<input id="param-color"[^>]*disabled/);
  assert.match(html,/<input id="param-background"[^>]*disabled/);
  const backgrounds = [];
  for (const time of [0,4,8,16]) {
    const params = resolvedColorParams(state.config,time);
    assert.equal(params.color,'#93B7E8');backgrounds.push(params.background);
  }
  assert.ok(new Set(backgrounds).size > 1);
  let saved;const storage = {setItem:(_,value) => {saved = value;},getItem:() => saved};persistState(state,storage);
  const restored = loadState(storage),scene = createState();applySnapshot(scene,snapshot(state));
  for (const config of [restored.config,scene.config]) {
    assert.equal(config.params.random_background,true);assert.equal(config.params.background,'#172E45');
    config.params = paramsAfterChange(config,'random_background',false);
    assert.equal(resolvedColorParams(config,8).background,'#172E45');
    assert.equal(config.params.color,'#93B7E8');
  }
});

test('generic primary randomization and legacy sparkle or flash colors remain mutually exclusive',() => {
  for (const id of ['starry_night','strobe']) {
    const config = defaultConfig(id),[generic,legacy] = effectById(id).exclusive_params[0];
    config.params.color = '#93B7E8';config.params = paramsAfterChange(config,legacy,true);
    config.params = paramsAfterChange(config,generic,true);
    assert.equal(config.params[legacy],false);assert.equal(config.params[generic],true);
    config.params = paramsAfterChange(config,legacy,true);
    assert.equal(config.params[legacy],true);assert.equal(config.params[generic],false);
    assert.equal(config.params.color,'#93B7E8');
    const state = createState();state.config = config;
    assert.match(effectsView(state),/<input id="param-color"[^>]*disabled/);
    assert.ok(effectsView(state).includes(id === 'starry_night' ? 'Different color per sparkle' : 'New color each flash'));
    if (config.params.random_background !== undefined) {
      config.params = paramsAfterChange(config,'random_background',true);
      assert.equal(config.params[legacy],true,'background randomization does not disable the primary source');
    }
  }
});

test('an older backend catalog does not render or send unsupported random-color parameters',async () => {
  const old = structuredClone(CATALOG);
  for (const effect of old) {
    effect.params = effect.params.filter(p => !p.randomizes && p.key !== 'color_transition');
    effect.params.forEach(p => {delete p.random_param;});delete effect.exclusive_params;
  }
  try {
    setEffectCatalog(old);
    const state = createState();state.config = defaultConfig('marquee');
    assert.ok(!effectsView(state).includes('Randomize'));
    assert.equal(state.config.params.random_background,undefined);
    assert.equal(state.config.params.color_transition,undefined);
    let sent;
    await applyZoneEffect({request:async (_path,_method,body) => {sent = body;return {overrides:{}};}},state.zones[0],state.config);
    assert.deepEqual(Object.keys(sent.params).sort(),effectById('marquee').params.map(p => p.key).sort());
  } finally {setEffectCatalog(CATALOG);}
});

test('color-transition edits preserve color phase independently of movement speed',() => {
  const config = defaultConfig('marquee'),motion = {},colors = {};
  config.params.random_background = true;
  const before = previewColorPhase(colors,config,12),position = previewPhase(motion,config,12);
  config.params.color_transition = 2;
  assert.equal(previewColorPhase(colors,config,12),before);
  assert.equal(previewPhase(motion,config,12),position);
  assert.equal(previewColorPhase(colors,config,13),before + .5);
  config.params.speed = 1.25;
  assert.equal(previewColorPhase(colors,config,13),before + .5);
  const a = resolvedColorParams(config,0,.99999).background,b = resolvedColorParams(config,0,1.00001).background;
  assert.ok(hexToRgb(a).every((channel,i) => Math.abs(channel - hexToRgb(b)[i]) <= 1));
});

test('overnight schedules include midnight and exclude their end',() => {
  const rule = {field:'time',op:'between',value:['22:00','07:00']};
  for (const [h,m,expected] of [[21,59,false],[22,0,true],[0,0,true],[6,59,true],[7,0,false]]) {
    const date = new Date(2026,9,29,h,m);assert.equal(conditionMatches(rule,{},date),expected);
  }
});

test('choosing an effect requests its group and color release atomically',async () => {
  const requests = [];
  const remote = {num_ports:8,overrides:{8:{color:'#ff0000'}}};
  const backend = {request:async (path,method,body) => {
    requests.push([path,method,body]);
    return remote;
  }};
  const config = defaultConfig('breathing');
  assert.equal(await applyZoneEffect(backend,{id:'desk',ports:[1,2]},config),remote);
  assert.deepEqual(requests,[['/zones/desk/effect','PUT',{...config,clear_overrides:true}]]);
  assert.equal(config.clear_overrides,undefined,'request flag does not leak into saved effect config');
});

test('a rejected effect request does not issue follow-up color deletions',async () => {
  const requests = [];
  const backend = {request:async (path,method) => {
    requests.push([path,method]);
    throw Error('connection interrupted');
  }};
  await assert.rejects(applyZoneEffect(backend,{id:'all',ports:[1,2,3,4,5,6,7,8]},defaultConfig('ripple')),/connection interrupted/);
  assert.deepEqual(requests,[['/zones/all/effect','PUT']]);
});

test('an older controller retaining group colors cannot report successful effect application',async () => {
  const requests = [];
  const backend = {request:async (path,method) => {
    requests.push([path,method]);
    return {overrides:{2:{color:'#FFFFFF'},8:{color:'#FF0000'}}};
  }};
  await assert.rejects(applyZoneEffect(backend,{id:'desk',ports:[1,2]},defaultConfig('breathing')),error => error.code === 'controller_update_required');
  assert.deepEqual(requests,[['/zones/desk/effect','PUT']],'compatibility failure does not delete colors');
  const otherGroup = await applyZoneEffect(backend,{id:'other',ports:[3,4]},defaultConfig('breathing'));
  assert.ok(otherGroup.overrides[8],'unrelated group colors remain valid');
});

test('lighting separates group effects from selected port colors without duplicate controls',() => {
  const state = createState();
  const effect = studioView(state,'#FFFFFF');
  assert.equal((effect.match(/data-action="effect"/g) || []).length,6);
  assert.equal((effect.match(/id="global-brightness"/g) || []).length,1);
  assert.ok(effect.includes('id="effect-speed"'));
  assert.ok(!effect.includes('id="color-plane"'));
  state.inspector = 'color';
  const color = studioView(state,'#FFFFFF');
  assert.ok(color.includes('id="color-plane"'));
  assert.ok(!color.includes('id="effect-speed"'));
  assert.ok(!color.includes('data-action="pause"'),'hardware controls do not contain a preview-only pause');
  state.selected = [];
  assert.ok(inspectorView(state,'#FFFFFF').includes('Select all ports'));
  assert.ok(!inspectorView(state,'#FFFFFF').includes('id="color-plane"'));
});

test('master and manual brightness display zero without replacing it with an inherited value',() => {
  const state = createState();state.brightness = 0;state.selected = [1];state.inspector = 'color';
  state.ports[0].override = {color:'#FFFFFF',brightness:null};
  const inherited = studioView(state,'#FFFFFF');
  assert.match(inherited,/<input id="global-brightness"[^>]*value="0"/);
  assert.match(inherited,/<output id="global-brightness-value">0%<\/output>/);
  assert.match(inherited,/<input id="port-brightness"[^>]*value="0"/);
  state.brightness = 80;state.ports[0].override.brightness = 0;
  const manual = inspectorView(state,'#FFFFFF');
  assert.match(manual,/<input id="port-brightness"[^>]*value="0"/);
  assert.match(manual,/<output id="port-brightness-value">0%<\/output>/);
  assert.doesNotMatch(manual,/<input id="port-brightness"[^>]*disabled/);
  state.selected = [1,2];state.ports[1].override = {color:'#FFFFFF',brightness:null};
  assert.match(inspectorView(state,'#FFFFFF'),/<output id="port-brightness-value">Mixed<\/output>/);
});

test('zero master and manual brightness survive workspace and scene restore independently',() => {
  const state = createState();state.brightness = 0;
  state.ports[0].override = {color:'#FFFFFF',brightness:0};
  state.ports[1].override = {color:'#FFFFFF',brightness:null};
  state.ports[2].override = {color:'#FFFFFF',brightness:100};
  let saved;const storage = {setItem:(_,value) => {saved = value;},getItem:() => saved};
  assert.equal(persistState(state,storage),true);
  const restored = loadState(storage),scene = createState();applySnapshot(scene,snapshot(state));
  for (const target of [restored,scene]) {
    assert.equal(target.brightness,0);
    assert.deepEqual(target.ports.slice(0,3).map(p => p.override.brightness),[0,null,100]);
  }
});

test('port source labels use controller truth and count only the selected group',() => {
  const state = createState();state.mode = 'live';state.zones[0].ports = [1,2];
  state.ports[0].source = {kind:'rule',name:'Offline'};state.ports[1].source = {kind:'override'};
  state.ports[7].source = {kind:'rule',name:'Other group'};
  assert.deepEqual(outputSummary(state),{manual:1,rules:1,total:2});
  assert.equal(sourceForPort(state,state.ports[0]).name,'Offline');
  state.released = true;
  assert.equal(sourceForPort(state,state.ports[0]).kind,'released');
});

test('parameter displays show percentages and port meanings without changing native values',() => {
  for (const key of ['floor','saturation','intensity','duty','night_brightness','smoothing']) assert.equal(paramText({key},.35),'35%');
  assert.equal(paramText({key:'center'},0),'Middle');
  assert.equal(paramText({key:'center'},3),'Port 3');
  assert.equal(paramText({key:'width',unit:'ports'},1.5),'1.5 ports');
});

test('demo speed edits preserve the current animation position and can pause at zero',() => {
  const clock = {},config = defaultConfig('rainbow');
  const before = previewPhase(clock,config,12);
  config.params.speed = .2;
  assert.equal(previewPhase(clock,config,12),before);
  assert.ok(Math.abs(previewPhase(clock,config,13) - before - .2) < 1e-10);
  config.params.speed = 0;
  const stopped = previewPhase(clock,config,14);
  assert.equal(previewPhase(clock,config,50),stopped);
});

test('workspace location survives reload without saving connection details',() => {
  const values = new Map(),storage = {getItem:key => values.get(key),setItem:(key,value) => values.set(key,value)};
  const state = createState();Object.assign(state,{page:'groups',activeZone:'desk',inspector:'color',deviceTab:'sfp',selected:[3,5],password:'not-persisted'});
  assert.equal(persistView(state,storage),true);
  assert.deepEqual(JSON.parse(values.get(VIEW_STORAGE_KEY)),{page:'groups',activeZone:'desk',inspector:'color',deviceTab:'sfp',selected:[3,5],preferences:{motion:true,labels:true}});
  const restored = restoreView(createState(),storage);
  assert.equal(restored.page,'groups');assert.equal(restored.activeZone,'desk');assert.deepEqual(restored.selected,[3,5]);
  assert.equal(restored.deviceTab,'sfp');
  assert.equal(restoreView(createState(),storage,'#rules').page,'rules','Back/Forward route takes precedence');
  values.set(VIEW_STORAGE_KEY,'invalid json');assert.equal(restoreView(createState(),storage).page,'studio');
});

test('effect transactions block overlapping changes and release the controls after failure recovery',async () => {
  const pending = [],transaction = new EffectTransaction(value => pending.push(value));
  let finish,started = false;
  const work = transaction.run(async () => {started = true;await new Promise(resolve => {finish = resolve;});return 'accepted';});
  assert.ok(started);assert.ok(transaction.pending);
  await assert.rejects(transaction.run(async () => 'wrong effect'),error => error.code === 'change_in_progress');
  finish();assert.equal(await work,'accepted');assert.equal(transaction.pending,false);
  await assert.rejects(transaction.run(async () => {throw Error('write failed');}),/write failed/);
  assert.equal(transaction.pending,false);assert.deepEqual(pending,[true,false,true,false]);
});

test('live workspace appearance preferences survive a fresh connected state',() => {
  let saved;const storage = {setItem:(_,value) => {saved = value;},getItem:() => saved};
  const live = createState();live.mode = 'live';live.preferences = {motion:false,labels:false};persistView(live,storage);
  const fresh = restoreView(createState(),storage);
  assert.deepEqual(fresh.preferences,{motion:false,labels:false});
  saved = JSON.stringify({preferences:{motion:'false',labels:null}});
  assert.deepEqual(restoreView(createState(),storage).preferences,{motion:true,labels:true});
});

test('rainbow stars keep their color through the global preview cycle boundary',() => {
  const config = defaultConfig('starry_night');config.params.rainbow = true;
  const cycle = 1.5 + config.params.fade + 1 / config.params.rate;
  for (let port = 0;port < 8;port++) {
    const a = hexToRgb(effectColor(config,port,8,cycle - .0001)),b = hexToRgb(effectColor(config,port,8,cycle + .0001));
    assert.ok(a.every((channel,i) => Math.abs(channel - b[i]) <= 1));
  }
});

test('rule animation rates honor the conservative ranges and keep flashes visible',() => {
  for (const [style,limits] of Object.entries(RULE_RATE_LIMITS)) {
    assert.equal(ruleRate(style,99),limits.max);assert.equal(ruleRate(style,-1),limits.min);assert.equal(ruleRate(style),limits.default);
  }
  const look = {style:'flash',color:'#FFFFFF',rate:.5};
  assert.equal(renderLook(look,'#000000',.35),'#FFFFFF');
  assert.equal(renderLook(look,'#000000',.5),'#000000');
  assert.equal(renderLook({style:'effect'},'#ABCDEF',1),'#ABCDEF');
});
test('unknown port data never triggers negative or offline conditions',() => {
  const date = new Date();
  assert.equal(conditionMatches({field:'link',op:'is',value:false},{link:null},date),false);
  assert.equal(conditionMatches({field:'poe',op:'is_not',value:'poe'}, {},date),false);
});
test('rule priority, scope, all/any and enabled state are honored',() => {
  const port = {id:2,facts:{link:true,anomaly:true,poe:'poe+'}},date = new Date();
  const online = {id:'online',enabled:true,match:'all',conditions:[{field:'link',op:'is',value:true}]};
  const alert = {id:'alert',enabled:true,match:'all',conditions:[{field:'anomaly',op:'is',value:true}]};
  assert.equal(matchingRule([alert,online],port,date).id,'alert');
  assert.equal(matchingRule([{...alert,ports:[1]},online],port,date).id,'online');
  assert.equal(matchingRule([{...alert,enabled:false},online],port,date).id,'online');
  assert.equal(matchingRule([{...alert,match:'any',conditions:[{field:'link',op:'is',value:false},...alert.conditions]}],port,date).id,'alert');
});
test('scene snapshots restore colors, brightness, and rules without shared references',() => {
  const state = createState();state.ports[2].override = {color:'#FF00AA',brightness:24};state.brightness = 35;
  const snap = snapshot(state);state.ports[2].override.color = '#000000';state.brightness = 100;
  applySnapshot(state,snap);assert.equal(state.ports[2].override.color,'#FF00AA');assert.equal(state.brightness,35);
  state.config.params.color_a = '#FFFFFF';assert.notEqual(snap.params.color_a,'#FFFFFF');
});
test('scene restore sanitizes each group effect while preserving valid colors and port selection',() => {
  const state = createState(),chosen = defaultConfig('marquee');
  state.selected = [2,5];
  Object.assign(chosen.params,{color:'#93B7E8',background:'#172E45',random_background:true});
  state.zones = [
    {id:'first',name:'First group',ports:[1,2,3,4],effect:chosen},
    {id:'second',name:'Second group',ports:[5,6,7,8],effect:defaultConfig('breathing')}
  ];
  const snap = snapshot(state),savedPrimary = structuredClone(chosen);
  Object.assign(snap.zones[1].effect.params,{color:'invalid',period:-10,floor:null,random_color:'yes',unknown:true});
  applySnapshot(state,snap);
  assert.deepEqual(state.selected,[2,5]);
  assert.deepEqual(state.zones.map(zone => zone.ports),[[1,2,3,4],[5,6,7,8]]);
  assert.deepEqual(state.zones[0].effect,savedPrimary);
  assert.deepEqual(state.config,savedPrimary);
  const restored = state.zones[1].effect;
  assert.equal(restored.params.color,defaultConfig('breathing').params.color);
  assert.equal(restored.params.period,1);
  assert.equal(restored.params.floor,defaultConfig('breathing').params.floor);
  assert.equal(restored.params.random_color,false);
  assert.equal(restored.params.unknown,undefined);
  assert.ok(normalizeHex(effectColor(restored,0,4,.5)));
  assert.equal(snap.zones[1].effect.params.color,'invalid','restore must not mutate the saved scene');
});

test('persistence excludes credentials and recovers from corrupt storage',() => {
  let saved;const storage = {setItem:(key,value) => {saved = value;},getItem:() => saved};
  const state = createState();state.password = 'test-secret';state.host = 'private-host';state.token = 'test-token';
  assert.equal(persistState(state,storage),true);for (const text of ['test-secret','private-host','test-token']) assert.equal(saved.includes(text),false);
  assert.equal(loadState(storage).ports.length,8);saved = '{';assert.equal(loadState(storage).ports.length,8);
  assert.equal(persistState(state,{setItem:() => {throw Error('quota');}}),false);
});
test('rapid edits are serialized and coalesced to the latest value',async () => {
  const writes = [];let unlock;
  const queue = new LatestQueue(async (key,value) => {writes.push([key,value]);if (writes.length === 1) await new Promise(resolve => {unlock = resolve;});},error => {throw error;});
  queue.push('ports',1);queue.push('ports',2);queue.push('ports',3);queue.push('brightness',50);
  unlock();await queue.idle();assert.deepEqual(writes,[['ports',1],['ports',3],['brightness',50]]);
});
test('a failed write cancels queued changes and reports failure once',async () => {
  let unlock;const writes = [],errors = [];
  const queue = new LatestQueue(async (key,value) => {writes.push(value);await new Promise(resolve => {unlock = resolve;});throw Error('offline');},error => errors.push(error.message));
  queue.push('ports',1);queue.push('ports',2);unlock();await queue.idle();assert.deepEqual(writes,[1]);assert.deepEqual(errors,['offline']);
});
test('groups partition ports without dropping ports or changing unrelated effects',() => {
  const state = createState(),group = {id:'desk',name:'Desk',ports:[1,2],effect:defaultConfig('rainbow')};
  const zones = assignGroup(state.zones,group,state.ports.map(p => p.id));
  assert.deepEqual(zones.flatMap(z => z.ports).sort((a,b) => a - b),[1,2,3,4,5,6,7,8]);
  assert.equal(zones[0].effect.id,'gradient');assert.equal(zones[0].name,'Other ports');
  const restored = removeGroup(zones,'desk');assert.deepEqual(restored[0].ports,[1,2,3,4,5,6,7,8]);
  assert.throws(() => removeGroup(restored,restored[0].id));
});

test('standalone group rename preserves enrolled SFP on save and reload',()=>{
  const state=createState();
  state.mode='live';
  const group={id:'desk',name:'Desk',ports:[1,9],effect:defaultConfig('activity_glow')};
  const html=groupEditor(state,group);
  const selected=[...html.matchAll(/name="ports" type="checkbox" value="(\d+)" checked/g)].map(match=>Number(match[1]));
  assert.deepEqual(selected,[1,9]);
  const copper=state.ports.map(port=>port.id),sfp=state.sfp.map(light=>light.port);
  state.zones=assignGroup([group],{...group,name:'Rack',ports:selected},copper,sfp);
  assert.deepEqual(state.zones.find(zone=>zone.id==='desk').ports,[1,9]);
  assert.ok(!state.zones.some(zone=>zone.ports.includes(10)));
  assert.throws(()=>assignGroup(state.zones,{...group,ports:[11]},copper,sfp),/valid ports/);
  const values=new Map(),storage={getItem:key=>values.get(key),setItem:(key,value)=>values.set(key,value)};
  persistState(state,storage);
  assert.deepEqual(loadState(storage).zones.find(zone=>zone.id==='desk').ports,[1,9]);
});

test('an optical-only switch starts without the demo group when selecting its first effect',()=>{
  const state=createState();
  replaceGroups(state,[]);
  assert.equal(state.activeZone,'');
  const selected={id:'fiber',name:'Fiber',ports:[2],effect:defaultConfig('activity_glow')};
  replaceGroups(state,assignGroup(state.zones,selected,[],[1,2,3]));
  assert.equal(state.activeZone,'fiber');
  assert.deepEqual(state.zones,[selected]);
  assert.equal(state.config.id,'activity_glow');
});
