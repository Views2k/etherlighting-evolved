import {effectDrafts,portEffect} from './effect-draft.mjs';
import {matchingRule, matchingSfpRule} from '../src/rules.mjs';
import {EFFECTS} from '../src/effects.mjs';
import {editableSfpLight,effectiveSfpLight} from './sfp-sync.mjs';
import {hostScheduleDate} from './schedule-clock.mjs';
import {lightingPorts, validPort} from './port-topology.mjs';

export const copy = value => structuredClone(value);
const same = (first, second) => {
  if (Object.is(first, second)) return true;
  if (!first || !second || typeof first !== 'object' || typeof second !== 'object' || Array.isArray(first) !== Array.isArray(second)) return false;
  const keys = Object.keys(first);
  return keys.length === Object.keys(second).length && keys.every(key => Object.hasOwn(second, key) && same(first[key], second[key]));
};
export function equalSetting(values) {
  return values.length > 0 && values.every(value => same(value, values[0]));
}
export function sceneAutomationMatches(scene, state, portIds, date = hostScheduleDate(state)) {
  const rules = scene.rules || [], sync = scene.sfp_sync || {mode:'independent',source_port:1};
  const sfp = (state.sfp || []).filter(port => Object.hasOwn(scene.sfp || {}, port.port))
    .map(port => ({...port,...scene.sfp[port.port],port:port.port}));
  const projected = {sfp, sfp_sync:sync}, matches = [];
  for (const id of [...new Set(portIds.map(Number))].sort((a,b) => a-b)) {
    const light = sfp.find(port => port.port === id);
    const port = light || state.ports?.find(port => port.port === id);
    if (!port || !light && Object.hasOwn(scene.overrides || {}, id)) continue;
    const rule = light
      ? matchingSfpRule(rules,{...light,mode:scene.zones?.some(zone=>zone.ports.includes(id)) || sync.mode === 'ethernet' ? 'color' : effectiveSfpLight(projected,id).mode},date)
      : matchingRule(rules,{id,facts:port.facts || {}},date);
    if (rule) matches.push({port:id,rule});
  }
  return matches;
}
export function controlSource(state, id, effects = [], date = hostScheduleDate(state)) {
  const sfp = state.sfp?.some(port => port.port === id) === true;
  const port = (sfp ? state.sfp : state.ports)?.find(p => p.port === id);
  if (!port) return {kind:'unknown', label:'Waiting for status'};
  if (!sfp && (state.overrides?.[id] || port.override || port.source?.kind === 'override')) return {kind:'color', label:'Fixed color'};
  const sync = state.sfp_sync || {mode:'independent',source_port:1};
  const shared = sfp ? effectiveSfpLight(state,id) : null;
  const grouped = sfp && portEffect(state,{},id);
  if (!grouped && sfp && sync.mode === 'independent' && port.mode === 'off') return {kind:'off', label:'Off'};
  if (!grouped && sfp && sync.mode === 'together' && shared.mode === 'off') return {kind:'off',label:'SFP lights off'};
  const reportedRule = sfp ? port.rule : port.source?.kind === 'rule' ? port.source : null;
  const reported = sfp ? Object.hasOwn(port, 'rule') : Boolean(port.source);
  const rulePort = grouped ? {...port,mode:'color'} : sfp && sync.mode !== 'independent' ? {...port,mode:sync.mode === 'ethernet' ? 'color' : shared.mode} : port;
  const rule = reportedRule ? state.rules?.find(item => item.id === reportedRule.id) || reportedRule
    : reported ? null : sfp ? matchingSfpRule(state.rules || [], rulePort, date) : matchingRule(state.rules || [], {id,facts:port.facts || {}}, date);
  if (rule) return {kind:'rule', label:rule.name, rule};
  if (grouped) return {kind:'effect',label:effects.find(item=>item.id===grouped.id)?.name || 'Effect',effect:grouped};
  if (sfp && sync.mode === 'ethernet') return {kind:'sync',label:`Following port ${sync.source_port}`};
  if (sfp && sync.mode === 'together') {
    if (shared.mode === 'off') return {kind:'off',label:'SFP lights off'};
    if (shared.mode === 'unmanaged') return {kind:'link',label:'Link status'};
    return {kind:'sfp',label:`Shared ${({steady:'color',pulse:'Gentle pulse',two_color:'Two-color fade',color_cycle:'Color cycle'}[shared.effect] || 'effect')}`};
  }
  if (sfp) return {kind:port.mode === 'off' ? 'off' : port.mode === 'color' ? 'sfp' : 'link', label:port.mode === 'off' ? 'Off' : port.mode === 'color' ? ({steady:'Steady',pulse:'Gentle pulse',two_color:'Two-color fade',color_cycle:'Color cycle'}[port.effect] || 'Custom color') : 'Link status'};
  if (port.source?.kind === 'none') return {kind:'off', label:'Off'};
  const effect = state.zones?.find(zone => zone.ports.includes(id))?.effect;
  if (!effect) return {kind:'off', label:'Off'};
  return {kind:'effect', label:effects.find(item => item.id === effect?.id)?.name || 'Effect', effect};
}
export const sfpSettings = item => Object.fromEntries(['mode','color','brightness','effect','secondary_color','period','color_brightness','secondary_brightness'].filter(key => item[key] !== undefined).map(key => [key,copy(item[key])]));

