import {lightingPorts} from './port-topology.mjs';

export const LINK_SPEEDS = [[100, 'FE'], [1000, 'GbE'], [2500, '2.5 GbE'], [5000, '5 GbE'], [10000, '10 GbE'], [25000, '25 GbE'], [40000, '40 GbE'], [100000, '100 GbE']];
export const GROUP_NAME_LIMIT = 40;

const text = value => typeof value === 'string' ? value.trim() : '';
const samePorts = (first, second) => first.length === second.length && first.every(port => second.includes(port));
export const isUplink = port => port.link?.uplink === true || port.facts?.uplink === true;

export function portLabel(port, sfp = false) {
  const display=text(port.display_name);
  if(display)return {lines:[display],text:display};
  if(port.name_mode==='manual'){const fallback=`${sfp?'SFP':'Port'} ${port.port}`;return {lines:[fallback],text:fallback};}
  if (isUplink(port)) return {lines: ['Uplink'], text: 'Uplink'};
  const clients = port.controller?.clients;
  let lines;
  if (Array.isArray(clients) && clients.length) {
    const assigned = clients.filter(client => client?.name_source === 'assigned' && text(client.name));
    lines = (assigned.length ? assigned : clients).map(client => text(client?.name)).sort((first, second) => {
      if (!first || !second) return Number(!first) - Number(!second);
      return first.toLocaleLowerCase('en-US').localeCompare(second.toLocaleLowerCase('en-US'), 'en-US')
        || first.localeCompare(second, 'en-US');
    }).map(name => name || 'Unnamed device');
    const count = port.controller.count;
    if (port.controller.clients_truncated && Number.isInteger(count) && count > clients.length) lines.push(`+${count - clients.length} more`);
  } else {
    lines = [text(port.name) || text(port.label) || `${sfp ? 'SFP' : 'Port'} ${port.port}`];
  }
  return {lines, text: lines.join(', ')};
}

export function linkStatus(port) {
  if (port.link?.admin_up === false) return {key: 'disabled', label: 'Disabled', online: false};
  const up = typeof port.link === 'boolean' ? port.link : port.link?.link_up ?? port.facts?.link;
  if (up === false) return {key: 'offline', label: 'Disconnected', online: false};
  if (up !== true) return {key: 'unknown', label: 'Not reported', online: null};
  const speed = port.link?.speed_mbps ?? port.facts?.speed;
  const known = LINK_SPEEDS.find(([rate]) => rate === speed);
  return {key: known ? speed : speed === 10 ? 100 : 'other',
    label: known?.[1] || (Number.isFinite(speed) && speed > 0 ? speed >= 1000 ? `${speed / 1000} GbE` : `${speed} Mbps` : 'Connected'), online: true};
}

export function portPower(port) {
  const poe = port.poe;
  if (poe?.delivering === false) return {delivering: false, watts: '', description: 'Not supplying power'};
  if (poe?.delivering !== true) return {delivering: null, watts: '', description: 'Not reported'};
  const watts = Number.isFinite(poe.power_w) && poe.power_w >= 0 ? `${poe.power_w.toFixed(1)} W` : '';
  return {delivering: true, watts, description: watts || 'Power usage not reported'};
}

export function portScopeOptions(state) {
  const {copper, sfp} = lightingPorts(state);
  const scopes = [{value: '__allports__', label: 'All ports', ports: [...copper, ...sfp]}];
  if (sfp.length && copper.length) scopes.push({value: '__all__', label: 'All Ethernet ports', ports: copper}, {value: '__sfp__', label: 'SFP ports', ports: sfp});
  const reserved = new Set(['all ports', 'all ethernet ports', 'sfp ports', 'selected ports']);
  const groups = (state.zones || []).filter(zone => !(zone.id === 'all'
    && ['all ports', 'all ethernet ports'].includes(text(zone.name).toLowerCase()) && samePorts(zone.ports, copper)))
    .map(zone => ({value: zone.id, label: reserved.has(text(zone.name).toLowerCase()) ? `Group: ${zone.name}` : zone.name, ports: zone.ports, group: true}));
  return [...scopes, ...groups];
}

export function groupNameError(value) {
  const name = text(value);
  return !name ? 'Enter a name for each group.' : name.length > GROUP_NAME_LIMIT ? `Group names can be up to ${GROUP_NAME_LIMIT} characters.` : '';
}

export function selectPort({ports, selected, anchor, port, additive = false, range = false}) {
  if (!ports.includes(port)) return {ports: [...selected], anchor};
  if (range && ports.includes(anchor)) {
    const first = ports.indexOf(anchor), last = ports.indexOf(port);
    const slice = ports.slice(Math.min(first, last), Math.max(first, last) + 1);
    return {ports: [...new Set(additive ? [...selected, ...slice] : slice)], anchor};
  }
  const next = new Set(additive ? selected : []);
  if (next.has(port)) next.delete(port); else next.add(port);
  return {ports: [...next], anchor: port};
}

export function selectionHint(platform, multiple = false, touch = false) {
  if (touch || /iphone|ipad|android/i.test(platform || '')) return multiple
    ? 'Tap ports to add or remove them.' : 'Tap a port to select it. Use Select multiple to choose several.';
  if (multiple) return 'Select ports to add or remove them. Shift-click selects a range.';
  return `${/mac/i.test(platform || '') ? 'Command' : 'Ctrl'}-click to add or remove ports. Shift-click selects a range.`;
}

export function switchTitle(state) {
  return text(state.switch?.model) || text(state.controller?.switch_name) || 'UniFi switch';
}
