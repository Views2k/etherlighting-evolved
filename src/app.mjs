import { icon } from './icons.mjs';
import { effectById,defaultConfig,effectColor,setEffectCatalog,previewPhase,previewColorPhase,paramsAfterChange,speedParam,speedParamValue,speedSliderValue,speedText,activityPeak,dataPreviewFacts,effectConfigForSelection,synchronizedBreathing,shareBreathingPace } from './effects.mjs';
import { CATALOG } from './catalog.mjs';
import { clamp,normalizeHex,hexToRgb,rgbToHex,hexToHsv,hsvToHex,dim } from './color.mjs';
import { createState,loadState,persistState,snapshot,applySnapshot,restoreView,persistView,PAGES,SfpEdits,MutationRecovery,sfpSettings,sfpControlStatus,demoSfp } from './state.mjs';
import { makeRule,matchingRule,renderLook,fieldByKey,ruleRate } from './rules.mjs';
import { Backend,LatestQueue,EffectTransaction,SessionRestore,friendlyError,applyZoneEffect } from './api.mjs';
import { esc,button,loginView,shellView,studioView,effectsView,rulesView,scenesView,activityView,settingsView,aboutContent,sourceForPort,outputSummary,paramText,presentParam,sfpLightingView,sfpPreview,sfpActiveRule,SFP_SPEED } from './views.mjs';
import { ruleEditor,readRuleForm,initialCondition } from './rule-editor.mjs';
import { assignGroup,removeGroup,replaceGroups } from './groups.mjs';
import { groupsView,groupEditor } from './group-views.mjs';
import { initWindowChrome } from './window-chrome.mjs';

const app = document.querySelector('#app'),dialog = document.querySelector('#dialog');
const backend = new Backend();
let state = restoreView(loadState(localStorage),localStorage,location.hash),entered = false,color = '#8F9DFF',hsv = hexToHsv(color);
let filter = 'All',search = '',ruleDraft = null,toastTimer,persistTimer,animationTime = 0,lastFrame = performance.now();
let connectionLost = false,backendSimulated = false,connecting = false,savedLogin = false;
const sfpEdits = new SfpEdits(state.sfp);
const previewClocks = new Map();
const sharedBreathingClock = {};
const cardPreviewStates = new WeakMap();
const pageScroll = new Map();
const disclosures = new Map();
const effectTransaction = new EffectTransaction(setEffectPending);
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const recovery = new MutationRecovery({
  generation:() => `${backend.generation}:${sfpEdits.generation}`,
  read:() => backend.read(),
  apply:data => {applyRemote(data.state,data.ports);renderPage();},
  onFailure:() => setConnectionLost(true),
  onPending:pending => {
    if (pending) state.sfp = sfpEdits.rollback();
    setEffectPending(effectTransaction.pending);updateSfpControls({force:true});
  }
});
const queue = new LatestQueue(async (path,request) => {
  const edit = request.sfpEdit;
  const current = () => request.generation === recovery.generation();
  if (!current()) return;
  if (edit && edit.generation !== sfpEdits.generation) return;
  let remote;
  try {remote = await backend.request(path,request.method,request.body);}
  catch (error) {if (!current()) return;throw error;}
  if (!current()) return;
  if (edit && sfpEdits.acknowledge(edit,remote.sfp)) {state.sfp = sfpEdits.values;updateSfpControls();}
},async error => {
  toast(friendlyError(error));
  await recovery.run();
});

function toast(message) {const el = document.querySelector('#toast');el.textContent = message;el.classList.add('visible');clearTimeout(toastTimer);toastTimer = setTimeout(() => el.classList.remove('visible'),4000);}
function persist() {clearTimeout(persistTimer);persistTimer = setTimeout(() => {if (state.mode === 'simulator' && !persistState(state,localStorage)) toast('Browser storage is full. Your current session is still active.');},200);}
function log(title,detail = '',glyph = 'activity') {state.activity.unshift({title,detail,icon:glyph,at:Date.now()});state.activity = state.activity.slice(0,100);}
function send(path,method,body,extra = {}) {
  if (effectTransaction.pending || recovery.pending) return false;
  if (state.mode !== 'live') {persist();return true;}
  if (connectionLost) {toast('Reconnect to the controller before changing the switch.');return false;}
  const generation = recovery.generation();
  if (state.released) {queue.push('/resume',{method:'POST',generation});state.released = false;}
  queue.push(path,{method,body,...extra,generation});return true;
}
function setConnectionLost(value) {
  connectionLost = value;
  const el = document.querySelector('#connection-alert');
  if (!el || el.dataset.state === String(value)) return;
  el.dataset.state = String(value);el.setAttribute('role','status');
  el.innerHTML = value ? `<div class="connection-alert"><span>Connection interrupted. The preview shows the last received state.</span><button class="text-button" data-action="reconnect">Reconnect ${icon('refresh')}</button></div>` : '';
  const badge = document.querySelector('.mode-badge');
  if (badge) badge.innerHTML = `<span class="dot ${value ? 'red' : state.mode === 'simulator' ? 'accent' : ''}"></span>${value ? 'Disconnected' : state.mode === 'simulator' ? 'Demo mode' : state.backendSimulated ? 'Backend demo' : 'Connected'}`;
  const deviceStatus = document.querySelector('.device-mini p');
  if (deviceStatus) deviceStatus.innerHTML = `<span class="dot ${value ? 'red' : state.mode === 'simulator' ? 'accent' : ''}"></span>${value ? 'Connection lost' : state.mode === 'simulator' ? 'Demo workspace' : state.backendSimulated ? 'Backend simulator' : 'Switch connected'}`;
}
function applyRemote(remote,ports) {
  if (backend.effects) setEffectCatalog(backend.effects);
  if (Array.isArray(remote.zones)) replaceGroups(state,remote.zones);
  else if (remote.effect) state.config = {id:remote.effect.id,params:remote.effect.params};
  state.brightness = remote.brightness;state.rules = remote.rules || [];
  state.switch = {native_breathing:remote.switch?.native_breathing === true};
  state.sfp = sfpEdits.receive(remote.sfp);
  if (ports) state.ports = ports.map(p => ({id:p.port,name:`Port ${p.port}`,preview:normalizeHex(p.preview) || '#000000',source:p.source,override:p.override,facts:p.facts || {link:p.link?.link_up ?? null,speed:p.link?.speed_mbps ?? null}}));
  state.ports.forEach(p => {p.override = remote.overrides?.[p.id] || null;});
  state.selected = state.selected.filter(id => state.ports.some(p => p.id === id));
  state.lightsOn = remote.brightness > 0;
  state.released = remote.mode === 'released';
  setConnectionLost(!remote.connected);
}
backend.onMessage = message => {
  if (message.type === 'session' && !entered) {sessionRestore.check(message.session);return;}
  if (!entered || state.mode !== 'live') return;
  if (message.type === 'session') updateSavedLogin(message.session);
  if (message.type === 'session' && message.session?.logged_in === false) {toast('The switch session ended.');leaveWorkspace(false);return;}
  if (message.type === 'state') {
    if (!queue.running) {
      const before = JSON.stringify([state.config,state.ports.map(p => p.override),state.brightness,state.released,state.switch?.native_breathing]);
      applyRemote(message.state);
      const after = JSON.stringify([state.config,state.ports.map(p => p.override),state.brightness,state.released,state.switch?.native_breathing]);
      if (before !== after && !document.activeElement?.matches('input,select') && !dialog.open) renderPage();
    } else state.sfp = sfpEdits.receive(message.state.sfp);
    updateSfpControls();
  }
  if (message.type === 'frame') {
    setConnectionLost(!message.connected);
    message.ports.forEach(p => {const port = state.ports.find(item => item.id === p.port);if (port) {port.preview = normalizeHex(p.preview) || '#000000';port.facts = p.facts || port.facts;port.source = p.source || port.source;}});
    if (Array.isArray(message.sfp)) {state.sfp = sfpEdits.receive(message.sfp,{factsOnly:true});updateSfpControls();}
    updatePortCounts();
  }
};
backend.onDisconnect = () => {if (entered && state.mode === 'live') setConnectionLost(true);};

