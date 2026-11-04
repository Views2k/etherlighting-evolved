export function effectDrafts(draft = {}) {
  return [...(draft.effect ? [draft.effect] : []), ...(draft.effects || [])];
}

export function effectDraftForPort(draft, port) {
  return effectDrafts(draft).findLast(effect => effect.ports?.includes(port));
}

export function portEffect(state, draft, port) {
  if (draft.sfpDetached?.includes(port)) return undefined;
  return effectDraftForPort(draft,port) || state.zones?.find(zone=>zone.ports.includes(port))?.effect;
}

export function leaveSfpEffect(draft, ports) {
  if (!ports.length) return draft;
  const removed = new Set(ports);
  const effects = effectDrafts(draft).map(effect=>({...effect,ports:effect.ports.filter(id=>!removed.has(id))})).filter(effect=>effect.ports.length);
  const result = {...draft,effects,sfpDetached:[...new Set([...(draft.sfpDetached || []),...ports])]};
  delete result.effect;
  return result;
}

export function stageEffectDraft(draft, config, newId, copperPorts = config.ports) {
  const ports = [...new Set(config.ports)].sort((a, b) => a - b), targets = new Set(ports);
  const existing = effectDrafts(draft), returned = new Set(draft.returnToEffect || []);
  const previous = existing.find(effect => effect.ports.length === ports.length && effect.ports.every(port => targets.has(port)));
  const copper = ports.filter(port=>copperPorts.includes(port));
  const inheritedClears = copper.filter(port => returned.has(port) || effectDraftForPort(draft, port)?.clearOverrides);
  const clearOverrides = config.clearOverrides ?? (copper.length > 0 && inheritedClears.length === copper.length);
  const next = {...structuredClone(config), ports, clearOverrides, zoneId: previous?.zoneId || newId};
  const effects = existing.map(effect => ({...structuredClone(effect), ports: effect.ports.filter(port => !targets.has(port))}))
    .filter(effect => effect.ports.length);
  effects.push(next);
  const result = {...draft, effects};
  delete result.effect;
  if (result.sfpDetached) result.sfpDetached = result.sfpDetached.filter(port=>!targets.has(port));
  if (config.clearOverrides === true) {
    result.colors = Object.fromEntries(Object.entries(draft.colors || {}).filter(([port]) => !targets.has(Number(port))));
    result.returnToEffect = (draft.returnToEffect || []).filter(port => !targets.has(port));
  } else if (config.clearOverrides === undefined && !clearOverrides && inheritedClears.length) {
    result.returnToEffect = [...new Set([...(draft.returnToEffect || []), ...inheritedClears])].sort((a, b) => a - b);
  }
  return result;
}
