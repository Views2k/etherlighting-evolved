import { defaultConfig, effectById } from './effects.mjs';
import { normalizeHex, clamp } from './color.mjs';
import { FIELDS } from './rules.mjs';
export const STORAGE_KEY = 'ee:workspace:v2';
export const VIEW_STORAGE_KEY = 'ee:view:v1';
export const PAGES = ['studio','effects','groups','rules','scenes','activity','settings'];
export const SFP_DEFAULTS = Object.freeze({mode:'unmanaged',color:'#FFFFFF',brightness:100,effect:'steady',period:6,
  secondary_color:'#006FFF',color_brightness:100,secondary_brightness:100});
export const SFP_EFFECTS = ['steady','pulse','color_cycle','two_color'];
const sfpLevel = (value,fallback = 100) => Number.isFinite(value) ? Math.round(clamp(value,0,100)) : fallback;
function sfpFacts(value) {
  if (!value || typeof value !== 'object') return {};
  const facts = {};
  for (const field of FIELDS) {
    const item = value[field.key];
    if (item === null || field.type === 'bool' && typeof item === 'boolean'
      || field.type === 'number' && Number.isFinite(item)
      || field.type === 'choice' && field.choices.includes(item)) facts[field.key] = item;
  }
  return facts;
}
export function sanitizeSfp(items) {
  const seen = new Set();
  return (Array.isArray(items) ? items : []).filter(item => {
    if (!Number.isInteger(item?.port) || item.port < 1 || item.port > 256 || seen.has(item.port)) return false;
    seen.add(item.port);return true;
  }).map(item => ({port:item.port,mode:['unmanaged','color','off'].includes(item.mode) ? item.mode : 'unmanaged',
    color:normalizeHex(item.color) || '#FFFFFF',brightness:sfpLevel(item.brightness),
    effect:SFP_EFFECTS.includes(item.effect) ? item.effect : 'steady',period:Number.isFinite(item.period) ? clamp(item.period,2,30) : 6,
    secondary_color:normalizeHex(item.secondary_color) || '#006FFF',color_brightness:sfpLevel(item.color_brightness),secondary_brightness:sfpLevel(item.secondary_brightness),
    link:typeof item.link === 'boolean' ? item.link : null,anomaly:typeof item.anomaly === 'boolean' ? item.anomaly : null,
    ...(item.rule && typeof item.rule.id === 'string' && typeof item.rule.name === 'string'
      ? {rule:{id:item.rule.id,name:item.rule.name}} : {}),
    ...(normalizeHex(item.preview) ? {preview:normalizeHex(item.preview)} : {}),
    ...(item.facts ? {facts:sfpFacts(item.facts)} : {}),
    revision:Number.isInteger(item.revision) && item.revision >= 0 ? item.revision : 0,
    acknowledged_revision:Number.isInteger(item.acknowledged_revision) && item.acknowledged_revision >= 0 ? item.acknowledged_revision : 0,
    control:{state:['idle','active','pending','uncertain'].includes(item.control?.state) ? item.control.state : item.mode === 'color' || item.mode === 'off' ? 'pending' : 'idle',
      owned:item.control?.owned === true,...(item.control?.error ? {error:true} : {})}}));
}
export const sfpSettings = item => Object.fromEntries(Object.entries(SFP_DEFAULTS).map(([key,value]) => [key,item[key] ?? value]));
export function demoSfp(items) {
  return sanitizeSfp(items).map(item => ({...item,revision:0,acknowledged_revision:0,
    control:{state:item.mode === 'unmanaged' ? 'idle' : 'active',owned:item.mode !== 'unmanaged'}}));
}
export function sfpControlStatus(item) {
  if (item.pending) return {text:'Applying…',retry:false};
  if (item.control?.state === 'uncertain' || item.control?.error) return {text:'Change not confirmed.',retry:true};
  if (item.control?.state === 'pending') return {text:'Applying…',retry:false};
  const confirmed = item.acknowledged_revision >= item.revision;
  if (item.mode === 'unmanaged' && !item.rule ? item.control?.state === 'idle' && !item.control.owned && confirmed
    : item.control?.state === 'active' && item.control.owned && confirmed) return {text:'',retry:false};
  return {text:'Waiting for confirmation…',retry:false};
}
export class SfpEdits {
  constructor(items = []) {this.pending = new Map();this.revision = 0;this.generation = 0;this.reset(items);}
  get values() {return this.confirmed.map(item => ({...item,...this.pending.get(item.port)?.body,pending:this.pending.has(item.port)}));}
  reset(items) {this.generation++;this.pending.clear();this.confirmed = [];return this.receive(items);}
  receive(items,{factsOnly = false} = {}) {
    const incoming = sanitizeSfp(items).map(item => {
      const previous = this.confirmed?.find(value => value.port === item.port);
      if (previous && (item.revision < previous.revision || (item.revision === previous.revision &&
          item.acknowledged_revision < previous.acknowledged_revision && item.control.state !== 'uncertain'))) {
        return {...previous,link:item.link,anomaly:item.anomaly,...(item.facts ? {facts:item.facts} : {})};
      }
      return item;
    });
    if (factsOnly) this.confirmed = this.confirmed.map(item => {
      const update = incoming.find(next => next.port === item.port);
      if (!update) return item;
      const {rule,preview,facts,...saved} = item;
      return {...saved,link:update.link,anomaly:update.anomaly,control:update.control,
        ...(update.rule ? {rule:update.rule} : {}),...(update.preview ? {preview:update.preview} : {}),
        ...(update.facts ? {facts:update.facts} : {}),
        revision:update.revision,acknowledged_revision:update.acknowledged_revision};
    });
    else {
      this.confirmed = incoming;
      for (const port of this.pending.keys()) if (!incoming.some(item => item.port === port)) this.pending.delete(port);
    }
    return this.values;
  }
  edit(port,patch) {
    const current = this.values.find(item => item.port === port);
    if (!current || !patch || !Object.keys(patch).length) return null;
    if (Object.keys(patch).some(key => !Object.hasOwn(SFP_DEFAULTS,key))) return null;
    if ('mode' in patch && !['unmanaged','color','off'].includes(patch.mode)) return null;
    if (['color','secondary_color'].some(key => key in patch && !normalizeHex(patch[key]))) return null;
    if (['brightness','color_brightness','secondary_brightness'].some(key => key in patch && (!Number.isInteger(patch[key]) || patch[key] < 0 || patch[key] > 100))) return null;
    if ('period' in patch && (!Number.isFinite(patch.period) || patch.period < 2 || patch.period > 30)) return null;
    if ('effect' in patch && !SFP_EFFECTS.includes(patch.effect)) return null;
    const body = sfpSettings(sanitizeSfp([{...current,...patch}])[0]);
    const request = {port,body,revision:++this.revision,generation:this.generation};
    this.pending.set(port,request);return request;
  }
  acknowledge(request,items) {
    if (request.generation !== this.generation) return false;
    if (this.pending.get(request.port)?.revision === request.revision) this.pending.delete(request.port);
    this.receive(items);return true;
  }
  rollback() {this.pending.clear();return this.values;}
}
export class MutationRecovery {
  constructor({generation,read,apply,onFailure,onPending}) {
    this.generation = generation;this.read = read;this.apply = apply;this.onFailure = onFailure;this.onPending = onPending;
    this.pending = false;this.running = null;
  }
  async run() {
    if (this.running) return this.running;
    const generation = this.generation();this.pending = true;this.onPending(true);
    this.running = this.restore(generation);
    try {await this.running;} finally {this.running = null;}
  }
  async restore(generation) {
    try {const data = await this.read();if (generation === this.generation()) this.apply(data);}
    catch (error) {if (generation === this.generation()) this.onFailure(error);}
    finally {this.pending = false;this.onPending(false);}
  }
}
export function restoreView(state,storage,route = '') {
  try {
    const saved = JSON.parse(storage.getItem(VIEW_STORAGE_KEY));
    if (saved && typeof saved === 'object') {
      if (PAGES.includes(saved.page)) state.page = saved.page;
      if (typeof saved.activeZone === 'string') state.activeZone = saved.activeZone;
      if (['effect','color'].includes(saved.inspector)) state.inspector = saved.inspector;
      if (['ethernet','sfp'].includes(saved.deviceTab)) state.deviceTab = saved.deviceTab;
      if (Array.isArray(saved.selected)) state.selected = [...new Set(saved.selected.filter(p => Number.isInteger(p) && p > 0 && p <= 128))];
      for (const key of ['motion','labels']) if (typeof saved.preferences?.[key] === 'boolean') state.preferences[key] = saved.preferences[key];
    }
  } catch { /* Invalid view state falls back to the default workspace. */ }
  const page = route.replace(/^#/,'');
  if (PAGES.includes(page)) state.page = page;
  return state;
}
export function persistView(state,storage) {
  try {storage.setItem(VIEW_STORAGE_KEY,JSON.stringify({page:state.page,activeZone:state.activeZone,selected:state.selected,inspector:state.inspector || 'effect',deviceTab:state.deviceTab || 'ethernet',preferences:{motion:state.preferences.motion,labels:state.preferences.labels}}));return true;} catch {return false;}
}
export function createState() {
  return {
    version:2,page:'studio',deviceTab:'ethernet',selected:[1,2,3,4,5,6,7,8],config:defaultConfig(),brightness:80,
    lightsOn:true,mode:'simulator',rules:[],scenes:[],activity:[],activeZone:'all',
    sfp:sanitizeSfp([{port:9,link:true},{port:10,link:false}]),
    zones:[{id:'all',name:'All ports',ports:[1,2,3,4,5,6,7,8],effect:defaultConfig()}],
    ports:Array.from({length:8},(_,i) => ({id:i + 1,name:`Port ${i + 1}`,override:null,preview:null,
      facts:{link:![5,7].includes(i),speed:[10000,10000,2500,1000,5000,0,1000,0][i],poe:['off','poe++','poe+','poe','off','off','poe+++','off'][i],poe_power:[0,42,18,9,0,0,65,0][i],anomaly:false,full_duplex:true,uplink:i === 0,lag:false,stp_state:'forwarding',traffic:i * 54}})),
    preferences:{motion:true,labels:true},simulationTime:null
  };
}
export function sanitizeConfig(value = {}) {
  const base = defaultConfig(value.id);
  for (const param of effectById(base.id).params) {
    const v = value.params?.[param.key];
    if (param.type === 'color') base.params[param.key] = normalizeHex(v) || base.params[param.key];
    else if (param.type === 'bool') base.params[param.key] = typeof v === 'boolean' ? v : param.default;
    else if (param.type === 'choice') base.params[param.key] = param.choices.includes(v) ? v : param.default;
    else if (Number.isFinite(v)) base.params[param.key] = clamp(v,param.min ?? 0,param.max ?? 100);
  }
  return base;
}
export function loadState(storage) {
  const state = createState();
  try {
    const data = JSON.parse(storage.getItem(STORAGE_KEY));
    if (!data || data.version !== 2) return state;
    state.config = sanitizeConfig(data.config);
    if (Array.isArray(data.sfp)) state.sfp = demoSfp(data.sfp);
    if (Array.isArray(data.zones) && data.zones.length) {
      const eligible = new Set([...state.ports.map(port => port.id),...state.sfp.map(light => light.port)]);
      const assigned = new Set();
      state.zones = data.zones.filter(z => typeof z.id === 'string' && typeof z.name === 'string' && Array.isArray(z.ports)).map(z => ({id:z.id,name:z.name.slice(0,40),effect:sanitizeConfig(z.effect),ports:z.ports.filter(p => {if (!eligible.has(p) || assigned.has(p)) return false;assigned.add(p);return true;})})).filter(z => z.ports.length);
      const unassigned = state.ports.map(p => p.id).filter(p => !assigned.has(p));
      if (unassigned.length) state.zones.push({id:'remaining',name:'Other ports',ports:unassigned,effect:defaultConfig()});
      state.activeZone = state.zones[0].id;state.config = structuredClone(state.zones[0].effect);
    }
    state.brightness = Number.isFinite(data.brightness) ? clamp(data.brightness,0,100) : 80;
    if (Array.isArray(data.ports) && data.ports.length === 8) state.ports.forEach((p,i) => {
      const o = data.ports[i]?.override;
      if (normalizeHex(o?.color)) p.override = {color:normalizeHex(o.color),brightness:Number.isFinite(o.brightness) ? clamp(o.brightness,0,100) : null};
    });
    if (Array.isArray(data.scenes)) state.scenes = data.scenes.filter(s => typeof s.id === 'string' && typeof s.name === 'string' && s.snapshot).slice(0,50);
    if (Array.isArray(data.rules)) state.rules = data.rules.filter(r => typeof r.id === 'string' && typeof r.name === 'string' && normalizeHex(r.look?.color) && Array.isArray(r.conditions)).slice(0,100);
    if (data.preferences) state.preferences = {motion:data.preferences.motion !== false,labels:data.preferences.labels !== false};
    return state;
  } catch { return state; }
}
export function persistState(state,storage) {
  try { const {version,config,brightness,ports,rules,scenes,preferences,zones,sfp} = state;
    storage.setItem(STORAGE_KEY,JSON.stringify({version,config,brightness,ports:ports.map(p => ({override:p.override})),rules,scenes,preferences,zones,
      sfp:sanitizeSfp(sfp).map(item => ({port:item.port,...sfpSettings(item),link:item.link,anomaly:item.anomaly}))})); return true;
  } catch { return false; }
}
export function snapshot(state) {
  return structuredClone({effect:state.config.id,params:state.config.params,zones:state.zones,brightness:state.brightness,rules:state.rules,
    sfp:Object.fromEntries(sanitizeSfp(state.sfp).map(item => [item.port,sfpSettings(item)])),
    overrides:Object.fromEntries(state.ports.filter(p => p.override).map(p => [String(p.id),p.override]))});
}
export function applySnapshot(state,snap) {
  state.config = sanitizeConfig({id:snap.effect,params:snap.params}); state.brightness = clamp(snap.brightness ?? 80,0,100);
  state.zones = structuredClone(snap.zones || [{id:'all',name:'All ports',ports:state.ports.map(p => p.id),effect:state.config}]).map(zone => ({...zone,effect:sanitizeConfig(zone.effect)}));
  state.activeZone = state.zones[0].id;state.config = sanitizeConfig(state.zones[0].effect);
  state.rules = structuredClone(snap.rules || []);
  state.ports.forEach(p => {p.override = snap.overrides?.[p.id] ? structuredClone(snap.overrides[p.id]) : null;});
  state.sfp = demoSfp((state.sfp || []).map(item => ({...item,...SFP_DEFAULTS,
    ...(snap.sfp?.[item.port] ? sfpSettings(snap.sfp[item.port]) : {})})));
}