function syncNavigation() {
  const sidebar = document.querySelector('#sidebar');
  if (!sidebar) return;
  const open = sidebar.classList.contains('open');
  const narrow = matchMedia('(max-width:600px)').matches;
  sidebar.inert = narrow && !open;
  document.querySelector('.workspace').inert = narrow && open;
  document.querySelectorAll('[data-action="menu"]').forEach(menu => {menu.setAttribute('aria-expanded',open);menu.setAttribute('aria-controls','sidebar');});
}
function updateSavedLogin(session) {
  savedLogin = session?.has_saved_login ?? Boolean(session?.remembered);
  const checkbox = document.querySelector('#save-login');
  if (checkbox && !checkbox.dataset.edited) checkbox.checked = savedLogin;
  const form = document.querySelector('#login-form');
  if (form && !connecting) {
    const submit = form.querySelector('[type=submit]'),pending = session?.connecting && !session.logged_in;
    submit.disabled = Boolean(pending);submit.innerHTML = pending ? `${icon('refresh','spin')} Connecting with saved login…` : `Connect ${icon('arrow')}`;
    if (session?.remembered) for (const name of ['host','username']) if (!form.elements[name].value) form.elements[name].value = session.remembered[name] || '';
  }
  const error = document.querySelector('#login-error');
  const failure = session?.saved_login_error ? {code:'saved_login_unavailable'} : !session?.logged_in && !session?.connecting ? session?.error : null;
  if (error) {error.hidden = !failure;if (failure) {error.className = 'form-error';error.textContent = friendlyError(failure);}}
}
function render() {app.innerHTML = entered ? shellView(state) : loginView({...state,savedLogin});if (entered) {renderPage();syncNavigation();}}
function renderPage() {
  const page = document.querySelector('#page');if (!page) return;
  page.querySelectorAll('[data-disclosure]').forEach(el => disclosures.set(el.dataset.disclosure,el.open));
  persistView(state,localStorage);
  const renderers = {studio:() => studioView(state,color),effects:() => effectsView(state,filter,search),groups:() => groupsView(state),rules:() => rulesView(state),scenes:() => scenesView(state),activity:() => activityView(state),settings:() => settingsView({...state,savedLogin})};
  page.innerHTML = (renderers[state.page] || renderers.studio)();
  page.querySelectorAll('[data-disclosure]').forEach(el => {el.open = disclosures.get(el.dataset.disclosure) || false;});
  setConnectionLost(connectionLost);updateSelection();updatePortCounts();paintFrame();setEffectPending(effectTransaction.pending);
}
function setEffectPending(value) {
  const page = document.querySelector('#page');if (!page) return;
  const blocked = value || recovery.pending;
  page.setAttribute('aria-busy',String(blocked));
  page.querySelectorAll('input,select,button:not([data-action=navigate])').forEach(el => {
    if (blocked && !el.disabled) {el.dataset.effectBlocked = 'true';el.disabled = true;}
    else if (!blocked && el.dataset.effectBlocked) {delete el.dataset.effectBlocked;el.disabled = false;}
  });
  const plane = page.querySelector('#color-plane');if (plane) plane.setAttribute('aria-disabled',String(blocked));
  updateSfpControls();
}
function updateSfpControls({force = false} = {}) {
  const panel = document.querySelector('#sfp-controls');if (!panel) return;
  const ports = [...panel.querySelectorAll('[data-sfp-port]')].map(row => Number(row.dataset.sfpPort));
  if (JSON.stringify(ports) !== JSON.stringify((state.sfp || []).map(item => item.port))) panel.innerHTML = sfpLightingView(state);
  panel.querySelectorAll('[data-sfp-port]').forEach(row => {
    const item = state.sfp.find(value => value.port === Number(row.dataset.sfpPort));if (!item) return;
    row.querySelector('[data-sfp-indicator]').style.backgroundColor = sfpPreview(state,item,animationTime,dateForRules());
    row.querySelector('[data-sfp-link]').textContent = item.link === null ? 'Link unknown' : item.link ? 'Link online' : 'Link offline';
    row.querySelector('[data-sfp-anomaly]').hidden = item.anomaly !== true;
    const rule = sfpActiveRule(state,item,dateForRules()),ruleText = row.querySelector('[data-sfp-rule]');
    ruleText.hidden = !rule;ruleText.textContent = rule ? `Rule: ${rule.name}` : '';
    const status = sfpControlStatus(item),statusText = row.querySelector('[data-sfp-pending]');
    statusText.textContent = status.text;statusText.dataset.uncertain = String(status.retry);
    row.querySelector('[data-action="sfp-retry"]').hidden = !status.retry;
    row.querySelector('[data-sfp-color-controls]').hidden = item.mode !== 'color';
    row.querySelector('[data-sfp-secondary]').hidden = item.effect !== 'two_color';
    row.querySelector('[data-sfp-speed-control]').hidden = item.effect === 'steady';
    const blocked = recovery.pending || effectTransaction.pending || (state.mode === 'live' && connectionLost);
    row.querySelector('[data-action="sfp-retry"]').disabled = blocked;
    row.querySelectorAll('input,select').forEach(input => {
      const key = input.dataset.sfpColorKey || input.dataset.sfpLevelKey;
      const value = input.dataset.sfpMode ? item.mode : input.dataset.sfpEffect ? item.effect : input.dataset.sfpSpeed ? speedSliderValue(SFP_SPEED,item.period) : item[key];
      if ((force || document.activeElement !== input) && input.value !== String(value)) input.value = value;
      if (force && input.dataset.sfpHex) {input.classList.remove('invalid');input.setAttribute('aria-invalid','false');}
      input.disabled = blocked || (!input.dataset.sfpMode && item.mode !== 'color');
      if (input.dataset.sfpBrightness) {
        input.style.setProperty('--fill',`${item[key]}%`);input.setAttribute('aria-valuetext',`${item[key]}%`);
        row.querySelector(`[data-sfp-level="${key}"]`).textContent = `${item[key]}%`;
      }
      if (input.dataset.sfpSpeed) {
        const text = speedText(SFP_SPEED,item.period);input.setAttribute('aria-valuetext',text);
        input.style.setProperty('--fill',`${100 * (value - 2) / 28}%`);row.querySelector('[data-sfp-speed-value]').textContent = text;
      }
    });
  });
}
function setSfp(port,patch) {
  if (effectTransaction.pending || recovery.pending) {updateSfpControls({force:true});return false;}
  if (state.mode === 'live' && connectionLost) {toast('Reconnect before changing the switch.');updateSfpControls({force:true});return false;}
  const edit = sfpEdits.edit(port,patch);if (!edit) return false;
  state.sfp = sfpEdits.values;
  if (state.mode === 'live') send(`/sfp/${port}`,'PUT',edit.body,{sfpEdit:edit});
  else {sfpEdits.acknowledge(edit,demoSfp(state.sfp));state.sfp = sfpEdits.values;persist();}
  updateSfpControls();return true;
}
function navigate(page,{historyEntry = true} = {}) {
  if (!PAGES.includes(page)) return;
  pageScroll.set(state.page,app.scrollTop);state.page = page;
  if (historyEntry && location.hash !== `#${page}`) history.pushState(null,'',`#${page}`);
  render();app.scrollTo(0,pageScroll.get(page) || 0);document.querySelector('#main')?.focus({preventScroll:true});
}
function updatePortCounts() {
  const online = state.ports.filter(p => p.facts.link === true).length,offline = state.ports.filter(p => p.facts.link === false).length;
  if (document.querySelector('#online-count')) document.querySelector('#online-count').textContent = online;
  if (document.querySelector('#offline-count')) document.querySelector('#offline-count').textContent = offline;
  document.querySelectorAll('button[data-port]').forEach(el => {
    const port = state.ports.find(p => p.id === Number(el.dataset.port));if (!port) return;
    const label = `Port ${port.id}, ${port.facts.link == null ? 'status unknown' : port.facts.link ? 'online' : 'offline'}${port.override ? ', port color' : ''}`;
    if (el.getAttribute('aria-label') !== label) el.setAttribute('aria-label',label);
  });
  document.querySelectorAll('[data-port-source]').forEach(el => {
    const port = state.ports.find(p => p.id === Number(el.dataset.portSource));if (!port) return;
    const source = sourceForPort(state,port,dateForRules());
    el.textContent = {override:'Color',zone:'Effect',rule:'Rule',released:'UniFi',none:'—'}[source.kind] || '—';
    el.title = source.name || el.textContent;
  });
  const sources = document.querySelector('#output-sources');
  if (sources) {const summary = outputSummary(state,dateForRules());sources.textContent = [summary.manual ? `${summary.manual} using port colors` : '',summary.rules ? `${summary.rules} following rules` : ''].filter(Boolean).join(' · ');}
}
function updateSelection() {
  document.querySelectorAll('button[data-port]').forEach(el => el.setAttribute('aria-pressed',state.selected.includes(Number(el.dataset.port))));
  const count = document.querySelector('#selection-count');if (count) count.textContent = state.selected.length === 1 ? `Port ${state.selected[0]}` : `${state.selected.length} ports`;
}
function dateForRules() {const date = new Date();if (state.simulationTime) {const [h,m] = state.simulationTime.split(':').map(Number);date.setHours(h,m,0,0);}return date;}
function portColor(port,index) {
  if (state.released) return '#363A40';
  if (state.mode === 'live') return port.preview || '#000000';
  if (!state.lightsOn) return '#000000';
  const date = dateForRules();
  const zone = state.zones?.find(z => z.ports.includes(port.id));
  const config = zone?.effect || state.config,key = zone?.id || 'all';
  if (!previewClocks.has(key) || previewClocks.get(key).id !== config.id) previewClocks.set(key,{id:config.id,start:animationTime});
  const clock = previewClocks.get(key),elapsed = animationTime - clock.start;
  const phase = synchronizedBreathing(config) ? previewPhase(sharedBreathingClock,config,animationTime) : previewPhase(clock,config,elapsed);
  const colorPhase = previewColorPhase(clock.colors ||= {},config,elapsed);
  const animationState = (clock.ports ||= {})[port.id] ||= {};
  const peakTraffic = config.id === 'activity_glow' ? activityPeak(state.ports.filter(item => !zone || zone.ports.includes(item.id)).map(item => item.facts)) : undefined;
  let result = effectColor(config,zone ? zone.ports.indexOf(port.id) : index,zone?.ports.length || state.ports.length,elapsed,{...port.facts,phase,colorPhase,animationState,peakTraffic,hour:date.getHours() + date.getMinutes() / 60 + date.getSeconds() / 3600});
  let brightness = state.brightness;
  const rule = matchingRule(state.rules,port,date);
  if (rule) {result = renderLook(rule.look,result,animationTime);brightness = rule.look.brightness ?? brightness;}
  if (port.override) {result = port.override.color;brightness = port.override.brightness ?? brightness;}
  return dim(result,brightness / 100);
}
function paintFrame() {
  if (!entered) return;
  document.querySelectorAll('[data-sfp-port]').forEach(row => {
    const item = state.sfp.find(value => value.port === Number(row.dataset.sfpPort));
    if (item) {
      row.querySelector('[data-sfp-indicator]').style.backgroundColor = sfpPreview({...state,brightness:state.lightsOn ? state.brightness : 0},item,state.preferences.motion && !reducedMotion.matches ? animationTime : 0,dateForRules());
      const rule = sfpActiveRule(state,item,dateForRules()),label = row.querySelector('[data-sfp-rule]'),text = rule ? `Rule: ${rule.name}` : '';
      label.hidden = !rule;if (label.textContent !== text) label.textContent = text;
    }
  });
  document.querySelectorAll('[data-port]').forEach(el => {const index = state.ports.findIndex(p => p.id === Number(el.dataset.port));if (index >= 0) {el.style.setProperty('--port-color',portColor(state.ports[index],index));const number = el.querySelector('.port-number');if (number) number.style.visibility = state.preferences.labels ? 'visible' : 'hidden';}});
  document.querySelectorAll('[data-effect-preview]').forEach(el => {
    const config = defaultConfig(el.dataset.effectPreview);
    // Small card previews stay subdued, even for flashing effects.
    const t = !state.preferences.motion || reducedMotion.matches || ['strobe','lightning','police'].includes(config.id) ? 1 : animationTime;
    if (!cardPreviewStates.has(el)) cardPreviewStates.set(el,Array.from({length:8},() => ({})));
    const states = cardPreviewStates.get(el),facts = dataPreviewFacts(8,t),peakTraffic = activityPeak(facts);
    [...el.children].forEach((led,i) => {
      if (config.id === 'activity_glow' && states[i].last === undefined) effectColor(config,i,8,t - 1,{...facts[i],peakTraffic,animationState:states[i]});
      led.style.backgroundColor = effectColor(config,i,8,t,{...facts[i],peakTraffic,animationState:states[i]});
    });
  });
}
function animate(now) {
  const dt = Math.min(.1,(now - lastFrame) / 1000);lastFrame = now;
  if (!document.hidden) {
    animationTime += dt;
    paintFrame();
  }
  requestAnimationFrame(animate);
}
function syncColorControls() {
  const plane = document.querySelector('#color-plane');
  if (plane) {plane.style.setProperty('--hue',hsv[0]);plane.setAttribute('aria-valuenow',Math.round(hsv[1] * 100));plane.setAttribute('aria-valuetext',`Saturation ${Math.round(hsv[1] * 100)}%, value ${Math.round(hsv[2] * 100)}%`);const cursor = plane.querySelector('.color-cursor');cursor.style.left = `${hsv[1] * 100}%`;cursor.style.top = `${(1 - hsv[2]) * 100}%`;}
  const values = {hex:color,hue:hsv[0],...Object.fromEntries(['R','G','B'].map((channel,i) => [`rgb-${channel}`,hexToRgb(color)[i]]))};
  Object.entries(values).forEach(([id,value]) => {const el = document.getElementById(id);if (el && document.activeElement !== el) el.value = value;});
  document.querySelectorAll('.swatch').forEach(el => el.classList.toggle('selected',el.dataset.color === color));
  const brightness = document.getElementById('port-brightness');
  if (brightness) brightness.disabled = !state.ports.some(p => state.selected.includes(p.id) && p.override);
}
function setColor(value,{keepHue = false} = {}) {
  if (effectTransaction.pending || recovery.pending) return;
  const hex = normalizeHex(value);if (!hex) return;
  if (!state.selected.length) {toast('Select at least one port first.');return;}
  if (state.mode === 'live' && connectionLost) {toast('Reconnect before changing colors.');return;}
  color = hex;if (!keepHue) hsv = hexToHsv(color);
  state.ports.forEach(p => {if (state.selected.includes(p.id)) p.override = {color,brightness:p.override?.brightness ?? null};});
  // Include all current overrides so coalescing a drag cannot drop another port’s color.
  const ports = Object.fromEntries(state.ports.filter(p => p.override).map(p => [p.id,p.override]));
  send('/ports','PUT',{ports});syncColorControls();updatePortCounts();paintFrame();
}
function updateParam(key,value) {
  if (effectTransaction.pending || recovery.pending || !state.activeZone) return;
  state.config.params = paramsAfterChange(state.config,key,value,state.zones);
  const zoneId = state.activeZone || 'all';
  const zone = state.zones.find(z => z.id === zoneId);if (zone) zone.effect = structuredClone(state.config);
  if (synchronizedBreathing(state.config) && ['pace','playback'].includes(key)) state.zones = shareBreathingPace(state.zones,state.config);
  send(`/zones/${encodeURIComponent(zoneId)}/params`,'PATCH',{params:{...state.config.params}});persist();
}
function labelDialog() {const title = dialog.querySelector('h2');if (title) {title.id = 'dialog-title';dialog.setAttribute('aria-labelledby','dialog-title');}else dialog.removeAttribute('aria-labelledby');}
function openDialog(content) {dialog.innerHTML = content;labelDialog();if (!dialog.open) dialog.showModal();}
function closeDialog() {dialog.close();ruleDraft = null;}
function openRule(type = 'link',existing) {ruleDraft = structuredClone(existing || makeRule(type));openDialog(ruleEditor(ruleDraft,state));}
function rerenderRule() {
  const active = document.activeElement;
  const attribute = ['id','name','data-condition-op','data-condition-value','data-condition-start','data-condition-end','data-action'].find(key => active?.hasAttribute(key));
  const selector = attribute ? `[${attribute}="${CSS.escape(active.getAttribute(attribute))}"]` : null;
  dialog.innerHTML = ruleEditor(ruleDraft,state);labelDialog();
  (selector && dialog.querySelector(selector) || dialog.querySelector('[data-action=add-condition]'))?.focus({preventScroll:true});
}
async function rulesChanged(message) {
  if (state.mode === 'live') {
    await queue.idle();
    try {state.rules = await backend.request('/rules','PUT',{rules:structuredClone(state.rules)});}
    catch (error) {toast(friendlyError(error));try {const data = await backend.read();applyRemote(data.state,data.ports);} catch {setConnectionLost(true);}render();return false;}
  }
  log(message,'Rules updated','activity');persist();render();return true;
}
async function commitGroups(zones) {
  if (state.mode === 'live') {
    await queue.idle();
    try {applyRemote(await backend.request('/zones','PUT',{zones}));}
    catch (error) {toast(friendlyError(error));return false;}
  } else replaceGroups(state,zones);
  persist();return true;
}
function openNameDialog({title,value = '',action,id = ''}) {
  openDialog(`<div class="dialog-head"><h2>${title}</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>Name this set of colors, effects, groups, and rules.</p><form id="scene-form" data-kind="${action}" data-id="${esc(id)}"><label class="field"><span>Scene name</span><input name="sceneName" type="text" value="${esc(value)}" placeholder="Evening lighting" maxlength="40" required autofocus></label><div id="scene-error" role="alert" hidden></div><div class="dialog-actions"><button class="button ghost" type="button" data-action="close-dialog">Cancel</button><button class="button primary" type="submit">Save scene ${icon('check')}</button></div></form>`);
}
function confirmDelete(kind,id,name) {
  openDialog(`<div class="dialog-head"><h2>Delete ${kind}?</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>“${esc(name)}” will be removed from your ${kind === 'scene' ? 'library' : 'rules'}.</p><div class="dialog-actions">${button('Keep it','close-dialog','ghost')}<button class="button danger" data-action="confirm-delete" data-kind="${kind}" data-id="${esc(id)}">Delete ${kind}</button></div>`);
}
function portDetails() {
  const port = state.ports.find(p => p.id === state.selected[0]);if (!port) return;
  const f = port.facts,known = v => v === null || v === undefined ? 'Waiting for data' : v;
  openDialog(`<div class="dialog-head"><h2>Port ${port.id}</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>View the port’s connection and power status.</p><div class="port-facts">${[['Link',f.link == null ? 'Waiting for data' : f.link ? 'Online' : 'Offline'],['Speed',f.speed == null ? 'Waiting for data' : `${f.speed} Mbps`],['PoE',f.poe?.toUpperCase() || 'Waiting for data'],['Power',f.poe_power == null ? 'Waiting for data' : `${f.poe_power} W`],['Anomaly',f.anomaly == null ? 'Waiting for data' : f.anomaly ? 'Detected' : 'Clear'],['Spanning tree',known(f.stp_state)]].map(([label,value]) => `<div class="port-fact"><span>${label}</span><strong>${esc(value)}</strong></div>`).join('')}</div>${state.mode === 'simulator' ? `<form id="port-demo-form" data-id="${port.id}"><div class="eyebrow">TRY A DIFFERENT STATE</div><div class="form-grid" style="margin-top:16px"><label class="field"><span>Link</span><select name="link"><option value="true" ${f.link ? 'selected' : ''}>Online</option><option value="false" ${!f.link ? 'selected' : ''}>Offline</option></select></label><label class="field"><span>PoE class</span><select name="poe">${['off','poe','poe+','poe++','poe+++'].map(p => `<option value="${p}" ${f.poe === p ? 'selected' : ''}>${p === 'off' ? 'Off' : p.toUpperCase()}</option>`).join('')}</select></label><label class="field"><span>Anomaly</span><select name="anomaly"><option value="false" ${!f.anomaly ? 'selected' : ''}>Clear</option><option value="true" ${f.anomaly ? 'selected' : ''}>Detected</option></select></label><label class="field"><span>Link speed · Mbps</span><select name="speed">${[100,1000,2500,5000,10000].map(s => `<option value="${s}" ${f.speed === s ? 'selected' : ''}>${s}</option>`).join('')}</select></label></div><div class="dialog-actions"><button class="button primary" type="submit">Update demo port</button></div></form>` : ''}`);
}
async function connectFromForm(form) {
  if (connecting) return;connecting = true;
  sessionRestore.cancel();backend.close();
  const submit = form.querySelector('[type=submit]'),error = document.querySelector('#login-error'),data = new FormData(form);
  error.hidden = true;submit.disabled = true;submit.innerHTML = `${icon('refresh','spin')} Connecting…`;
  backend.token = String(data.get('token') || '');
  try {
    const session = await backend.login(String(data.get('host')).trim(),String(data.get('username')).trim(),String(data.get('password')),data.get('remember') === 'on');
    if (!session.logged_in) throw new Error('not_logged_in');
    updateSavedLogin(session);
    const {state:remote,ports} = await backend.read();
    state = restoreView(createState(),localStorage,location.hash);state.mode = 'live';backendSimulated = session.simulated === true;state.backendSimulated = backendSimulated;
    sfpEdits.reset([]);
    applyRemote(remote,ports);
    const presets = await backend.request('/presets').catch(() => ({}));loadPresets(presets);
    form.reset();entered = true;log(backendSimulated ? 'Backend simulator connected' : 'Connected to switch',backendSimulated ? 'Isolated test workspace' : 'SSH connection established','link');render();app.scrollTo(0,0);backend.subscribe();
  } catch (e) {error.className = 'form-error';error.textContent = friendlyError(e);error.hidden = false;submit.innerHTML = `Connect to switch ${icon('arrow')}`;submit.disabled = false;}
  finally {connecting = false;}
}
function loadPresets(presets) {state.scenes = Object.entries(presets).map(([name,snapshot]) => ({id:name,name,snapshot:{...snapshot,effect:snapshot.effect || snapshot.zones?.[0]?.effect?.id,params:snapshot.params || snapshot.zones?.[0]?.effect?.params}}));}
async function leaveWorkspace(logout = true) {
  sessionRestore.cancel();
  if (state.mode === 'live' && logout) {
    await queue.idle();
    try {updateSavedLogin(await backend.request('/session/logout','POST'));} catch (error) {
      openDialog(`<div class="dialog-head"><h2>Controller unavailable</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>The disconnect could not be confirmed. You can close this workspace; the controller may still be running your lights.</p><div class="dialog-actions">${button('Stay here','close-dialog','ghost')}${button('Close workspace','leave-local','primary')}</div>`);return;
    }
  }
  closeDialog();backend.close();setEffectCatalog(CATALOG);entered = false;connectionLost = false;backendSimulated = false;state = loadState(localStorage);sfpEdits.reset(state.sfp);render();app.scrollTo(0,0);
}
async function saveScene(form) {
  const name = new FormData(form).get('sceneName').trim(),id = form.dataset.id;
  const error = form.querySelector('#scene-error');
  if (!name || !/^[\p{L}\p{N} ._-]{1,40}$/u.test(name)) {error.className = 'form-error';error.textContent = 'Use letters, numbers, spaces, periods, hyphens, or underscores.';error.hidden = false;return;}
  if (state.scenes.some(s => s.name.toLowerCase() === name.toLowerCase() && s.id !== id)) {error.className = 'form-error';error.textContent = 'A scene already has that name. Choose another.';error.hidden = false;return;}
  if (state.scenes.length >= 50 && !id) {toast('Your library holds 50 scenes. Delete one to make room.');return;}
  if (state.mode === 'live') {
    if (id) {toast('Rename is available for local demo scenes. Save a new live scene to keep both.');return;}
    await queue.idle();
    try {loadPresets(await backend.request('/presets','POST',{name}));} catch (e) {toast(friendlyError(e));return;}
  } else if (id) state.scenes.find(s => s.id === id).name = name;
  else state.scenes.push({id:crypto.randomUUID(),name,snapshot:snapshot(state)});
  log(id ? 'Scene renamed' : 'Scene saved',name,'layers');persist();closeDialog();render();toast('Scene saved.');
}