export function fixedColorSettings(state, id, draft = {}) {
  if (state.sfp?.some(port => port.port === id)) {
    const light = editableSfpLight(state,id,draft);
    return {color:light.color,brightness:light.color_brightness};
  }
  const port = state.ports?.find(port => port.port === id);
  if (!port) throw new Error('Choose an available port.');
  return copy(draft.colors?.[id] ?? state.overrides?.[id] ?? {color:port.color || '#ffffff',brightness:null});
}

export function fixedColorBrightness(settings, state, draft = {}) {
  return settings.brightness ?? draft.brightness ?? state.brightness ?? 100;
}

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const color = value => typeof value === 'string' && /^#[\da-f]{6}$/i.test(value);
function fields(value, allowed, message) {
  if (!object(value) || Object.keys(value).some(key => !allowed.includes(key))) throw new Error(message);
}
function range(value, min, max, message, integer = false) {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max || integer && !Number.isInteger(value)) throw new Error(message);
}
function validateEffect(effect) {
  fields(effect,['id','params','ports','zoneId','clearOverrides'],'Choose valid effect settings.');
  const spec = EFFECTS.find(item => item.id === effect.id);
  if (!spec) throw new Error('Choose an available lighting effect.');
  if (effect.zoneId !== undefined && (typeof effect.zoneId !== 'string' || !effect.zoneId.trim())) throw new Error('Choose a valid effect group.');
  if (effect.clearOverrides !== undefined && typeof effect.clearOverrides !== 'boolean') throw new Error('Choose whether to replace fixed port colors.');
  const params = effect.params === undefined ? {} : effect.params;
  fields(params,spec.params.map(param => param.key),'Choose valid effect parameters.');
  for (const [key,value] of Object.entries(params)) {
    const param = spec.params.find(item => item.key === key), message = `Choose a valid ${param.label || key} value.`;
    if (param.type === 'color') { if (!color(value)) throw new Error(message); }
    else if (param.type === 'bool') { if (typeof value !== 'boolean') throw new Error(message); }
    else if (param.type === 'choice') { if (!param.choices.includes(value)) throw new Error(message); }
    else if (['int','float'].includes(param.type)) range(value,param.min ?? -Infinity,param.max ?? Infinity,message,param.type === 'int');
    else throw new Error(message);
  }
  for (const [left,right] of spec.exclusive_params || []) {
    if (params[left] === true && params[right] === true) throw new Error('Choose one of the conflicting color options.');
  }
}
function validateSfp(settings) {
  fields(settings,['mode','color','brightness','effect','secondary_color','period','color_brightness','secondary_brightness'],'Choose valid SFP lighting settings.');
  for (const [key,value] of Object.entries(settings)) {
    if (['color','secondary_color'].includes(key)) { if (!color(value)) throw new Error('Enter a six-digit SFP color.'); }
    else if (['brightness','color_brightness','secondary_brightness'].includes(key)) range(value,0,100,'SFP brightness must be a whole number from 0 to 100.',true);
    else if (key === 'period') range(value,2,30,'SFP duration must be from 2 to 30 seconds.');
    else if (key === 'mode' && !['unmanaged','color','off'].includes(value)) throw new Error('Choose an available SFP lighting mode.');
    else if (key === 'effect' && !['steady','pulse','two_color','color_cycle'].includes(value)) throw new Error('Choose an available SFP lighting effect.');
  }
}

