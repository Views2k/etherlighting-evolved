import { defaultConfig } from './effects.mjs';
export function replaceGroups(state,zones) {
  state.zones = structuredClone(zones);
  const current = state.zones.find(zone => zone.id === state.activeZone) || state.zones[0];
  state.activeZone = current?.id ?? '';
  state.config = current ? structuredClone(current.effect) : defaultConfig();
}
export function assignGroup(zones,group,copperPorts,sfpPorts = []) {
  const selected = new Set(group.ports);
  if (!selected.size) throw new Error('Select at least one port.');
  const eligible = new Set([...copperPorts,...sfpPorts]);
  if (group.ports.some(p => !eligible.has(p))) throw new Error('Select valid ports.');
  const next = zones.filter(z => z.id !== group.id).map(z => ({...structuredClone(z),ports:z.ports.filter(p => !selected.has(p))})).filter(z => z.ports.length);
  next.forEach(z => {if (z.id === 'all' && z.name === 'All ports' && z.ports.length < copperPorts.length) z.name = 'Other ports';});
  next.push(structuredClone(group));
  const assigned = new Set(next.flatMap(z => z.ports));
  const remaining = copperPorts.filter(p => !assigned.has(p));
  if (remaining.length) next.push({id:crypto.randomUUID().replaceAll('-','').slice(0,24),name:'Other ports',ports:remaining,effect:defaultConfig()});
  return next;
}
export function removeGroup(zones,id) {
  const removed = zones.find(z => z.id === id),next = structuredClone(zones.filter(z => z.id !== id));
  if (!removed || !next.length) throw new Error('Keep at least one group for your ports.');
  next[0].ports = [...next[0].ports,...removed.ports].sort((a,b) => a - b);
  return next;
}