document.addEventListener('submit',async event => {
  const form = event.target;if (!['login-form','rule-form','scene-form','port-demo-form','group-form'].includes(form.id)) return;event.preventDefault();
  if (recovery.pending) {toast('Wait for connection recovery to finish.');return;}
  if (effectTransaction.pending) {toast('Wait for the effect change to finish.');return;}
  if (entered && state.mode === 'live' && connectionLost) {toast('Reconnect before saving changes.');return;}
  if (form.id === 'login-form') await connectFromForm(form);
  if (form.id === 'scene-form') await saveScene(form);
  if (form.id === 'rule-form') {
    const next = readRuleForm(dialog,ruleDraft,state);
    if (!next.name || !next.conditions.length || next.ports?.length === 0 || next.conditions.some(c => ['in','not_in'].includes(c.op) && !c.value.length)) {const error = form.querySelector('#rule-error');error.className = 'form-error';error.textContent = 'Add a name, at least one condition, and the ports or values to match.';error.hidden = false;return;}
    const index = state.rules.findIndex(r => r.id === next.id);if (index >= 0) state.rules[index] = next;else state.rules.unshift(next);
    if (await rulesChanged('Rule saved')) {closeDialog();toast('Rule saved.');}
  }
  if (form.id === 'group-form') {
    const data = new FormData(form),existing = state.zones.find(z => z.id === form.dataset.id);
    const group = {id:existing?.id || crypto.randomUUID().replaceAll('-','').slice(0,24),name:data.get('groupName').trim(),ports:data.getAll('ports').map(Number),effect:existing?.effect.id === data.get('effect') ? existing.effect : defaultConfig(data.get('effect'))};
    if (!group.name || !group.ports.length) {const error = form.querySelector('#group-error');error.textContent = 'Give your group a name and at least one port.';error.className = 'form-error';error.hidden = false;return;}
    if (await commitGroups(assignGroup(state.zones,group,state.ports.map(p => p.id),state.sfp.map(light => light.port)))) {closeDialog();render();log('Port group saved',group.name,'server');toast('Group saved.');}
  }
  if (form.id === 'port-demo-form') {
    const data = new FormData(form),port = state.ports.find(p => p.id === Number(form.dataset.id));
    Object.assign(port.facts,{link:data.get('link') === 'true',poe:data.get('poe'),anomaly:data.get('anomaly') === 'true',speed:data.get('link') === 'true' ? Number(data.get('speed')) : 0});
    closeDialog();renderPage();log('Demo port updated',`Port ${port.id}`,'server');toast('Demo state updated.');
  }
});

