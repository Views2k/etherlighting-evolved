import { icon,logo } from './icons.mjs';
import { EFFECTS,LIBRARY,effectById,defaultConfig,effectColor,speedParam,speedSliderValue,speedText,synchronizedBreathing } from './effects.mjs';
import { hexToRgb,hexToHsv,normalizeHex,dim,hsvToHex,mix } from './color.mjs';
import { sfpControlStatus } from './state.mjs';
import { fieldByKey,OPERATORS,matchingRule,matchingSfpRule,renderLook,rulePortLabel } from './rules.mjs';
export const esc = value => String(value ?? '').replace(/[&<>"']/g,c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export const button = (text,action,kind = '',glyph = '') => `<button class="button ${kind}" data-action="${action}">${glyph ? icon(glyph) : ''}${text}</button>`;
export const toggle = (action,value,label,extra = '') => `<button class="toggle" role="switch" aria-checked="${!!value}" aria-label="${esc(label)}" data-action="${action}" ${extra}></button>`;
const brand = `${logo}<span class="brand-name product-name">Etherlighting <br>Evolved</span>`;
export const NAV = [['studio','grid','Lighting'],['effects','wind','Effects'],['rules','activity','Rules'],['scenes','layers','Scenes']];
const UTILITIES = [['activity','clock','Activity'],['settings','settings','Settings']];

export function chassis(state,decorative = false) {
  return `<div class="switch-chassis"><div class="chassis-top"><span class="chassis-brand">${logo}</span><span class="chassis-model">${state.ports.length === 8 ? 'PRO XG 8 POE' : `${state.ports.length} PORTS`}</span><span class="dot"></span></div>
    <div class="port-row" style="grid-template-columns:repeat(${Math.min(12,state.ports.length)},minmax(0,1fr))">${state.ports.map((port,i) => {
      const color = effectColor(defaultConfig('gradient'),i,state.ports.length,0),source = sourceForPort(state,port);
      const label = {override:'Color',zone:'Effect',rule:'Rule',released:'UniFi',none:'—'}[source.kind] || '—';
      return `<${decorative ? 'div' : 'button'} class="port" style="--port-color:${color}" ${decorative ? '' : `data-action="port" data-id="${port.id}" aria-label="Port ${port.id}, ${port.facts.link === null ? 'status unknown' : port.facts.link ? 'online' : 'offline'}${port.override ? ', port color' : ''}" aria-pressed="${state.selected.includes(port.id)}"`} data-port="${port.id}"><span class="port-socket"></span><span class="port-number">${String(port.id).padStart(2,'0')}</span>${decorative ? '' : `<span class="port-source" data-port-source="${port.id}" title="${esc(source.name || label)}">${label}</span>`}</${decorative ? 'div' : 'button'}>`;
    }).join('')}</div></div>`;
}
export function loginView(state) {
  return `<main class="login" id="main"><div class="login-card"><div class="brand">${brand}</div><div class="login-form-panel"><h1>Connect your switch</h1><p class="login-intro">Use your switch’s IP address and SSH login.</p><form id="login-form"><label class="field"><span>Switch IP address</span><input name="host" type="text" placeholder="Switch IP address" required autocomplete="off" spellcheck="false" maxlength="253"></label><div class="login-credentials"><label class="field"><span>SSH username</span><input name="username" type="text" placeholder="Username" autocomplete="username" required maxlength="64"></label><label class="field"><span>SSH password</span><div class="password-wrap"><input name="password" type="password" placeholder="Password" autocomplete="current-password" required><button type="button" data-action="show-password" aria-label="Show password">Show</button></div></label></div><label class="check-label save-login"><input id="save-login" name="remember" type="checkbox" ${state.savedLogin ? 'checked' : ''}><span>Save login on this computer</span></label><div id="login-error" role="alert" hidden></div><details class="connection-options"><summary>Connection options</summary><label class="field"><span>API token, if configured</span><input name="token" type="password" autocomplete="off" placeholder="Optional"></label></details><button class="button primary full" type="submit" id="connect-button">Connect ${icon('arrow')}</button></form><button class="button ghost full login-demo" data-action="demo">Explore the demo ${icon('arrow')}</button><p class="login-legal">Unofficial community project.<br>Not affiliated with Ubiquiti.</p></div></div></main>`;
}
export function shellView(state) {
  const active = state.page === 'groups' ? 'studio' : state.page;
  const current = [...NAV,...UTILITIES].find(n => n[0] === active)?.[2] || 'Lighting';
  const navigation = items => items.map(([id,glyph,name]) => `<button class="nav-item ${active === id ? 'active' : ''}" data-action="navigate" data-page="${id}" ${active === id ? 'aria-current="page"' : ''} title="${name}">${icon(glyph)}<span>${name}</span></button>`).join('');
  return `<div class="shell"><aside class="sidebar" id="sidebar"><button class="icon-button mobile-nav-close" data-action="menu" aria-label="Close navigation">${icon('close')}</button><button class="brand" data-action="about" aria-label="About Etherlighting Evolved">${brand}</button><nav class="primary-nav" aria-label="Main navigation">${navigation(NAV)}</nav><div class="sidebar-bottom"><div class="device-mini"><div class="device-line">${icon('server')}<strong>${state.ports.length === 8 ? 'Pro XG 8 PoE' : `${state.ports.length}-port switch`}</strong></div><p><span class="dot ${state.mode === 'simulator' ? 'accent' : ''}"></span>${state.mode === 'simulator' ? 'Demo workspace' : state.backendSimulated ? 'Backend simulator' : 'Switch connected'}</p></div><nav aria-label="Workspace">${navigation(UTILITIES)}</nav><div class="sidebar-foot"><button data-action="about">About ${icon('arrow')}</button></div></div></aside><button class="navigation-scrim" data-action="menu" aria-label="Close navigation" tabindex="-1"></button><div class="workspace"><header class="topbar"><button class="icon-button mobile-menu" data-action="menu" aria-label="Toggle navigation">${icon('menu')}</button><div class="breadcrumbs">${state.page === 'groups' ? '<button data-action="navigate" data-page="studio">Lighting</button><span aria-hidden="true">/</span><span>Port groups</span>' : `<span>${current}</span>`}</div><div class="top-actions"><span class="mode-badge"><span class="dot ${state.mode === 'simulator' ? 'accent' : ''}"></span>${state.mode === 'simulator' ? 'Demo mode' : state.backendSimulated ? 'Backend demo' : 'Connected'}</span><button class="icon-button" data-action="disconnect" title="Disconnect switch" aria-label="Disconnect switch">${icon('power')}</button><button class="topbar-brand" data-action="about" aria-label="About Etherlighting Evolved">${logo}</button></div></header><main id="main" tabindex="-1"><div id="connection-alert"></div><div id="page"></div></main></div></div>`;
}
export function heading(title,description,action = '') { return `<div class="page-heading"><div><h1>${title}</h1>${description ? `<p>${description}</p>` : ''}</div>${action}</div>`; }
export function effectCard(effect,state) {
  const preview = effect.supportsPreview ? `data-effect-preview="${esc(effect.id)}"` : '';
  const visual = effect.supportsPreview ? Array.from({length:8},() => '<i class="effect-led"></i>').join('') : icon('blend');
  return `<button class="effect-card ${state.config.id === effect.id ? 'selected' : ''}" data-action="effect" data-id="${esc(effect.id)}" aria-pressed="${state.config.id === effect.id}" style="--effect-a:${effect.colors[0]};--effect-b:${effect.colors[1]}"><div class="effect-visual" ${preview} aria-hidden="true">${visual}</div><div class="effect-copy"><strong>${esc(effect.name)}</strong><p>${esc(effect.description)}</p></div>${state.config.id === effect.id ? `<span class="selected-indicator">${icon('check')}</span>` : ''}</button>`;
}
export function paramControl(param,value,colorMode = null) {
  const name = esc(param.key),label = esc(param.label.replaceAll('Colour','Color').replaceAll('colour','color'));
  if (param.type === 'bool') return `<div class="param inline-row"><span class="muted">${label}</span>${toggle('param-toggle',value,label,`data-key="${name}"`)}</div>`;
  if (param.type === 'color') {
    const brightness = colorMode?.brightness,brightnessName = brightness ? esc(brightness.key) : '';
    const level = Number.isFinite(Number(brightness?.value)) ? Math.min(100,Math.max(0,Number(brightness.value))) : 100;
    const brightnessControl = brightness ? `<div class="color-brightness"><label for="param-${brightnessName}">Brightness</label><input id="param-${brightnessName}" data-param="${brightnessName}" type="range" min="0" max="100" step="1" value="${level}" aria-label="${label} brightness" aria-valuetext="${level}%" style="--fill:${level}%"><output id="out-${brightnessName}" for="param-${brightnessName}">${level}%</output></div>` : '';
    return `<div class="param"><div class="control-label"><label for="param-${name}">${label}</label>${colorMode?.key ? `<div class="color-mode"><span>Randomize</span>${toggle('param-toggle',colorMode.random,`Randomize ${param.label.toLowerCase()}`,`data-key="${esc(colorMode.key)}"`)}</div>` : ''}</div><div class="param-color"><input id="param-${name}" type="color" value="${normalizeHex(value) || '#FFFFFF'}" data-param="${name}" aria-label="${label}" ${colorMode?.active ? 'disabled' : ''}><input type="text" value="${esc(value)}" data-param-hex="${name}" aria-label="${label} hex${colorMode?.active ? ', saved fixed color' : ''}" maxlength="7" spellcheck="false" ${colorMode?.active ? 'readonly' : ''}></div>${brightnessControl}</div>`;
  }
  if (param.type === 'choice') return `<div class="param"><label class="control-label" for="param-${name}">${label}</label><select id="param-${name}" data-param="${name}">${param.choices.map(choice => `<option value="${esc(choice)}" ${choice === value ? 'selected' : ''}${param.disabledChoices?.includes(choice) ? ' disabled' : ''}>${esc(param.key === 'playback' ? choice[0].toUpperCase() + choice.slice(1) : choice.replaceAll('_',' '))}</option>`).join('')}</select></div>`;
  const fill = 100 * (value - param.min) / (param.max - param.min);
  return `<div class="param"><label class="control-label" for="param-${name}"><span>${label}</span><output id="out-${name}">${esc(paramText(param,value))}</output></label><input id="param-${name}" data-param="${name}" type="range" min="${param.min}" max="${param.max}" step="${param.step || .1}" value="${value}" aria-valuetext="${esc(paramText(param,value))}" style="--fill:${fill}%"></div>`;
}
export function paramText(param,value) {
  if (param.dims) return `${Number(Number(value).toFixed(2))}%`;
  if (['floor','saturation','intensity','duty','night_brightness','smoothing'].includes(param.key)) return `${Math.round(value * 100)}%`;
  if (param.key === 'center') return value === 0 ? 'Middle' : `Port ${param.portIds?.[value - 1] ?? value}`;
  return `${Number(Number(value).toFixed(2))}${param.unit ? ` ${param.unit}` : ''}`;
}
export function speedControl(config) {
  const param = speedParam(effectById(config.id),config.params);
  if (!param) return '';
  const value = config.params[param.key] ?? param.default;
  const slider = speedSliderValue(param,value),text = speedText(param,value);
  return `<div class="param effect-speed"><label class="control-label" for="effect-speed"><span>${param.key === 'pace' ? 'Shared speed' : 'Speed'}</span><output id="effect-speed-value">${esc(text)}</output></label><input id="effect-speed" data-speed="${esc(param.key)}" type="range" min="${param.min}" max="${param.max}" step="${param.step || .1}" value="${slider}" aria-valuetext="${esc(text)}" style="--fill:${100 * (slider - param.min) / (param.max - param.min)}%"><div class="slider-labels"><span>Slow</span><span>Fast</span></div>${param.key === 'pace' ? '<p class="inspector-hint">One speed for all synchronized groups.</p>' : ''}</div>`;
}
export function presentParam(param,state) {
  if (param.key !== 'center') return param;
  const ports = (state.zones.find(z => z.id === state.activeZone) || state.zones[0])?.ports || [];
  return {...param,max:ports.length || param.max,portIds:ports};
}
export function effectControls(state) {
  const effect = effectById(state.config.id),speed = speedParam(effect,state.config.params),synchronized = synchronizedBreathing(state.config);
  const synchronizedAvailable = state.mode === 'simulator' || state.switch?.native_breathing === true;
  const playbackParam = effect.id === 'breathing' ? effect.params.find(param => param.key === 'playback') : null;
  const playback = playbackParam && (synchronizedAvailable || synchronized)
    ? {...playbackParam,disabledChoices:synchronizedAvailable ? [] : ['synchronized']} : null;
  const colorModes = new Map();
  for (const param of effect.params.filter(p => p.type === 'color')) {
    const companion = effect.params.find(p => p.key === param.random_param && p.type === 'bool' && p.randomizes === param.key);
    const brightness = effect.params.find(p => p.key === param.brightness_param && p.type === 'float' && p.dims === param.key);
    const random = companion && !synchronized ? state.config.params[companion.key] === true : false;
    const legacy = companion && (effect.exclusive_params || []).some(pair => pair.includes(companion.key) && pair.some(key => key !== companion.key && state.config.params[key] === true));
    colorModes.set(param.key,{key:synchronized ? undefined : companion?.key,random,active:!synchronized && (random || legacy),
      brightness:brightness ? {key:brightness.key,value:state.config.params[brightness.key] ?? brightness.default} : null});
  }
  const randomEnabled = [...colorModes.values()].some(mode => mode.random);
  const params = effect.params.filter(p => p.key !== speed?.key && !p.randomizes && !p.dims && (effect.id !== 'breathing' || (!['playback','pace'].includes(p.key) && (!synchronized || p.type === 'color'))) && (p.key !== 'color_transition' || randomEnabled)).map(p => presentParam(p,state));
  const uplink = effect.id === 'traffic' ? params.filter(p => p.key === 'custom_uplink' || p.key === 'uplink_color' && state.config.params.custom_uplink === true) : [];
  const colors = params.filter(p => p.type === 'color' && (effect.id !== 'traffic' || p.key !== 'uplink_color')), adjustments = params.filter(p => p.type !== 'color' && !uplink.includes(p));
  const control = p => paramControl(p,state.config.params[p.key] ?? p.default,colorModes.get(p.key));
  return `<section class="effect-controls"><div class="control-heading"><span class="control-kicker">Group effect</span><h2>${esc(effect.name)}</h2><p>${esc(effect.description)}</p></div>${playback ? paramControl(playback,state.config.params.playback ?? playback.default) : ''}${speedControl(state.config)}<div class="effect-parameters">${colors.map(control).join('')}${uplink.map(control).join('')}</div>${adjustments.length ? `<details class="effect-details" data-disclosure="effect-${esc(effect.id)}"><summary>Fine-tune effect ${icon('down')}</summary><div class="effect-parameters">${adjustments.map(control).join('')}</div></details>` : ''}</section>`;
}
export function sourceForPort(state,port,date = new Date()) {
  if (state.released) return {kind:'released',name:'UniFi'};
  if (state.mode === 'live') return port.source || (port.override ? {kind:'override',name:'Port color'} : {kind:'none',name:'Waiting'});
  if (port.override) return {kind:'override',name:'Port color'};
  const rule = matchingRule(state.rules,port,date);
  if (rule) return {kind:'rule',name:rule.name};
  const zone = state.zones.find(z => z.ports.includes(port.id));
  return {kind:'zone',name:effectById(zone?.effect.id).name};
}
export function outputSummary(state,date = new Date()) {
  const group = state.zones.find(z => z.id === state.activeZone) || state.zones[0];
  const ports = state.ports.filter(p => group?.ports.includes(p.id));
  const sources = ports.map(p => sourceForPort(state,p,date));
  return {manual:sources.filter(s => s.kind === 'override').length,rules:sources.filter(s => s.kind === 'rule').length,total:ports.length};
}
function groupToolbar(state) {
  return `<div class="effect-target"><label for="effect-group">Group</label><select id="effect-group">${state.zones.map(z => `<option value="${esc(z.id)}" ${z.id === state.activeZone ? 'selected' : ''}>${esc(z.name)} · ${z.ports.length} ports</option>`).join('')}</select><button class="text-button" data-action="navigate" data-page="groups">Manage groups ${icon('arrow')}</button></div>`;
}
function masterBrightness(state) {
  return `<div class="master-brightness"><label for="global-brightness">${icon('sun')}Brightness</label><input id="global-brightness" type="range" aria-label="Master brightness" min="0" max="100" value="${state.brightness}" style="--fill:${state.brightness}%"><output id="global-brightness-value">${state.brightness}%</output></div>`;
}
export const SFP_SPEED = {key:'period',min:2,max:30,step:.5};
export function sfpIndicatorColor(item,time = 0,master = 100,rule = null) {
  if (item.mode === 'off') return '#000000';
  if (item.mode !== 'color' && !rule) return '#51555C';
  const chosen = normalizeHex(item.color) || '#FFFFFF',primary = dim(chosen,(item.color_brightness ?? 100) / 100);
  const phase = ((time / (item.period || 6)) % 1 + 1) % 1,blend = (1 - Math.cos(2 * Math.PI * phase)) / 2;
  let color = primary;
  if (item.effect === 'pulse') color = dim(primary,1 - blend);
  if (item.effect === 'two_color') color = mix(primary,dim(normalizeHex(item.secondary_color) || '#006FFF',(item.secondary_brightness ?? 100) / 100),blend);
  if (item.effect === 'color_cycle') {
    const [h,s,v] = hexToHsv(chosen),sector = ((h / 360 + phase) % 1) * 6,fraction = sector % 1,eased = fraction * fraction * (3 - 2 * fraction);
    color = dim(hsvToHex((Math.floor(sector) + eased) * 60,Math.max(.75,s),v),(item.color_brightness ?? 100) / 100);
  }
  if (rule) color = renderLook(rule.look,color,time);
  return dim(color,(item.brightness ?? 100) * (rule?.look.brightness ?? master) / 10000);
}
export function sfpActiveRule(state,item,date = new Date()) {
  if (state.released || item.mode === 'off') return null;
  return state.mode === 'live' ? item.rule || null : matchingSfpRule(state.rules || [],item,date);
}
export function sfpPreview(state,item,time = 0,date = new Date()) {
  if (!state.released && state.mode === 'live' && item.mode !== 'off' && item.rule && normalizeHex(item.preview)) return item.preview;
  return sfpIndicatorColor(item,time,state.brightness,state.mode === 'live' ? null : sfpActiveRule(state,item,date));
}
function sfpBrightness(item,key,label,id) {
  const port = item.port,level = item[key];
  return `<div class="color-brightness"><label for="sfp-${id}-${port}">${key === 'brightness' ? label : 'Brightness'}</label><input id="sfp-${id}-${port}" data-sfp-brightness="${port}" data-sfp-level-key="${key}" type="range" min="0" max="100" step="1" value="${level}" aria-label="SFP ${port} ${label.toLowerCase()}" aria-valuetext="${level}%" style="--fill:${level}%"><output for="sfp-${id}-${port}" data-sfp-level="${key}">${level}%</output></div>`;
}
function sfpColor(item,key,label) {
  const port = item.port,secondary = key === 'secondary_color',id = secondary ? 'secondary-' : '';
  return `<div class="sfp-color-group" ${secondary ? 'data-sfp-secondary' : ''} ${secondary && item.effect !== 'two_color' ? 'hidden' : ''}><label class="control-label" for="sfp-${id}color-${port}">${label}</label><div class="param-color sfp-color"><input id="sfp-${id}color-${port}" type="color" value="${normalizeHex(item[key]) || '#FFFFFF'}" data-sfp-color="${port}" data-sfp-color-key="${key}" aria-label="SFP ${port} ${label.toLowerCase()}"><input id="sfp-${id}hex-${port}" type="text" value="${normalizeHex(item[key]) || '#FFFFFF'}" data-sfp-hex="${port}" data-sfp-color-key="${key}" aria-label="SFP ${port} ${label.toLowerCase()} hex" maxlength="7" spellcheck="false"></div>${sfpBrightness(item,secondary ? 'secondary_brightness' : 'color_brightness',`${secondary ? 'Secondary' : 'Primary'} brightness`,secondary ? 'secondary-level' : 'primary-level')}</div>`;
}
export function sfpLightingView(state) {
  if (!state.sfp?.length) return '';
  return `<section class="sfp-lighting" aria-labelledby="sfp-heading"><h2 id="sfp-heading" class="sr-only">SFP lighting</h2><div class="sfp-grid">${state.sfp.map(item => {
    const port = item.port,status = sfpControlStatus(item),slider = speedSliderValue(SFP_SPEED,item.period),rule = sfpActiveRule(state,item);
    return `<div class="sfp-control" data-sfp-port="${port}"><div class="sfp-title"><h3><span class="sfp-indicator" data-sfp-indicator aria-hidden="true" style="background-color:${sfpPreview(state,item)}"></span>SFP ${port}</h3><span class="sfp-link" data-sfp-link>${item.link === null ? 'Link unknown' : item.link ? 'Link online' : 'Link offline'}</span><span class="sfp-anomaly" data-sfp-anomaly ${item.anomaly === true ? '' : 'hidden'}>Anomaly detected</span></div><div class="sfp-mode"><label for="sfp-mode-${port}">Lighting</label><select id="sfp-mode-${port}" data-sfp-mode="${port}" aria-label="SFP ${port} lighting">${[['unmanaged','Link status'],['color','Color'],['off','Off']].map(([value,label]) => `<option value="${value}" ${item.mode === value ? 'selected' : ''}>${label}</option>`).join('')}</select></div><div data-sfp-color-controls ${item.mode === 'color' ? '' : 'hidden'}><div class="sfp-mode"><label for="sfp-effect-${port}">Effect</label><select id="sfp-effect-${port}" data-sfp-effect="${port}" aria-label="SFP ${port} effect">${[['steady','Steady'],['pulse','Gentle pulse'],['color_cycle','Color cycle'],['two_color','Two-color fade']].map(([value,label]) => `<option value="${value}" ${item.effect === value ? 'selected' : ''}>${label}</option>`).join('')}</select></div><div class="sfp-speed" data-sfp-speed-control ${item.effect === 'steady' ? 'hidden' : ''}><label class="control-label" for="sfp-speed-${port}"><span>Speed</span><output data-sfp-speed-value>${speedText(SFP_SPEED,item.period)}</output></label><input id="sfp-speed-${port}" data-sfp-speed="${port}" type="range" min="2" max="30" step=".5" value="${slider}" aria-valuetext="${speedText(SFP_SPEED,item.period)}" style="--fill:${100 * (slider - 2) / 28}%"><div class="slider-labels"><span>Slow</span><span>Fast</span></div></div>${sfpColor(item,'color','Primary color')}${sfpColor(item,'secondary_color','Secondary color')}${sfpBrightness(item,'brightness','Port brightness','brightness')}</div><p class="inspector-hint" data-sfp-rule ${rule ? '' : 'hidden'}>${rule ? `Rule: ${esc(rule.name)}` : ''}</p><div class="sfp-status"><span class="sfp-pending" data-sfp-pending role="status">${status.text}</span><button class="text-button" data-action="sfp-retry" data-id="${port}" ${status.retry ? '' : 'hidden'}>Retry</button></div></div>`;
  }).join('')}</div></section>`;
}
function inspectorTabs(state) {
  const mode = state.inspector || 'effect';
  return `<div class="inspector-tabs" role="tablist" aria-label="Lighting controls">${[['effect','Effect'],['color','Port color']].map(([id,label]) => `<button id="tab-${id}" role="tab" aria-selected="${mode === id}" aria-controls="lighting-inspector" tabindex="${mode === id ? 0 : -1}" data-action="inspector-tab" data-value="${id}">${label}</button>`).join('')}</div>`;
}
export function inspectorView(state,color) {
  if (!state.selected.length) return `<section class="inspector empty-inspector"><h2>Select ports</h2><p>Click a port on the switch, or select all ports to set one color.</p>${button('Select all ports','select-all','ghost full')}</section>`;
  const [h,s,v] = hexToHsv(color),[r,g,b] = hexToRgb(color);
  const pinned = state.ports.filter(p => state.selected.includes(p.id) && p.override);
  const levels = [...new Set(pinned.map(p => p.override.brightness ?? state.brightness))];
  const selectedLevel = levels.length === 1 ? levels[0] : state.brightness;
  return `<section class="inspector"><div class="inspector-title"><h2>Port color</h2><span class="small-chip accent" id="selection-count">${state.selected.length === 1 ? `Port ${state.selected[0]}` : `${state.selected.length} ports`}</span></div><div class="control-label"><span>Color for selected ports</span></div><div class="color-plane" id="color-plane" role="slider" tabindex="0" aria-label="Color saturation and value. Use arrow keys to adjust." aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(s * 100)}" aria-valuetext="Saturation ${Math.round(s * 100)}%, value ${Math.round(v * 100)}%" style="--hue:${h}"><span class="color-cursor" style="left:${s * 100}%;top:${(1 - v) * 100}%"></span></div><div class="hue-row"><input id="hue" aria-label="Hue" type="range" min="0" max="360" step="1" value="${h}"></div><div class="color-values"><label>HEX<input id="hex" type="text" aria-label="Hex color" value="${color}" maxlength="7" spellcheck="false"></label>${[['R',r],['G',g],['B',b]].map(([key,value]) => `<label>${key}<input id="rgb-${key}" type="number" min="0" max="255" value="${value}" aria-label="${key === 'R' ? 'Red' : key === 'G' ? 'Green' : 'Blue'} channel"></label>`).join('')}</div><div class="swatches">${['#FFFFFF','#E4BD82','#F18C93','#C29CE6','#87B9ED','#79DCC0','#000000'].map(c => `<button class="swatch ${color === c ? 'selected' : ''}" style="--swatch:${c}" data-action="swatch" data-color="${c}" aria-label="Set ${c}" title="${c}"></button>`).join('')}</div><div class="control-section"><label class="control-label" for="port-brightness"><span>Port brightness</span><output id="port-brightness-value">${levels.length > 1 ? 'Mixed' : selectedLevel + '%'}</output></label><input id="port-brightness" type="range" min="0" max="100" value="${selectedLevel}" style="--fill:${selectedLevel}%" ${pinned.length ? '' : 'disabled'}><div class="slider-labels"><span>0%</span><span>100%</span></div></div><p class="inspector-hint">Changing this color takes over the selected ports.</p>${button('Use group lighting','clear-pins','ghost full','refresh')}${state.selected.length === 1 ? button('Port details','port-details','ghost full','server') : ''}</section>`;
}
export function studioView(state,color) {
  const online = state.ports.filter(p => p.facts.link).length,summary = outputSummary(state);
  const effect = effectById(state.config.id),inspector = state.inspector || 'effect';
  const sfp = state.deviceTab === 'sfp' && state.sfp?.length;
  const quickIds = ['gradient','breathing','marquee','aurora','lava_lamp','plasma'];
  const quickEffects = [...new Set([...quickIds,...LIBRARY.map(item => item.id)])]
    .map(id => LIBRARY.find(item => item.id === id)).filter(Boolean).slice(0,6);
  const deviceTabs = `<div class="device-tabs" role="tablist" aria-label="Port type">${[['ethernet','Ethernet',state.ports.length],...(state.sfp?.length ? [['sfp','SFP',state.sfp.length]] : [])].map(([id,label,count]) => `<button id="device-tab-${id}" role="tab" aria-selected="${id === (sfp ? 'sfp' : 'ethernet')}" aria-controls="${id}-workspace" tabindex="${id === (sfp ? 'sfp' : 'ethernet') ? 0 : -1}" data-action="device-tab" data-value="${id}">${label}<span>${count}</span></button>`).join('')}</div>`;
  return `${heading('Lighting','',button('Save scene','save-scene','','save'))}<div class="lighting-toolbar">${deviceTabs}${masterBrightness(state)}</div>
  <div id="ethernet-workspace" role="tabpanel" aria-labelledby="device-tab-ethernet" ${sfp ? 'hidden' : ''}>${groupToolbar(state)}<div class="studio-grid"><div class="studio-main"><section class="panel device-panel"><div class="panel-header"><div class="device-name"><div><h2>${state.ports.length === 8 ? 'Pro XG 8 PoE' : `${state.ports.length}-port switch`}</h2><p>Choose ports to set a color</p></div></div><span class="output-state"><span class="dot ${state.mode === 'simulator' ? 'accent' : ''}"></span>${state.mode === 'simulator' ? 'Demo preview' : state.released ? 'Lighting released' : state.backendSimulated ? 'Simulator output' : 'Live output'}</span></div><div class="switch-stage">${chassis(state)}</div><div class="canvas-meta"><div class="canvas-legend"><span><i class="dot"></i><b id="online-count">${online}</b> online</span><span><i class="dot neutral"></i><b id="offline-count">${state.ports.filter(p => p.facts.link === false).length}</b> offline</span></div><div class="select-toolbar"><span>Select</span>${['All','Online','Offline','None'].map(label => `<button class="tiny-button ${label === 'All' && state.selected.length === state.ports.length ? 'active' : ''}" data-action="select" data-value="${label.toLowerCase()}">${label}</button>`).join('')}</div></div></section><div class="output-summary" id="output-summary"><span>${state.released ? 'Lighting released' : `${esc(effect.name)} · ${summary.total} ports`}</span><span id="output-sources">${summary.manual ? `${summary.manual} using port colors` : ''}${summary.manual && summary.rules ? ' · ' : ''}${summary.rules ? `${summary.rules} following rules` : ''}</span>${state.released || !summary.rules ? '' : '<button class="text-button" data-action="navigate" data-page="rules">View rules</button>'}</div><div class="section-heading"><h2>Quick effects</h2><button class="text-button" data-action="navigate" data-page="effects">All ${LIBRARY.length} effects ${icon('arrow')}</button></div><div class="effect-grid compact-effects">${quickEffects.map(e => effectCard(e,state)).join('')}</div><p class="selection-hint">Shift-click to select multiple ports. Double-click for port details.</p></div><aside class="studio-aside panel">${inspectorTabs(state)}<div id="lighting-inspector" role="tabpanel" aria-labelledby="tab-${inspector}">${inspector === 'color' ? inspectorView(state,color) : effectControls(state)}</div></aside></div></div>
  <div id="sfp-workspace" role="tabpanel" aria-labelledby="device-tab-sfp" ${sfp ? '' : 'hidden'}><div class="sfp-workspace-heading"><p>Control each indicator’s color and movement.</p><button class="text-button" data-action="navigate" data-page="rules">Manage rules ${icon('arrow')}</button></div><div id="sfp-controls">${sfpLightingView(state)}</div></div>
  <div class="lighting-footer"><span>${state.mode === 'simulator' ? 'Demo workspace' : state.released ? 'UniFi controls your lights' : 'Changes apply as you adjust them'}</span><button class="text-button" data-action="power">${state.mode === 'live' ? state.released ? 'Resume lighting' : 'Release to UniFi' : state.lightsOn ? 'Turn lights off' : 'Turn lights on'}</button></div>`;
}
export function effectsView(state,filter = 'All',search = '') {
  const items = LIBRARY.filter(e => (filter === 'All' || e.category === filter) && `${e.name} ${e.description}`.toLowerCase().includes(search.toLowerCase()));
  return `${heading('Effects','Choose an effect, then adjust its color and movement.')}${groupToolbar(state)}<div class="effects-workspace"><div><div class="filterbar"><div class="tabs" aria-label="Effect categories">${['All','Ambient','Dynamic','Data'].filter(category => category === 'All' || LIBRARY.some(effect => effect.category === category)).map(f => `<button data-action="filter" data-value="${f}" class="${f === filter ? 'active' : ''}" aria-pressed="${f === filter}">${f}</button>`).join('')}</div><div class="search-box">${icon('search')}<input id="effect-search" type="text" placeholder="Search effects" aria-label="Search effects" value="${esc(search)}"></div></div><div class="effect-grid library-grid" id="effects-results">${items.length ? items.map(e => effectCard(e,state)).join('') : '<div class="empty-state"><h2>No effects found</h2><p>Try another name or category.</p></div>'}</div></div><aside class="panel library-controls">${effectControls(state)}${masterBrightness(state)}</aside></div>`;
}
export function conditionText(c) {
  const field = fieldByKey(c.field); let value = c.value;
  if (typeof value === 'boolean') value = value ? field.trueLabel || 'Yes' : field.falseLabel || 'No';
  if (c.field === 'weekday' && Array.isArray(value)) {
    const selected = new Set(value),days = ['mon','tue','wed','thu','fri','sat','sun'];
    const isExactly = expected => selected.size === expected.length && expected.every(day => selected.has(day));
    value = isExactly(days) ? 'Every day' : isExactly(days.slice(0,5)) ? 'Weekdays' : isExactly(days.slice(5)) ? 'Weekends' : days.filter(day => selected.has(day)).map(day => day[0].toUpperCase() + day.slice(1)).join(', ');
    return `${field.label} ${c.op === 'not_in' ? 'excludes' : 'includes'} ${value}`;
  }
  if (Array.isArray(value)) value = value.join(c.field === 'time' ? ' – ' : ', ');
  if (c.field === 'poe') value = String(value).replaceAll('poe','PoE').replaceAll('off','No PoE');
  return `${field.label} ${OPERATORS[c.op] || c.op} ${value}${field.unit ? ` ${field.unit}` : ''}`;
}
export function rulesView(state) {
  return `${heading('Rules','Set port colors using link status, PoE, anomalies, or a schedule.',`<div class="heading-actions">${button('Add link colors','link-preset','ghost','link')}${button('New rule','new-rule','primary','plus')}</div>`)}<div class="rule-templates">${[
    ['link','link','Link & status','Use link status and negotiated speed.'],['poe','zap','Power & PoE','Match PoE classes and power usage.'],['time','clock','Time & schedule','Choose times and days for a color.'],['anomaly','activity','Health & alerts','Respond to reported port anomalies.']
  ].map(([type,glyph,title,desc]) => `<button class="rule-template" data-action="rule-template" data-type="${type}">${icon(glyph)}<h3>${title}</h3><p>${desc}</p></button>`).join('')}</div><div class="section-heading"><div><h2>Your rules <span class="muted">${String(state.rules.length).padStart(2,'0')}</span></h2><p>First match wins. Move important rules to the top.</p></div></div><div class="rule-list">${state.rules.length ? state.rules.map((r,i) => `<div class="rule-row ${r.enabled ? '' : 'disabled'}"><span class="rule-priority">${String(i + 1).padStart(2,'0')}</span><span class="rule-color" style="--rule-color:${normalizeHex(r.look.color) || '#FFFFFF'}"></span><div class="rule-copy"><h3>${esc(r.name)}</h3><p>${r.conditions.map(c => esc(conditionText(c))).join(r.match === 'any' ? ' · or ' : ' · and ')} · ${esc(rulePortLabel(r.ports,(state.sfp || []).map(item => item.port)))}</p></div>${toggle('rule-toggle',r.enabled,`Enable ${r.name}`,`data-id="${esc(r.id)}"`)}<button class="icon-button" data-action="rule-up" data-id="${esc(r.id)}" aria-label="Move ${esc(r.name)} up" ${i === 0 ? 'disabled' : ''}>${icon('arrow','rotate-up')}</button><button class="icon-button" data-action="edit-rule" data-id="${esc(r.id)}" aria-label="Edit ${esc(r.name)}">${icon('edit')}</button><button class="icon-button" data-action="delete-rule" data-id="${esc(r.id)}" aria-label="Delete ${esc(r.name)}">${icon('trash')}</button></div>`).join('') : `<div class="empty-state">${icon('activity')}<h2>No rules configured</h2><p>Choose a template or create a custom rule.</p>${button('White online, red offline','link-preset','ghost','plus')}</div>`}</div><div class="priority-note">${icon('info')}Port colors take priority over rules. The first matching rule takes priority over the group effect.</div>`;
}
export function scenesView(state) {
  return `${heading('Scenes','Save colors, effects, groups, and rules together.',button('Save current look','save-scene','primary','plus'))}<div class="scene-grid">${state.scenes.length ? state.scenes.map(scene => `<article class="panel"><div class="scene-art" aria-hidden="true">${Array.from({length:8},(_,i) => `<i style="--scene-color:${normalizeHex(scene.snapshot?.overrides?.[i + 1]?.color) || normalizeHex(effectColor({id:scene.snapshot?.effect || 'gradient',params:scene.snapshot?.params || {}},i,8,1)) || '#FFFFFF'}"></i>`).join('')}</div><div class="scene-body"><h3>${esc(scene.name)}</h3><p>${esc(effectById(scene.snapshot?.effect).name)} · ${scene.snapshot?.rules?.length || 0} ${scene.snapshot?.rules?.length === 1 ? 'rule' : 'rules'}</p><div class="scene-actions"><button class="button ghost" data-action="apply-scene" data-id="${esc(scene.id)}">${icon('play')}Apply scene</button><button class="icon-button" ${state.mode === 'live' ? 'hidden' : ''} data-action="rename-scene" data-id="${esc(scene.id)}" aria-label="Rename ${esc(scene.name)}">${icon('edit')}</button><button class="icon-button" data-action="delete-scene" data-id="${esc(scene.id)}" aria-label="Delete ${esc(scene.name)}">${icon('trash')}</button></div></div></article>`).join('') : `<div class="empty-state">${icon('layers')}<h2>No scenes saved</h2><p>Save your current lighting settings to reuse them later.</p>${button('Save your first scene','save-scene','primary','plus')}</div>`}</div>`;
}
export function activityView(state) {
  return `${heading('Activity','Review lighting changes made during this session.',button('Clear activity','clear-activity','ghost'))}<section class="panel">${state.activity.length ? state.activity.map(a => `<div class="log-row">${icon(a.icon || 'activity')}<div><strong>${esc(a.title)}</strong><p>${esc(a.detail)}</p></div><time>${new Date(a.at).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</time></div>`).join('') : `<div class="empty-state">${icon('clock')}<h2>No activity recorded</h2><p>Your lighting changes will appear here.</p></div>`}</section>`;
}
export function aboutContent() {return `<div class="about-mark">${logo}</div><h2 class="product-name">Etherlighting Evolved</h2><p>Open-source lighting control for UniFi switches.</p><p class="legal">Unofficial community project. Not affiliated with, endorsed by, or sponsored by Ubiquiti. UniFi and Ubiquiti are trademarks of their respective owners.</p>`;}
export function settingsView(state) {
  return `${heading('Settings','Configure the workspace and connection.')}<div class="settings-grid"><div><section class="panel settings-panel"><h2>Workspace</h2><p>Choose how lighting appears in this window.</p><div class="setting-row"><div><strong>Animate effect previews</strong><p>Show movement in effect thumbnails.</p></div>${toggle('motion',state.preferences.motion,'Animated previews')}</div><div class="setting-row"><div><strong>Show port numbers</strong><p>Display a number below each port.</p></div>${toggle('labels',state.preferences.labels,'Port numbers')}</div></section><section class="panel settings-panel"><h2>Connection</h2><p>${state.mode === 'simulator' ? 'This workspace uses simulated port data.' : 'Your switch is connected to the local controller.'}</p><div class="connection-actions">${button(state.mode === 'simulator' ? 'Connect a switch' : 'Disconnect switch','disconnect','ghost','link')}${state.mode === 'live' && state.savedLogin ? button('Forget saved login…','forget-login','ghost') : ''}</div></section>${state.mode === 'simulator' ? `<section class="panel settings-panel"><h2>Schedule preview</h2><p>Preview your schedule rules at a specific time.</p><label class="field"><span>Demo clock</span><input type="time" id="simulation-time" value="${state.simulationTime || ''}"></label>${button('Use current time','reset-clock','ghost','clock')}<p class="inspector-hint">Demo schedules use ${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)}. Open a port’s details to try link, PoE, and anomaly changes.</p></section>` : ''}</div><section class="panel settings-panel">${aboutContent()}</section></div>`;
}
