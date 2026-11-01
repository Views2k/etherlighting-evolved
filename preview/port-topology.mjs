export const validPort = id => Number.isInteger(id) && id > 0 && id <= 256;
export const portIds = values => [...new Set((Array.isArray(values) ? values : []).filter(validPort))].sort((a, b) => a - b);

export function lightingPorts(state = {}) {
  const count = state.num_ports ?? state.switch?.num_ports;
  const copper = Array.isArray(state.ports) ? portIds(state.ports.map(port => port?.port))
    : Number.isInteger(count) && count >= 0 && count <= 256 ? Array.from({length:count}, (_, i) => i + 1) : [];
  const sfp = Array.isArray(state.sfp) ? portIds(state.sfp.map(port => port?.port))
    : portIds(state.sfp_ports ?? state.switch?.sfp_ports);
  return {copper, sfp:sfp.filter(id => !copper.includes(id))};
}

export function previewGrid(count) {
  const rows = Math.ceil(count / 12);
  return {rows, columns:rows ? Math.ceil(count / rows) : 0};
}

export function portRanges(values) {
  const ids=portIds(values), ranges=[];
  for (let index=0; index<ids.length; index++) {
    const first=ids[index];
    while (ids[index+1]===ids[index]+1) index++;
    const last=ids[index];
    ranges.push(last-first>1?`${first}–${last}`:last===first?String(first):`${first}, ${last}`);
  }
  return ranges.join(', ');
}

export function sceneTopology(scene) {
  const topology = scene?.topology;
  if (topology?.version !== 1 || !Array.isArray(topology.copper_ports) || !Array.isArray(topology.sfp_ports)) return null;
  const copper = portIds(topology.copper_ports), sfp = portIds(topology.sfp_ports);
  if (copper.length !== topology.copper_ports.length || sfp.length !== topology.sfp_ports.length || sfp.some(id => copper.includes(id))) return null;
  return {copper, sfp};
}
