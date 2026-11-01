import { defaultConfig } from './effects.mjs';
export function assignGroup(zones,group,allPorts) {
  const selected = new Set(group.ports);
  if (!selected.size) throw new Error('Select at least one port.');
  if (group.ports.some(p => !allPorts.includes(p))) throw new Error('Select valid ports.');
  const next = zones.filter(z => z.id !== group.id).map(z => ({...structuredClone(z),ports:z.ports.filter(p => !selected.has(p))})).filter(z => z.ports.length);
  next.forEach(z => {if (z.id === 'all' && z.name === 'All ports' && z.ports.length < allPorts.length) z.name = 'Other ports';});
  next.push(structuredClone(group));
  const assigned = new Set(next.flatMap(z => z.ports));
  const remaining = allPorts.filter(p => !assigned.has(p));
  if (remaining.length) next.push({id:crypto.randomUUID().replaceAll('-','').slice(0,24),name:'Other ports',ports:remaining,effect:defaultConfig()});
  return next;
}
export function removeGroup(zones,id) {
  const removed = zones.find(z => z.id === id),next = structuredClone(zones.filter(z => z.id !== id));
  if (!removed || !next.length) throw new Error('Keep at least one group for your ports.');
  next[0].ports = [...next[0].ports,...removed.ports].sort((a,b) => a - b);
  return next;
}
