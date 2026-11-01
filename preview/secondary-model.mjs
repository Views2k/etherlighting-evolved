import {effectiveSfpLight,sfpSync} from './sfp-sync.mjs';

const clone = value => structuredClone(value);
const sfpKeys = ['mode','color','brightness','effect','period','secondary_color','color_brightness','secondary_brightness'];
const normalize = value => Array.isArray(value) ? value.map(normalize) : value && typeof value === 'object'
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, normalize(value[key])]))
  : typeof value === 'string' && /^#[a-f\d]{6}$/i.test(value) ? value.toLowerCase() : value;

export function profileNameError(value) {
  const name = String(value).trim();
  if (name === '.' || name === '..') return 'Use a profile name other than a single or double period.';
  return /^[\p{L}\p{N}_. \-]{1,64}$/u.test(name) ? ''
    : 'Use 1–64 letters, numbers, spaces, periods, underscores, or hyphens.';
}

export function sceneFingerprint(scene = {}) {
  const sfp = Array.isArray(scene.sfp)
    ? Object.fromEntries(scene.sfp.map(light => [light.port, light])) : scene.sfp || {};
  return JSON.stringify(normalize({
    brightness: scene.brightness ?? 100,
    zones: (scene.zones || []).map(zone => ({id: zone.id, name: zone.name,
      ports: [...zone.ports].sort((a,b) => a-b), effect: {id: zone.effect.id, params: zone.effect.params || {}}})).sort((a,b) => String(a.id).localeCompare(String(b.id))),
    rules: scene.rules || [], overrides: scene.overrides || {},
    sfp_sync: {mode:scene.sfp_sync?.mode ?? 'independent',source_port:scene.sfp_sync?.source_port ?? 1},
    sfp: Object.fromEntries(Object.entries(sfp).map(([port, light]) => [port,
      Object.fromEntries(sfpKeys.filter(key => light[key] !== undefined).map(key => [key, light[key]]))]))
  }));
}

export function matchingProfile(profiles, state) {
  const current = sceneFingerprint(state);
  return Object.keys(profiles).find(name => sceneFingerprint(profiles[name]) === current) ?? null;
}

export function profileSfpSummary(scene) {
  const ports=(Array.isArray(scene.sfp)?scene.sfp.map(light=>light.port):Object.keys(scene.sfp||{}).map(Number)).sort((a,b)=>a-b);
  const sync=sfpSync(scene),names={pulse:'Gentle pulse',color_cycle:'Color cycle',two_color:'Two-color fade',steady:'Steady color'};
  return ports.map(port=>{
    if(sync.mode==='ethernet')return {port,label:`Following port ${sync.source_port}`};
    const light=effectiveSfpLight(scene,port);
    const label=light.mode==='off'?'Off':light.mode==='unmanaged'?'Link status':names[light.effect]||'Steady color';
    return {port,label:sync.mode==='together'?`${label} · Together`:label};
  });
}

export function templateForRule(template, draft, defaultPorts) {
  return {...clone(draft), name: !draft.name?.trim() || draft.name === 'New rule' ? template.name : draft.name,
    ports: draft.ports == null ? [...defaultPorts] : [...draft.ports],
    conditions: clone(template.conditions || []), look: clone(template.look || {}), match: 'all'};
}

export function scopeSummary(ports, copperPorts, sfpPorts) {
  const chosen = ports == null ? copperPorts : ports;
  const all = [...copperPorts, ...sfpPorts];
  const same = list => list.length === chosen.length && list.every(port => chosen.includes(port));
  if (!chosen.length) return 'No ports selected';
  if (same(all)) return 'All ports';
  if (same(copperPorts)) return 'All Ethernet ports';
  if (sfpPorts.length && same(sfpPorts)) return 'All SFP ports';
  return chosen.map(port => sfpPorts.includes(port) ? `SFP ${port}` : `Port ${port}`).join(', ');
}

export function conditionAvailable(condition, specs) {
  return Boolean(condition && specs.some(spec=>spec.key===condition.field && spec.operators.includes(condition.op)));
}

export function ruleRateFromCycle(rate, originalCycle, currentCycle) {
  return Number(currentCycle) === Number(originalCycle) ? rate : 1 / Number(currentCycle);
}

export function rulesSnapshot(baseScene, rules) {
  if (!baseScene || !Array.isArray(baseScene.rules) || !Array.isArray(rules)
      || rules.some(rule=>!rule || typeof rule.id!=='string' || !rule.id.trim())
      || new Set(rules.map(rule=>rule.id)).size!==rules.length) throw new Error('Refresh the rules before applying changes.');
  return {...clone(baseScene),rules:clone(rules)};
}

export function validateRuleDraft(draft, specs) {
  if (!draft.name.trim()) return 'Enter a rule name.';
  if (!draft.ports?.length) return 'Choose at least one port.';
  if (!draft.conditions?.length) return 'Add at least one condition.';
  if (specs && draft.conditions.some(condition=>!conditionAvailable(condition,specs))) return 'Replace or remove unavailable conditions before applying.';
  if (draft.conditions.some(condition => Array.isArray(condition.value) && !condition.value.length)) return 'Choose a value for every condition.';
  return '';
}

export function createOperationGate(onBusy, onError) {
  let pending = null, active = true;
  return {
    get pending() { return pending; },
    get active() { return active; },
    run(action) {
      if (!active) return Promise.resolve();
      if (pending) return pending;
      onBusy(true);
      pending = Promise.resolve().then(action).catch(error => { if (active) onError(error); })
        .finally(() => { pending = null; if (active) onBusy(false); });
      return pending;
    },
    async settle() { await pending; return active; },
    close() { active = false; }
  };
}
