export function effectDrafts(draft = {}) {
  return [...(draft.effect ? [draft.effect] : []), ...(draft.effects || [])];
}

export function effectDraftForPort(draft, port) {
  return effectDrafts(draft).findLast(effect => effect.ports?.includes(port));
}

export function stageEffectDraft(draft, config, newId) {
  const ports = [...new Set(config.ports)].sort((a, b) => a - b), targets = new Set(ports);
  const existing = effectDrafts(draft), returned = new Set(draft.returnToEffect || []);
  const previous = existing.find(effect => effect.ports.length === ports.length && effect.ports.every(port => targets.has(port)));
  const inheritedClears = ports.filter(port => returned.has(port) || effectDraftForPort(draft, port)?.clearOverrides);
  const clearOverrides = config.clearOverrides ?? (ports.length > 0 && inheritedClears.length === ports.length);
  const next = {...structuredClone(config), ports, clearOverrides, zoneId: previous?.zoneId || newId};
  const effects = existing.map(effect => ({...structuredClone(effect), ports: effect.ports.filter(port => !targets.has(port))}))
    .filter(effect => effect.ports.length);
  effects.push(next);
  const result = {...draft, effects};
  delete result.effect;
  if (config.clearOverrides === true) {
    result.colors = Object.fromEntries(Object.entries(draft.colors || {}).filter(([port]) => !targets.has(Number(port))));
    result.returnToEffect = (draft.returnToEffect || []).filter(port => !targets.has(port));
  } else if (config.clearOverrides === undefined && !clearOverrides && inheritedClears.length) {
    result.returnToEffect = [...new Set([...(draft.returnToEffect || []), ...inheritedClears])].sort((a, b) => a - b);
  }
  return result;
}