document.addEventListener('click',async event => {
  const target = event.target.closest('[data-action]');if (!target || target.disabled) return;
  const action = target.dataset.action,id = target.dataset.id;
  if (recovery.pending && !['navigate','menu','about','close-dialog','disconnect','leave-local','demo'].includes(action)) {toast('Wait for connection recovery to finish.');return;}
  if (effectTransaction.pending && !['navigate','menu','about','close-dialog'].includes(action)) {toast('Wait for the effect change to finish.');return;}
  const liveMutations = ['effect','swatch','clear-pins','rule-toggle','rule-up','link-preset','power','param-toggle','apply-scene','confirm-delete'];
  if (state.mode === 'live' && connectionLost && liveMutations.includes(action)) {toast('Reconnect before changing the switch.');return;}
  switch (action) {
    case 'sfp-retry': {const item = state.sfp.find(value => value.port === Number(id));if (item) setSfp(item.port,sfpSettings(item));break;}
    case 'demo': sessionRestore.cancel();backend.close();entered = true;state = restoreView(loadState(localStorage),localStorage,location.hash);state.mode = 'simulator';connectionLost = false;sfpEdits.reset(state.sfp);if (!state.zones.some(z => z.id === state.activeZone)) state.activeZone = state.zones[0].id;state.config = structuredClone(state.zones.find(z => z.id === state.activeZone).effect);log('Demo workspace opened','All device data is simulated','monitor');render();app.scrollTo(0,0);break;
    case 'show-password': {const input = document.querySelector('input[name=password]');input.type = input.type === 'password' ? 'text' : 'password';target.textContent = input.type === 'password' ? 'Show' : 'Hide';target.setAttribute('aria-label',`${target.textContent} password`);break;}
    case 'navigate': navigate(target.dataset.page);break;
    case 'menu': document.querySelector('#sidebar').classList.toggle('open');syncNavigation();break;
    case 'about': openDialog(`<div class="dialog-head"><span class="eyebrow">About</span><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div>${aboutContent()}`);break;
    case 'close-dialog': closeDialog();break;
    case 'disconnect': await leaveWorkspace();break;
    case 'forget-login': openDialog(`<div class="dialog-head"><h2>Forget saved login?</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>This removes the saved login from this computer. Your current connection stays open.</p><div class="dialog-actions">${button('Cancel','close-dialog','ghost')}${button('Forget saved login','confirm-forget-login','danger')}</div>`);break;
    case 'confirm-forget-login': {
      target.disabled = true;
      try {updateSavedLogin(await backend.forgetLogin());closeDialog();renderPage();toast('Saved login removed.');}
      catch (error) {toast(friendlyError(error));target.disabled = false;}
      break;
    }
    case 'leave-local': closeDialog();await leaveWorkspace(false);break;
    case 'reconnect': try {const data = await backend.read();applyRemote(data.state,data.ports);renderPage();toast(data.state.connected ? 'Connection restored.' : 'The controller is reconnecting to the switch.');} catch (e) {toast(friendlyError(e));}break;
    case 'port': {
      const portId = Number(id);
      if (event.shiftKey || event.ctrlKey || event.metaKey) state.selected = state.selected.includes(portId) ? state.selected.filter(p => p !== portId) : [...state.selected,portId];
      else state.selected = [portId];
      state.inspector = 'color';const port = state.ports.find(p => p.id === portId);if (port.override) {color = port.override.color;hsv = hexToHsv(color);}
      renderPage();break;
    }
    case 'select-all': state.inspector = 'color';state.selected = state.ports.map(p => p.id);renderPage();break;
    case 'select': state.inspector = 'color';state.selected = state.ports.filter(p => target.dataset.value === 'all' || (target.dataset.value === 'online' && p.facts.link === true) || (target.dataset.value === 'offline' && p.facts.link === false)).map(p => p.id);renderPage();break;
    case 'swatch': setColor(target.dataset.color);log('Port color changed',`${state.selected.length} port${state.selected.length === 1 ? '' : 's'} · ${color}`,'sun');break;
    case 'clear-pins': {
      if (!state.selected.length) {toast('Select the ports you want to return to their effects.');break;}
      await queue.idle();
      if (state.mode === 'live') {try {for (const portId of state.selected) await backend.request(`/ports/${portId}`,'DELETE');} catch (e) {toast(friendlyError(e));break;}}
      state.ports.forEach(p => {if (state.selected.includes(p.id)) p.override = null;});persist();renderPage();toast('Selected ports are following effects and rules.');break;
    }
    case 'effect': {
      const config = effectConfigForSelection(id,state),zone = state.zones.find(z => z.id === state.activeZone) || state.zones[0];
      if (!zone) {toast('Create a port group to apply an effect.');break;}
      if (state.mode === 'live') {
        try {await effectTransaction.run(async () => {
          await queue.idle();
          try {if (state.released) await backend.request('/resume','POST');applyRemote(await applyZoneEffect(backend,zone,config));}
          catch (error) {try {const remote = await backend.read();applyRemote(remote.state,remote.ports);} catch {setConnectionLost(true);}throw error;}
        });}
        catch (error) {toast(friendlyError(error));renderPage();break;}
      } else {zone.effect = config;state.config = structuredClone(config);state.ports.forEach(p => {if (zone.ports.includes(p.id)) p.override = null;});persist();}
      state.inspector = 'effect';log('Effect changed',`${effectById(id).name} · ${zone.name}`,'sparkles');previewClocks.delete(zone.id);renderPage();toast(`${effectById(id).name} applied to ${zone.name}.`);break;
    }
    case 'new-group': openDialog(groupEditor(state));break;
    case 'edit-group': openDialog(groupEditor(state,state.zones.find(z => z.id === id)));break;
    case 'open-group': state.deviceTab = 'ethernet';state.inspector = 'effect';state.activeZone = id;state.config = structuredClone(state.zones.find(z => z.id === id).effect);navigate('studio');break;
    case 'delete-group': {
      const group = state.zones.find(z => z.id === id),other = state.zones.find(z => z.id !== id);
      openDialog(`<div class="dialog-head"><h2>Remove this group?</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><p>Ports in “${esc(group.name)}” will join “${esc(other.name)}” and follow its effect.</p><div class="dialog-actions">${button('Keep group','close-dialog','ghost')}<button class="button danger" data-action="confirm-group-delete" data-id="${esc(id)}">Remove group</button></div>`);break;
    }
    case 'confirm-group-delete': if (await commitGroups(removeGroup(state.zones,id))) {closeDialog();render();toast('Group removed.');}break;
    case 'device-tab': state.deviceTab = target.dataset.value;renderPage();document.querySelector(`#device-tab-${state.deviceTab}`)?.focus({preventScroll:true});break;
    case 'inspector-tab': state.inspector = target.dataset.value;renderPage();document.querySelector(`#tab-${state.inspector}`)?.focus({preventScroll:true});break;
    case 'param-toggle': {
      const key = target.dataset.key,effect = effectById(state.config.id);
      updateParam(key,!state.config.params[key]);
      if (effect.params.find(p => p.key === key)?.randomizes || effect.exclusive_params?.some(pair => pair.includes(key))) {
        renderPage();document.querySelector(`[data-action="param-toggle"][data-key="${CSS.escape(key)}"]`)?.focus({preventScroll:true});
      } else target.setAttribute('aria-checked',state.config.params[key]);
      break;
    }
    case 'filter': filter = target.dataset.value;renderPage();break;
    case 'power': {
      if (state.mode === 'live') {
        await queue.idle();
        try {
          applyRemote(await backend.request(state.released ? '/resume' : '/release','POST'));
        } catch (error) {toast(friendlyError(error));break;}
        renderPage();toast(state.released ? 'EE lighting stopped.' : 'EE lighting resumed.');
      } else {
        state.lightsOn = !state.lightsOn;renderPage();toast(state.lightsOn ? 'Lights on.' : 'Lights off.');
      }
      break;
    }
    case 'motion': state.preferences.motion = !state.preferences.motion;persist();renderPage();break;
    case 'labels': state.preferences.labels = !state.preferences.labels;persist();renderPage();break;
    case 'reset-clock': state.simulationTime = null;renderPage();break;
    case 'port-details': portDetails();break;
    case 'new-rule': openRule();break;
    case 'rule-template': openRule(target.dataset.type);break;
    case 'edit-rule': openRule('link',state.rules.find(r => r.id === id));break;
    case 'rule-toggle': {const rule = state.rules.find(r => r.id === id);rule.enabled = !rule.enabled;await rulesChanged(rule.enabled ? 'Rule enabled' : 'Rule disabled');break;}
    case 'rule-up': {const index = state.rules.findIndex(r => r.id === id);if (index > 0) {[state.rules[index - 1],state.rules[index]] = [state.rules[index],state.rules[index - 1]];await rulesChanged('Rule priority changed');}break;}
    case 'delete-rule': confirmDelete('rule',id,state.rules.find(r => r.id === id).name);break;
    case 'add-condition': ruleDraft = readRuleForm(dialog,ruleDraft,state);ruleDraft.conditions.push(initialCondition('link'));rerenderRule();break;
    case 'remove-condition': ruleDraft = readRuleForm(dialog,ruleDraft,state);ruleDraft.conditions.splice(Number(target.dataset.index),1);rerenderRule();break;
    case 'link-preset': {
      const offline = makeRule('link'),online = makeRule('link');offline.name = 'Offline · red';online.name = 'Online · white';online.conditions[0].value = true;online.look.color = '#FFFFFF';offline.look.color = '#FF0000';
      state.rules = state.rules.filter(r => !['Offline · red','Online · white'].includes(r.name));state.rules.push(offline,online);if (await rulesChanged('Link colors added')) toast('Link colors added. Select Use group lighting to release any port colors.');break;
    }
    case 'save-scene': openNameDialog({title:'Save scene',action:'save'});break;
    case 'rename-scene': openNameDialog({title:'Rename scene',value:state.scenes.find(s => s.id === id).name,action:'rename',id});break;
    case 'delete-scene': confirmDelete('scene',id,state.scenes.find(s => s.id === id).name);break;
    case 'apply-scene': {
      const scene = state.scenes.find(s => s.id === id);await queue.idle();
      if (state.mode === 'live') {try {const remote = await backend.request(`/presets/${encodeURIComponent(scene.name)}/apply`,'POST');applyRemote(remote);} catch (e) {toast(friendlyError(e));break;}}
      else {applySnapshot(state,scene.snapshot);sfpEdits.reset(state.sfp);}
      previewClocks.clear();log('Scene applied',scene.name,'layers');persist();renderPage();toast(`${scene.name} applied.`);break;
    }
    case 'confirm-delete': {
      if (target.dataset.kind === 'rule') {state.rules = state.rules.filter(r => r.id !== id);if (!await rulesChanged('Rule removed')) break;}
      else {if (state.mode === 'live') {try {loadPresets(await backend.request(`/presets/${encodeURIComponent(id)}`,'DELETE'));} catch (e) {toast(friendlyError(e));break;}}else state.scenes = state.scenes.filter(s => s.id !== id);persist();renderPage();}
      closeDialog();toast('Deleted.');break;
    }
    case 'clear-activity': state.activity = [];renderPage();break;
  }
});