export function lightingOperations(state, draft) {
  fields(draft,['brightness','effect','effects','returnToEffect','colors','sfp','sfp_sync','sfpDetached','mode'],'Choose valid lighting changes.');
  const operations = [];
  const add = (key, label, path, method, body) => operations.push({key,label,path,method,body});
  const copperPorts = values => {
    const available = lightingPorts(state).copper;
    if (!Array.isArray(values) || values.some(port => !available.includes(port))) throw new Error('Choose Ethernet ports for this change.');
    return [...new Set(values)].sort((a, b) => a - b);
  };
  if (Object.hasOwn(draft,'brightness')) {
    range(draft.brightness,0,100,'Brightness must be a whole number from 0 to 100.',true);
    if (draft.brightness !== state.brightness) add('brightness','Default brightness','/api/brightness','PUT',{value:draft.brightness});
  }
  if (Object.hasOwn(draft,'effects') && (!Array.isArray(draft.effects) || draft.effects.length > 64)) throw new Error('Choose valid effect previews.');
  if (Object.hasOwn(draft,'effect')) validateEffect(draft.effect);
  let effectZones = copy(state.zones || []);
  const topology = lightingPorts(state), available = new Set([...topology.copper,...topology.sfp]);
  if (Object.hasOwn(draft,'sfpDetached')) {
    if (!Array.isArray(draft.sfpDetached) || draft.sfpDetached.some(port=>!topology.sfp.includes(port))) throw new Error('Choose an available SFP port.');
    const detached = new Set(draft.sfpDetached);
    if (effectZones.some(zone=>zone.ports.some(port=>detached.has(port)))) {
      effectZones = effectZones.map(zone=>({...zone,ports:zone.ports.filter(port=>!detached.has(port))})).filter(zone=>zone.ports.length);
      add('group','Return to SFP lighting','/api/zones','PUT',{zones:copy(effectZones)});
    }
  }
  for (const effectDraft of effectDrafts(draft)) {
    validateEffect(effectDraft);
    if (!Array.isArray(effectDraft.ports) || effectDraft.ports.some(port=>!available.has(port))) throw new Error('Choose available lighting ports for this effect.');
    const ports = [...new Set(effectDraft.ports)].sort((a,b)=>a-b), target = new Set(ports), effect = {id:effectDraft.id,params:copy(effectDraft.params || {})};
    if (!ports.length) throw new Error('Select at least one port.');
    const exact = effectZones?.find(zone => zone.ports.length === ports.length && zone.ports.every(port => target.has(port)));
    let id = exact?.id;
    if (!id) {
      const zones = effectZones.map(zone => ({...copy(zone),ports:zone.ports.filter(port => !target.has(port))})).filter(zone => zone.ports.length);
      const base = effectDraft.zoneId || `selection-${ports.join('-')}`, used = new Set(zones.map(zone => zone.id));
      id = base;
      for (let suffix = 2; used.has(id); suffix++) id = `${base}-${suffix}`;
      zones.push({id,name:`Ports ${ports.join(', ')}`,ports:[...ports],effect});
      effectZones = zones;
      add('group','Selected port group','/api/zones','PUT',{zones});
    }
    effectZones.find(zone => zone.id === id).effect = copy(effect);
    add('effect',effectDraft.clearOverrides ? 'Effect and selected port colors' : 'Effect',`/api/zones/${encodeURIComponent(id)}/effect`,'PUT',{...effect,clear_overrides:effectDraft.clearOverrides === true});
  }
  for (const port of copperPorts(draft.returnToEffect ?? [])) add(`return:${port}`,`Port ${port}: follow effect`,`/api/ports/${port}`,'DELETE');
  if (Object.hasOwn(draft,'returnToEffect') && !Array.isArray(draft.returnToEffect)) throw new Error('Choose Ethernet ports for this change.');
  if (Object.hasOwn(draft,'colors')) {
    if (!object(draft.colors)) throw new Error('Choose valid fixed port colors.');
    if (Object.keys(draft.colors).some(port => String(Number(port)) !== port)) throw new Error('Choose Ethernet ports for this change.');
    for (const settings of Object.values(draft.colors)) {
      fields(settings,['color','brightness'],'Choose valid fixed port settings.');
      if (!color(settings.color)) throw new Error('Enter a six-digit port color.');
      if (settings.brightness !== undefined && settings.brightness !== null) range(settings.brightness,0,100,'Port brightness must be a whole number from 0 to 100.',true);
    }
  }
  if (Object.keys(draft.colors || {}).length) {
    copperPorts(Object.keys(draft.colors).map(Number));
    add('colors','Selected port colors','/api/ports','PUT',{ports:copy(draft.colors)});
  }
  if (Object.hasOwn(draft,'sfp') && !object(draft.sfp)) throw new Error('Choose valid SFP lighting changes.');
  for (const [port, settings] of Object.entries(draft.sfp || {})) {
    if (String(Number(port)) !== port || !state.sfp?.some(item => item.port === Number(port))) throw new Error('Choose an available SFP port.');
    validateSfp(settings);
    const changes = sfpSettings(settings);
    if (Object.keys(changes).length) add(`sfp:${port}`,`SFP ${port}`,`/api/sfp/${port}`,'PUT',changes);
  }
  if (Object.hasOwn(draft,'sfp_sync')) {
    fields(draft.sfp_sync,['mode','source_port'],'Choose valid SFP sync settings.');
    const sync = {...(state.sfp_sync || {mode:'independent',source_port:1}),...draft.sfp_sync};
    if (!['independent','together','ethernet'].includes(sync.mode)) throw new Error('Choose an available SFP sync mode.');
    const available = lightingPorts(state).copper;
    const validSource = available.length ? available.includes(sync.source_port) : sync.mode !== 'ethernet' && sync.source_port === 1;
    if (!validPort(sync.source_port) || !validSource) throw new Error('Choose an Ethernet port to follow.');
    add('sfp-sync','SFP synchronization','/api/sfp/sync','PUT',sync);
  }
  if (Object.hasOwn(draft,'mode')) {
    if (!['running','released'].includes(draft.mode)) throw new Error('Choose an available lighting mode.');
    add('mode',draft.mode === 'running' ? 'Enable EE lighting' : 'Return lighting to UniFi',draft.mode === 'running' ? '/api/resume' : '/api/release','POST');
  }
  return operations;
}

