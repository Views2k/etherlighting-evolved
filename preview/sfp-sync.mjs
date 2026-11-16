import {effectDrafts,portEffect} from './effect-draft.mjs';

export function sfpSync(state = {}, draft = {}) {
  return {mode:'independent',source_port:1,...state.sfp_sync,...draft.sfp_sync};
}

function sfpLights(state) {
  const lights=Array.isArray(state.sfp)?state.sfp:Object.entries(state.sfp||{}).map(([id,light])=>({...light,port:Number(id)}));
  return lights.filter(light=>Number.isInteger(light?.port)&&light.port>0);
}

function sfpIds(state) {
  return [...new Set(sfpLights(state).map(light=>light.port))].sort((first,second)=>first-second);
}

export function sfpLight(state, port, draft = {}) {
  const lights = sfpLights(state);
  return {...lights.find(light => light.port === port),...draft.sfp?.[port],port};
}

export function effectiveSfpLight(state, port, draft = {}) {
  const sync = sfpSync(state,draft), target = sfpLight(state,port,draft), ids=sfpIds(state);
  if (sync.mode !== 'together'||!ids.includes(port)) return target;
  const shared = sfpLight(state,ids[0],draft);
  return {...target,...Object.fromEntries(['mode','color','brightness','effect','secondary_color','period','color_brightness','secondary_brightness'].map(key => [key,shared[key]]))};
}

export function sfpEditTargets(state, ports, draft = {}) {
  const ids=sfpIds(state),selected=[...new Set((ports||[]).filter(port=>ids.includes(port)))];
  if(!selected.length)return [];
  return sfpSync(state,draft).mode === 'together' ? [ids[0]] : selected;
}

export function editableSfpLight(state, port, draft = {}) {
  return normalizeSfpBrightness(effectiveSfpLight(state,port,draft));
}

export function normalizeSfpBrightness(light) {
  const scale=(light.brightness??100)/100;
  return {...light,brightness:100,color_brightness:Math.round((light.color_brightness??100)*scale),secondary_brightness:Math.round((light.secondary_brightness??100)*scale)};
}

const nativePeriods = {pulse:[2,4,6],two_color:[2,4,6],color_cycle:[6,12,18]};

export function sfpNativeAvailable(state) {
  return state.switch?.sfp_native_gradient === true && sfpLights(state).some(light=>light.playback);
}

export function sfpNativePeriod(effect, period) {
  return nativePeriods[effect]?.includes(period) ?? false;
}

export function sfpDurationRange(effect, period, native = true) {
  const stops = native ? nativePeriods[effect] : null;
  const current = Number.isFinite(period) ? period : 6;
  if (!stops) return {min:2, max:30, step:.1, value:current, savedCustom:false};
  const value = stops.reduce((best, item) => Math.abs(item-current)<Math.abs(best-current)?item:best);
  return {min:stops[0], max:stops.at(-1), step:stops[1]-stops[0], value, savedCustom:!stops.includes(current)};
}

export function sfpEffectChange(light, effect, native = true) {
  return {effect,period:!native||effect===light.effect||effect==='steady'||sfpNativePeriod(effect,light.period)?light.period:6};
}

export function sfpCandidateSettings(state, port, effect, draft = {}) {
  const sync=sfpSync(state,draft);
  const candidate=sync.mode==='ethernet'?{...draft,sfp_sync:{...sync,mode:'together'}}:draft;
  const light=effectiveSfpLight(state,port,candidate);
  return {...light,...sfpEffectChange(light,effect,sfpNativeAvailable(state))};
}

export function sfpDraftPorts(state, draft = {}) {
  const sync=sfpSync(state,draft),ids=sfpIds(state),changed=Object.keys(draft.sfp || {}).map(Number).filter(port=>ids.includes(port));
  const detached=(draft.sfpDetached || []).filter(id=>ids.includes(id));
  const synced=ids.filter(port=>!portEffect(state,draft,port)||changed.includes(port));
  if(draft.sfp_sync) return synced;
  if(sync.mode==='together'&&changed.includes(ids[0]))return synced;
  if(sync.mode==='ethernet') {
    const source=sync.source_port;
    if(draft.colors?.[source]||effectDrafts(draft).some(effect=>effect.ports?.includes(source))||draft.returnToEffect?.includes(source))return synced;
  }
  const changedPorts=sync.mode==='independent'?changed:changed.filter(id=>state.zones?.some(zone=>zone.ports.includes(id)));
  return [...new Set([...changedPorts,...detached])];
}