document.addEventListener('input',event => {
  const el = event.target;
  if (effectTransaction.pending || recovery.pending) return;
  if (entered && state.mode === 'live' && connectionLost && !el.closest('dialog') && el.id !== 'effect-search') {toast('Reconnect before changing the switch.');renderPage();return;}
  if (el.dataset.sfpColor) setSfp(Number(el.dataset.sfpColor),{[el.dataset.sfpColorKey]:el.value});
  if (el.dataset.sfpBrightness) setSfp(Number(el.dataset.sfpBrightness),{[el.dataset.sfpLevelKey]:Number(el.value)});
  if (el.dataset.sfpSpeed) setSfp(Number(el.dataset.sfpSpeed),{period:speedParamValue(SFP_SPEED,el.value)});
  if (el.dataset.sfpHex) {
    const hex = normalizeHex(el.value);el.classList.toggle('invalid',!hex);el.setAttribute('aria-invalid',String(!hex));
    if (hex) setSfp(Number(el.dataset.sfpHex),{[el.dataset.sfpColorKey]:hex});
  }
  if (el.id === 'hue') {hsv[0] = Number(el.value);setColor(hsvToHex(...hsv),{keepHue:true});}
  if (el.id === 'hex') {const hex = normalizeHex(el.value);el.classList.toggle('invalid',!hex);el.setAttribute('aria-invalid',!hex);if (hex) setColor(hex);}
  if (el.id.startsWith('rgb-')) {const values = ['R','G','B'].map(c => Number(document.querySelector(`#rgb-${c}`).value));if (values.every(v => Number.isFinite(v) && v >= 0 && v <= 255)) setColor(rgbToHex(values));}
  if (el.id === 'global-brightness') {state.brightness = Number(el.value);state.lightsOn = true;send('/brightness','PUT',{value:state.brightness});document.querySelector('#global-brightness-value').textContent = `${el.value}%`;}
  if (el.id === 'port-brightness') {
    if (!state.selected.length) {toast('Select at least one port first.');return;}
    const ports = {};
    state.ports.forEach(p => {if (state.selected.includes(p.id) && p.override) p.override.brightness = Number(el.value);if (p.override) ports[p.id] = p.override;});
    send('/ports','PUT',{ports});document.querySelector('#port-brightness-value').textContent = `${el.value}%`;
  }
  if (el.matches('input[type=range]')) el.style.setProperty('--fill',`${100 * (el.value - el.min) / (el.max - el.min)}%`);
  if (el.id === 'effect-speed') {
    const param = speedParam(effectById(state.config.id),state.config.params);
    if (param) {
      const value = speedParamValue(param,el.value),text = speedText(param,value);
      updateParam(param.key,value);document.querySelector('#effect-speed-value').textContent = text;
      el.setAttribute('aria-valuetext',text);
    }
  }
  if (el.dataset.param) {
    const meta = presentParam(effectById(state.config.id).params.find(p => p.key === el.dataset.param),state);const value = meta.type === 'color' || meta.type === 'choice' ? el.value : Number(el.value);
    updateParam(el.dataset.param,value);const output = document.getElementById(`out-${el.dataset.param}`);if (output) output.textContent = paramText(meta,value);
    if (el.type === 'range') el.setAttribute('aria-valuetext',paramText(meta,value));
    if (meta.type === 'color') {const hex = document.querySelector(`[data-param-hex="${el.dataset.param}"]`);if (hex) hex.value = value;}
    if (el.dataset.param === 'playback') {renderPage();document.querySelector('#param-playback')?.focus({preventScroll:true});}
  }
  if (el.dataset.paramHex) {const hex = normalizeHex(el.value);if (hex) {updateParam(el.dataset.paramHex,hex);document.querySelector(`[data-param="${el.dataset.paramHex}"]`).value = hex;}}
  if (el.id === 'effect-search') {search = el.value;const pos = el.selectionStart;renderPage();const input = document.querySelector('#effect-search');input.focus();input.setSelectionRange(pos,pos);}
});
document.addEventListener('change',event => {
  const el = event.target;
  if (effectTransaction.pending || recovery.pending) return;
  if (el.dataset.sfpMode) {setSfp(Number(el.dataset.sfpMode),{mode:el.value});return;}
  if (el.dataset.sfpEffect) {setSfp(Number(el.dataset.sfpEffect),{effect:el.value});return;}
  if (el.dataset.sfpHex && !normalizeHex(el.value)) {
    toast('Enter a valid hex color, such as #87B9ED.');
    el.value = state.sfp.find(item => item.port === Number(el.dataset.sfpHex))?.[el.dataset.sfpColorKey] || '#FFFFFF';
    el.classList.remove('invalid');el.setAttribute('aria-invalid','false');return;
  }
  if (el.id === 'save-login') el.dataset.edited = 'true';
  if (el.name === 'inheritBrightness' && el.closest('#rule-form')) el.form.elements.brightness.disabled = el.checked;
  if (el.id === 'effect-group') {state.inspector = 'effect';state.activeZone = el.value;state.config = structuredClone(state.zones.find(z => z.id === el.value).effect);renderPage();}
  if (el.name === 'scope' && el.closest('#rule-form')) {ruleDraft = readRuleForm(dialog,ruleDraft,state);rerenderRule();dialog.querySelector('[name=scope]')?.focus();}
  if (el.name === 'style' && el.closest('#rule-form')) {ruleDraft = readRuleForm(dialog,ruleDraft,state);ruleDraft.look.rate = ruleRate(el.value);rerenderRule();dialog.querySelector('[name=style]')?.focus();}
  if (el.id === 'simulation-time') state.simulationTime = el.value || null;
  if (el.dataset.conditionField !== undefined) {ruleDraft = readRuleForm(dialog,ruleDraft,state);ruleDraft.conditions[Number(el.dataset.conditionField)] = initialCondition(el.value);rerenderRule();}
  if (el.dataset.conditionOp !== undefined) {
    const index = Number(el.dataset.conditionOp),previous = ruleDraft.conditions[index].op;
    ruleDraft = readRuleForm(dialog,ruleDraft,state);
    if (el.value === 'between' && previous !== 'between') ruleDraft.conditions[index].value = [0,1000];
    if (el.value !== 'between' && previous === 'between') ruleDraft.conditions[index].value = 1000;
    if (['in','not_in'].includes(el.value) && !['in','not_in'].includes(previous)) ruleDraft.conditions[index].value = [ruleDraft.conditions[index].value];
    if (!['in','not_in'].includes(el.value) && ['in','not_in'].includes(previous)) ruleDraft.conditions[index].value = ruleDraft.conditions[index].value[0] || fieldByKey(ruleDraft.conditions[index].field).choices[0];
    rerenderRule();
  }
});
document.addEventListener('focusout',event => {
  if (event.target.closest('[data-sfp-port]')) queueMicrotask(() => updateSfpControls());
});
let dragging = false;
function movePicker(event) {const plane = document.querySelector('#color-plane');if (!plane || effectTransaction.pending || recovery.pending) return;const rect = plane.getBoundingClientRect();hsv[1] = clamp((event.clientX - rect.left) / rect.width);hsv[2] = 1 - clamp((event.clientY - rect.top) / rect.height);setColor(hsvToHex(...hsv),{keepHue:true});}
document.addEventListener('pointerdown',event => {const plane = event.target.closest('#color-plane');if (!plane || effectTransaction.pending || recovery.pending) return;if (!state.selected.length) {toast('Select at least one port first.');return;}dragging = true;plane.setPointerCapture(event.pointerId);movePicker(event);});
document.addEventListener('pointermove',event => {if (dragging) movePicker(event);});
document.addEventListener('pointerup',() => {if (dragging) {dragging = false;log('Port color changed',`${state.selected.length} selected · ${color}`,'sun');}});
document.addEventListener('pointercancel',() => {dragging = false;});
document.addEventListener('keydown',event => {
  if (event.target.matches('[role=tab]') && ['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) {
    event.preventDefault();
    const tabs = [...event.target.closest('[role=tablist]').querySelectorAll('[role=tab]')],current = tabs.indexOf(event.target);
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
    tabs[index].click();
  }
  if (event.key === 'Escape' && document.querySelector('#sidebar.open')) {document.querySelector('#sidebar').classList.remove('open');syncNavigation();document.querySelector('.topbar [data-action="menu"]')?.focus();}
  if (event.target.id === 'color-plane' && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)) {
    if (effectTransaction.pending || recovery.pending) return;
    event.preventDefault();const step = event.shiftKey ? .1 : .01;
    if (event.key === 'ArrowLeft') hsv[1] = clamp(hsv[1] - step);if (event.key === 'ArrowRight') hsv[1] = clamp(hsv[1] + step);
    if (event.key === 'ArrowUp') hsv[2] = clamp(hsv[2] + step);if (event.key === 'ArrowDown') hsv[2] = clamp(hsv[2] - step);
    setColor(hsvToHex(...hsv),{keepHue:true});
  }
});
dialog.addEventListener('click',event => {if (event.target === dialog) {const rect = dialog.getBoundingClientRect();if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) closeDialog();}});
document.addEventListener('visibilitychange',() => {lastFrame = performance.now();});
window.addEventListener('popstate',() => {const page = location.hash.slice(1) || 'studio';if (entered && PAGES.includes(page)) navigate(page,{historyEntry:false});});
matchMedia('(max-width:600px)').addEventListener('change',syncNavigation);
const sessionRestore = new SessionRestore(backend,{
  available:() => !entered && !connecting,
  update:updateSavedLogin,
  enter:(session,{state:remote,ports},presets) => {
    state = restoreView(createState(),localStorage,location.hash);state.mode = 'live';backendSimulated = session.simulated === true;state.backendSimulated = backendSimulated;
    sfpEdits.reset([]);
    applyRemote(remote,ports);
    loadPresets(presets);
    entered = true;render();backend.subscribe();
  },
  onError:error => {const el = document.querySelector('#login-error');if (el) {el.className = 'form-error';el.textContent = friendlyError(error);el.hidden = false;}}
});
initWindowChrome(toast);
render();requestAnimationFrame(animate);backend.subscribe();sessionRestore.check();