export function lightingSnapshot(baseScene, state, draft) {
  if (!object(baseScene) || !Array.isArray(baseScene.zones) || !Array.isArray(baseScene.rules)
      || !object(baseScene.overrides) || !object(baseScene.sfp)) throw new Error('Refresh the lighting setup before applying changes.');
  const scene = copy(baseScene);
  const operations = lightingOperations({...state,zones:scene.zones,brightness:scene.brightness,sfp_sync:scene.sfp_sync},draft);
  for (const operation of operations) {
    const {key,body} = operation;
    if (key === 'brightness') scene.brightness = body.value;
    else if (key === 'group') scene.zones = copy(body.zones);
    else if (key === 'effect') {
      const id = decodeURIComponent(operation.path.split('/')[3]);
      const zone = scene.zones.find(item => item.id === id);
      if (!zone) throw new Error('The selected group changed. Review the current lighting setup.');
      zone.effect = {id:body.id,params:copy(body.params)};
      if (body.clear_overrides) for (const port of zone.ports) delete scene.overrides[port];
    } else if (key.startsWith('return:')) delete scene.overrides[key.slice(7)];
    else if (key === 'colors') Object.assign(scene.overrides,copy(body.ports));
    else if (key === 'sfp-sync') scene.sfp_sync = copy(body);
    else if (key.startsWith('sfp:')) {
      const port = key.slice(4);
      if (!object(scene.sfp[port])) throw new Error('The selected SFP light changed. Refresh its settings.');
      scene.sfp[port] = {...scene.sfp[port],...copy(body)};
    }
  }
  return scene;
}

export async function applyLightingPlan(pending, send, confirmed = () => {}) {
  while (pending.length) {
    const item = pending[0];
    await send(item.path,item.method,item.body);
    pending.shift();
    await confirmed(item);
  }
}
