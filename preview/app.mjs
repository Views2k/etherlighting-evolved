import {sceneFromState,reviewSceneConflict} from './scene-review.mjs';
import {createPortHistory,openFindPorts,openLivePortDetails} from './port-tools.mjs';
import {openSavedLibrary,openSceneHistory} from './saved-library.mjs';
import {profileLayoutNotice} from './profile-preview.mjs';
import {previewDraftColors} from './draft-preview.mjs';
import { request as apiRequest } from './api.mjs';
import {commitScene} from './scene-transaction.mjs';
import {portRanges} from './port-topology.mjs';
import {sceneFingerprint} from './secondary-model.mjs';
import {hostScheduleDate,receiveScheduleClock} from './schedule-clock.mjs';
import { renderSwitchConnection, showConnectionStatus, showSceneChoice, handoffStatus } from './connections.mjs';
import { workspaceStorageKey, switchConnectionStatus, controllerConnectionStatus } from './workspace-session.mjs';
import { renderAutomation, renderProfiles, renderSettings, renderHelp } from './secondary.mjs';
import { lightingOperations, lightingSnapshot, controlSource, sceneAutomationMatches, equalSetting, sfpSettings, fixedColorSettings, fixedColorBrightness } from './lighting-state.mjs';
import {openColorPicker} from './color-picker.mjs';
import {effectDrafts,effectDraftForPort,stageEffectDraft,leaveSfpEffect,portEffect} from './effect-draft.mjs';
import {createRouteHistory} from './route-history.mjs';
import { renderEffectGallery, sfpPreviewColor } from './effect-gallery.mjs';
import { sfpSync, effectiveSfpLight, editableSfpLight, normalizeSfpBrightness, sfpEditTargets, sfpDurationRange, sfpEffectChange, sfpCandidateSettings, sfpNativeAvailable, sfpDraftPorts } from './sfp-sync.mjs';
import { createPortAnimator, portInk } from './port-animation.mjs';
import {createPortGlow, enterInspector, confirmPorts} from './motion.mjs';
import {getInspectorCollapsed,setInspectorCollapsed} from './preferences.mjs';
import {mountUplinkBandwidth} from './uplink-bandwidth.mjs';
import { portDisplayRgb, manualDisplayRgb, masterDisplayRgb } from './port-display.mjs';
import { LINK_SPEEDS, GROUP_NAME_LIMIT, groupNameError, portLabel, portPower, isUplink, linkStatus, portScopeOptions, selectPort, selectionHint, switchTitle } from './port-map.mjs';
import { EFFECTS, effectColor, defaultConfig, setEffectCatalog, previewPhase, previewColorPhase, resolvedColorParams, activityPeak, dim } from './effect-renderer.mjs';
import { matchingRule, matchingSfpRule, renderLook } from '../src/rules.mjs';
import { hexToHsv, hsvToHex, hexToRgb, rgbToHex, normalizeHex } from '../src/color.mjs';
import { icon } from '../src/icons.mjs';

const app = document.querySelector('#app');
const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const clone = value => structuredClone(value);
const hex = value => /^#[\da-f]{6}$/i.test(value || '') ? value : '#000000';
const number = (value, fallback = 0) => Number.isFinite(Number(value)) ? Number(value) : fallback;
const label = value => String(value).replaceAll('_', ' ').replace(/^./, c => c.toUpperCase());
let state = {}, effects = [], session = {}, page = 'Lighting', selected = new Set(), editor = 'effect';
let multipleSelection = false, selectionAnchor = null;
let secondaryCleanup, currentSocket, retryTimer, inspectorSignature = '';
let operation = Promise.resolve(), busy = 0, refreshTimer, disconnected = false, toastTimer;
let workspaceOptions = {live:false}, workspaceReady = false, openingWorkspace = null, closingWorkspace = false;
let activeColorPopover;
const edits = new Set();
let draft = {}, profiles = {}, pendingPlan = null, lastApplied = null, compareApplied = false;
let draftBaseScene=null;
let draftBaseline=null, draftConfirmation=null, saveUiTimer, requestedRuleId;
const request=(path,method,body)=>apiRequest(path,method,body,{workspace:session.workspace_id});
const workspaceRequest=()=>{const workspace=session.workspace_id;return async(path,method='GET',body)=>{
  if(workspace!==session.workspace_id)throw new Error('The connected switch changed. Review its current setup.');
  if(method!=='GET'&&['/api/scene','/api/resume'].includes(path)&&(disconnected||!state.connected))throw new Error('Reconnect the switch before applying. Your edits are preserved.');
  return apiRequest(path,method,body,{workspace});
};};
const galleryState = {};
let appliedCount = 0, copiedAppearance = null, navigationVersion = 0;
const previewClocks=new Map();
let portGlow, uplinkBandwidth;
let inspectorCollapsed=getInspectorCollapsed();
const portAnimator=createPortAnimator(port=>document.querySelector(`[data-port="${port}"]`),globalThis,{onPaint:colors=>portGlow?.paint(colors)});
const rememberedEffects=new Map(), previewStates=new Map();
const telemetryNodes=new WeakMap(), rangeProgress=new WeakMap();
let telemetryPaintAt=0;
const portHistory=createPortHistory();
let livePortDetails;
const currentSelectionHint=()=>selectionHint(navigator.userAgentData?.platform||navigator.platform,multipleSelection,
  navigator.maxTouchPoints>0||matchMedia('(pointer: coarse)').matches);

app.innerHTML = `<section class="app-shell">
  <header class="titlebar">
    <div class="window-brand"><div class="traffic-lights" aria-hidden="true"><span class="traffic-light close"></span><span class="traffic-light minimize"></span><span class="traffic-light maximize"></span></div><span class="wordmark">Etherlighting Evolved</span></div>
    <nav class="navigation" aria-label="Main navigation">${[['Lighting','grid'], ['Effects','wave'], ['Automation','activity'], ['Profiles','layers']].map(([name,symbol]) => `<button class="nav-button ${name === page ? 'selected' : ''}" data-page="${name}" ${name===page?'aria-current="page"':''}>${icon(symbol,'nav-icon')}<span>${name}</span></button>`).join('')}</nav>
    <div class="window-actions"><button class="button quiet" data-page="Help" aria-label="Help">${icon('info','nav-icon')}<span>Help</span></button><button class="button quiet" data-page="Settings" aria-label="Settings">${icon('settings','nav-icon')}<span>Settings</span></button><img class="brand-logo" src="/assets/logo.png" alt="Etherlighting Evolved"></div>
  </header>
  <aside class="pending-lighting-bar" id="pending-lighting" hidden><span>Lighting preview saved while you browse</span><button class="button" id="review-lighting">Review preview</button></aside>
  <main id="view" class="view"><div class="loading" role="status"><span class="loading-spinner"></span>Opening lighting workspace…</div></main>
  <footer class="statusbar"><span id="status">Opening workspace</span><span class="status-connections"><span id="connection" class="connection-state is-disconnected">Connecting…</span><span id="controller-connection" class="connection-state is-disconnected" hidden>Controller not connected</span></span></footer>
  <div class="toast" id="toast" role="status" hidden><span id="toast-message"></span><button class="button quiet" id="toast-dismiss" hidden>Dismiss</button></div>
</section>`;
const view = document.querySelector('#view');
const routeHistory=createRouteHistory();
document.querySelector('#review-lighting').onclick=()=>run(()=>navigate('Lighting',{keepDraft:true}));

function status(message, error = false) {
  const el = document.querySelector('#status'); el.textContent = message; el.classList.toggle('error', error);
}
function updateConnection() {
  if(document.hidden)return;
  showConnectionStatus(document.querySelector('#connection'),switchConnectionStatus({...session,reconnect_required:state.reconnect_required},disconnected,busy));
  let recovery=document.querySelector('#connection-recovery');
  if(!recovery){
    recovery=document.createElement('div');recovery.id='connection-recovery';recovery.className='connection-recovery';
    const message=document.createElement('span'),retry=document.createElement('button');
    message.textContent='Lighting paused after a connection interruption. Reconnect to recover your current setup.';
    retry.type='button';retry.className='button';retry.textContent='Reconnect switch';
    retry.addEventListener('click',()=>run(async()=>{
      retry.disabled=true;
      try{await request('/api/session/recover','POST');await refresh(false);notify('Switch connection restored');}
      finally{retry.disabled=false;}
    }));
    recovery.append(message,retry);document.querySelector('.titlebar').after(recovery);
  }
  recovery.hidden=!workspaceReady||!state.reconnect_required;
  const controllerField=document.querySelector('#controller-connection');
  controllerField.hidden=!workspaceReady||session.simulated;
  showConnectionStatus(controllerField,controllerConnectionStatus(state.controller,disconnected));
  document.querySelectorAll('[data-page]').forEach(button => {button.disabled=button.dataset.page==='Help'?false:!workspaceReady;});
}
function notify(message, error = false) {
  status(message, error);
  const el = document.querySelector('#toast');el.setAttribute('role',error?'alert':'status');document.querySelector('#toast-message').textContent=message;el.classList.toggle('error', error);el.hidden=false;
  const dismiss=document.querySelector('#toast-dismiss');dismiss.hidden=!error;dismiss.onclick=()=>{el.hidden=true;};
  clearTimeout(toastTimer);if(!error)toastTimer=setTimeout(()=>{el.hidden=true;},2500);
}
function setBusy(value) {
  busy += value; app.classList.toggle('is-saving', busy > 0);
  document.querySelectorAll('.workspace input,.workspace select,.workspace button,.rack-workspace-dialog #master,.draft-dialog button').forEach(control=>{
    if(busy>0){if(!Object.hasOwn(control.dataset,'previousDisabled'))control.dataset.previousDisabled=String(control.disabled);control.disabled=true;}
    else if(Object.hasOwn(control.dataset,'previousDisabled')){control.disabled=control.dataset.previousDisabled==='true';delete control.dataset.previousDisabled;}
  });
  updateConnection();
  if(!busy)updateDraftBar();
}
function serialize(action) {
  const task = operation.catch(() => {}).then(async () => {
    setBusy(1);
    try { return await action(); }
    finally { setBusy(-1); }
  });
  operation = task;
  return task;
}
async function run(action) {
  try { return await action(); }
  catch (error) {
    notify(error.message || 'Could not confirm the change. Try again.', true);
    try { await refresh(); } catch { /* Connection status remains visible. */ }
  }
}
function queueEdit(key) {
  if(!draftBaseline){draftBaseScene=sceneFromState(state);draftBaseline=sceneFingerprint(state);}
  edits.add(key); compareApplied=false;clearTimeout(saveUiTimer);saveUiTimer=setTimeout(saveUiState,150);updateDraftBar();portAnimator.setPreview(previewDraft); updateLive();
}
async function flushEdits() {
  const invalid=view.querySelector('input:invalid');if(invalid){if(!invalid.matches('[data-hex]'))invalid.reportValidity();invalid.focus();throw new Error('Correct the highlighted value before applying.');}
  if(!edits.size)return;
  await serialize(async()=>{
    const send=workspaceRequest(),workspace=session.workspace_id;
    if(disconnected||!state.connected)throw new Error('Reconnect the switch before applying. Your preview is preserved.');
    if(!pendingPlan){
      lightingOperations(state,draft);
      const before=await send('/api/scene');
      if(workspace!==session.workspace_id)throw new Error('The connected switch changed. Review its current setup.');
      let scene=lightingSnapshot(before.scene,state,draft);
      if(draftBaseline&&sceneFingerprint(before.scene)!==draftBaseline){
        if(!draftBaseScene)throw new Error('This saved preview needs a refresh. Discard it and review the current setup.');
        const reviewed=await reviewSceneConflict(draftBaseScene,before.scene,lightingSnapshot(draftBaseScene,state,draft));
        if(!reviewed)return;
        scene=reviewed;
      }
      pendingPlan=[];
      if(sceneFingerprint(scene)!==sceneFingerprint(before.scene))pendingPlan.push({key:'scene',before,scene});
      if(draft.mode!==undefined&&draft.mode!==state.mode)pendingPlan.push({key:'mode',mode:draft.mode});
      pendingPlan.workspace=workspace;appliedCount=0;lastApplied={before,mode:state.mode,workspace};
    }
    const plan=pendingPlan,history=lastApplied;
    try{
      while(plan.length){
        if(workspace!==session.workspace_id||plan.workspace!==workspace)throw new Error('The connected switch changed. Review its current setup before applying.');
        const item=plan[0];
        if(item.key==='scene'){
          try{history.after=await commitScene(item.before,item.scene,send);}
          catch(error){if(error.acceptedScene){history.after=error.acceptedScene;plan.shift();if(workspace===session.workspace_id)appliedCount++;}throw error;}
        }else{await send(item.mode==='running'?'/api/resume':'/api/release','POST');lastApplied=null;}
        if(workspace!==session.workspace_id)throw new Error('The connected switch changed. Review its current setup.');
        plan.shift();appliedCount++;updateDraftBar();
      }
    }catch(error){
      if(workspace!==session.workspace_id)throw error;
      if(!pendingPlan?.length){edits.clear();draft={};draftBaseline=null;draftBaseScene=null;pendingPlan=null;portAnimator.setPreview(null);}
      try{await refresh(false);}catch{}
      saveUiState();updateDraftBar();renderInspector();throw error;
    }
    for(const effect of effectDrafts(draft))rememberedEffects.set(effect.id,clone(effect));
    const controlChanged=draft.mode!==undefined;
    const changedPorts=draft.brightness!==undefined?allPorts():[...effectDrafts(draft).flatMap(effect=>effect.ports),...Object.keys(draft.colors||{}),...(draft.returnToEffect||[]),...sfpDraftPorts(state,draft)];
    edits.clear();draft={};draftBaseline=null;draftBaseScene=null;pendingPlan=null;compareApplied=false;portAnimator.setPreview(null);saveUiState();
    try{await refresh(false);const covered=sceneAutomationMatches(history.after?.scene||history.before.scene,state,changedPorts).map(item=>item.port);notify(covered.length?`Setup saved. Automation matches ${covered.length===1?'port':'ports'} ${portRanges(covered)}.`:'Changes applied');if(!controlChanged)confirmPorts(changedPorts.filter(id=>!covered.includes(Number(id))));}
    catch{notify('Changes were applied. Status refresh is unavailable; reconnecting will refresh it.',true);}
    if(controlChanged)lastApplied=null;
  });
  updateDraftBar();renderInspector();updateUndo();focusLightingControls();
}
function discardDraft(){const partial=Boolean(pendingPlan);edits.clear();draft={};draftBaseline=null;draftBaseScene=null;pendingPlan=null;compareApplied=false;saveUiState();portAnimator.setPreview(null);updateDraftBar();renderInspector();updateLive();focusLightingControls();if(partial)notify('Preview discarded. Saved changes remain applied.');}
function draftSummary(){
  const changes=[];
  for(const effect of effectDrafts(draft)){const name=effectSpec(effect.id)?.name||'Effect';if(!changes.includes(name))changes.push(name);}
  if(Object.keys(draft.colors||{}).length)changes.push('Fixed colors');
  if(draft.returnToEffect?.length)changes.push('Follow group effect');
  if(Object.keys(draft.sfp||{}).length||draft.sfpDetached?.length)changes.push('SFP lighting');
  if(draft.sfp_sync)changes.push('SFP synchronization');
  const effectPorts=effectDrafts(draft).flatMap(effect=>effect.ports);
  const targets=[...new Set([...(effectPorts||[]),...Object.keys(draft.colors||{}).map(Number),...(draft.returnToEffect||[]),...sfpDraftPorts(state,draft)])].sort((a,b)=>a-b);
  const scope=targets.length?selectionName(targets):'Entire setup';
  if(draft.brightness!==undefined)changes.push('Default brightness');
  if(draft.mode)changes.push(draft.mode==='running'?'Enable EE lighting':'Return to UniFi');
  return `${changes.join(', ')} · ${scope} · Unsaved preview`;
}
function updateDraftBar(){
  updateUndo();
  const pending=document.querySelector('#pending-lighting');if(pending){pending.hidden=!edits.size||page==='Lighting'||page==='Effects';pending.querySelector('span').textContent=draftSummary();}
  const bar=document.querySelector('#draft-actions');if(bar)bar.hidden=!edits.size;
  const label=bar?.querySelector('[data-draft-label]');if(label)label.textContent=pendingPlan?`${appliedCount} confirmed · ${pendingPlan.length} remaining`:draftSummary();
  const apply=bar?.querySelector('#apply-draft');if(apply){apply.disabled=Boolean(view.querySelector('input:invalid'))||busy>0||disconnected||!state.connected;apply.textContent=busy?'Applying…':pendingPlan?'Retry remaining changes':'Apply changes';apply.title=disconnected||!state.connected?'Reconnect the switch to apply this preview.':'';}
  document.querySelector('#inspector')?.classList.toggle('awaiting-confirmation',Boolean(pendingPlan));
  document.querySelectorAll('#inspector input,#inspector select,#inspector button,#master,#toggle-output').forEach(control=>{if(pendingPlan){control.dataset.partialDisabled='true';control.disabled=true;}else if(control.dataset.partialDisabled){control.disabled=false;delete control.dataset.partialDisabled;}});
  const compare=bar?.querySelector('#compare-draft');if(compare){compare.textContent=compareApplied?'Show preview':'Show applied';compare.setAttribute('aria-pressed',String(compareApplied));}
  const follow=document.querySelector('#restore-effect');if(follow)follow.disabled=Boolean(busy||pendingPlan)||!canFollowGroup();
  status(edits.size?(compareApplied?'Showing applied lighting':'Previewing changes'):'Lighting output');
}
async function confirmDraft(){
  if(busy)await operation.catch(()=>{});
  if(!edits.size)return true;
  if(draftConfirmation)return draftConfirmation;
  draftConfirmation=new Promise(resolve=>{
    const dialog=document.createElement('dialog');dialog.className='dialog draft-dialog';dialog.setAttribute('aria-label','Unsaved lighting changes');
    dialog.innerHTML='<form method="dialog"><header class="dialog-header"><h2>Unapplied lighting changes</h2></header><p>Your lighting edits are being previewed.</p><p class="inline-error" role="alert"></p><footer class="dialog-footer"><button class="button primary" value="stay" autofocus>Keep editing</button><button class="button quiet" value="discard">Discard</button><button class="button" type="button" id="confirm-apply">Apply changes</button></footer></form>';
    document.body.append(dialog);dialog.showModal();
    dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault();});
    dialog.querySelector('#confirm-apply').onclick=async()=>{const button=dialog.querySelector('#confirm-apply');button.disabled=true;try{await flushEdits();dialog.close('applied');}catch(error){dialog.querySelector('.inline-error').textContent=error.message;}finally{button.disabled=false;}};
    dialog.addEventListener('close',()=>{const result=dialog.returnValue;if(result==='discard')discardDraft();dialog.remove();if(result==='applied')focusLightingControls();draftConfirmation=null;resolve(result==='discard'||result==='applied');},{once:true});
  });
  return draftConfirmation;
}
async function refresh(rebuild = true) {
  const workspace=session.workspace_id,send=workspaceRequest();
  const [next, ports] = await Promise.all([send('/api/state'), send('/api/ports')]);
  if(workspace!==session.workspace_id)return;
  receiveScheduleClock(next.schedule_clock);
  state = {...next, ports};
  if(lastApplied?.after&&sceneFingerprint(state)!==sceneFingerprint(lastApplied.after.scene))lastApplied=null;
  updateConnection();
  updateLive();
  if(page==='Effects')secondaryCleanup?.update?.({state,selectedPorts:[...selected],effectDrafts:rememberedEffects,draft});
  if(page==='Automation')secondaryCleanup?.update?.(state);
  if(page==='Profiles')secondaryCleanup?.update?.(state);
  if(page==='Settings')secondaryCleanup?.update?.(state,session,disconnected);
  if (page === 'Lighting') { updateGroups(); if (rebuild) reconcileEditor(true); }
}
function isSfpPort(id) { return state.sfp?.some(port=>port.port===id)===true; }
function copperSelection() { return (state.ports||[]).map(port=>port.port).filter(port=>selected.has(port)).sort((a,b)=>a-b); }
function sfpSelection() { return (state.sfp||[]).map(port=>port.port).filter(port=>selected.has(port)).sort((a,b)=>a-b); }
function canFollowGroup(){return copperSelection().some(id=>{
  if(Object.hasOwn(draft.colors||{},id))return true;
  if(draft.returnToEffect?.includes(id)||effectDraftForPort(draft,id)?.clearOverrides)return false;
  const port=state.ports.find(port=>port.port===id);
  return Boolean(state.overrides?.[id]||port?.override||port?.source?.kind==='override');
});}
function selectionName(ports = [...selected].sort((a,b) => a-b)) {
  if(!ports.length)return 'No ports selected';
  if(ports.length===allPorts().length&&allPorts().every(id=>ports.includes(id)))return 'All ports';
  if(ports.length===state.ports?.length&&state.ports.every(port=>ports.includes(port.port)))return 'All Ethernet ports';
  if(ports.length===state.sfp?.length&&state.sfp.every(p=>ports.includes(p.port)))return 'All SFP ports';
  if(ports.length===1)return `${isSfpPort(ports[0])?'SFP':'Port'} ${ports[0]}`;
  return `Ports ${portRanges(ports)}`;
}
function zoneFor(port) { return state.zones?.find(zone => zone.ports.includes(port)); }
function effectSignature(effect) { return effect ? JSON.stringify([effect.id,Object.entries(effect.params||{}).sort(([a],[b])=>a.localeCompare(b))]) : ''; }
function effectSelection() { return [...copperSelection(),...sfpSelection()]; }
function selectionEffects() { return effectSelection().map(port=>effectSignature(portEffect(state,draft,port))); }
function groupedSfpSelection() { return sfpSelection().filter(port=>portEffect(state,draft,port)); }
function useSfpInspector() { return sfpSelection().length&&!copperSelection().length&&!groupedSfpSelection().length; }
function hasMixedEffects() { return new Set(selectionEffects()).size>1; }
function currentEffect() { return hasMixedEffects() ? undefined : portEffect(state,draft,effectSelection()[0]); }
function effectSpec(id) { return effects.find(effect => effect.id === id); }

async function chooseEffect(config) {
  const ports = config.ports || effectSelection();
  if (!ports.length) { notify('Select ports to apply an effect.'); return; }
  selected=new Set(ports);
  stageEffect({...clone(config),ports,clearOverrides:true});editor='effect';inspectorSignature='';
  await navigate('Lighting',{keepDraft:true});
  focusEditor(true);
}
function focusEditor(reveal=false){
  if(reveal){
    const opener=['#expand-inspector','#open-port-controls'].map(id=>document.querySelector(id)).find(node=>node?.getClientRects().length);
    opener?.click();
  }
  const heading=document.querySelector('#inspector h2');
  if(heading?.getClientRects().length){heading.tabIndex=-1;heading.focus();}
  else document.querySelector('#expand-inspector')?.focus({preventScroll:true});
}
function focusLightingControls(){if(page==='Lighting'&&!document.querySelector('dialog[open]'))focusEditor();}
function stageEffect(config){
  draft=stageEffectDraft(draft,config,`group-${crypto.randomUUID()}`,state.ports.map(port=>port.port));
  queueEdit('effect');
}

async function navigate(target,{keepDraft=false,fromHistory=false,historyEntry}={}) {
  if(!workspaceReady){if(target==='Help')showLoginHelp();return;}
  const version=++navigationVersion;
  await routeHistory.ready();if(version!==navigationVersion)return;
  if(fromHistory&&historyEntry){if(!await routeHistory.reach(historyEntry))return;}
  else if(!fromHistory)await routeHistory.restore();
  if(version!==navigationVersion)return;
  if (target === page && (view.querySelector('.studio')||secondaryCleanup)) {routeHistory.commit(page,{fromHistory});return;}
  const canceled=async()=>{if(version===navigationVersion&&fromHistory&&!await routeHistory.restore()&&version===navigationVersion)notify('Your preview is preserved. Use the app navigation to continue.');};
  if (secondaryCleanup?.canLeave) {
    const allowed=await secondaryCleanup.canLeave();if(version!==navigationVersion)return;
    if(!allowed){await canceled();return;}
  }
  const invalid=secondaryCleanup?.canLeave?null:view.querySelector('input:invalid');if(invalid){invalid.focus();notify('Correct the highlighted value before leaving this page.',true);await canceled();return;}
  if(!keepDraft&&pendingPlan){
    const allowed=await confirmDraft();if(version!==navigationVersion)return;
    if(!allowed){await canceled();return;}
  }
  await operation.catch(()=>{});if(version!==navigationVersion)return;document.querySelector('.rack-workspace-dialog')?.close();activeColorPopover?.();secondaryCleanup?.(); secondaryCleanup = undefined;portAnimator.stop();portGlow?.dispose();portGlow=null;uplinkBandwidth?.dispose();uplinkBandwidth=null;
  page = target;document.title=`${page} · Etherlighting Evolved`;
  routeHistory.commit(page,{fromHistory});
  saveUiState();
  document.querySelectorAll('[data-page]').forEach(button => {
    const active = button.dataset.page === page; button.classList.toggle('selected', active);if(active)button.setAttribute('aria-current','page');else button.removeAttribute('aria-current');
  });
  view.classList.remove('view-enter'); void view.offsetWidth; view.classList.add('view-enter');
  if (page === 'Lighting') renderStudio();
  else if(page==='Effects'){
    secondaryCleanup=renderEffectGallery(view,{effects,state,simulated:session.simulated,selectedPorts:[...selected],effectDrafts:rememberedEffects,draft,galleryState,onChoose:chooseEffect,onChooseSfp:async({effect,ports})=>{selected=new Set(ports);draft=leaveSfpEffect(draft,groupedSfpSelection());if(sfpSync(state,draft).mode==='ethernet')draft.sfp_sync={...sfpSync(state,draft),mode:'together'};draft.sfp??={};for(const port of sfpEditTargets(state,ports,draft))draft.sfp[port]=normalizeSfpBrightness({...sfpSettings(sfpCandidateSettings(state,port,effect,draft)),mode:'color'});queueEdit('sfp');await navigate('Lighting',{keepDraft:true});focusEditor(true);},onChangeSelection:async()=>{await navigate('Lighting');document.querySelector('#select-all')?.focus();}});
  }else {
    view.innerHTML = '';
    const renderer = {Automation: renderAutomation, Profiles: renderProfiles, Settings: renderSettings,Help:renderHelp}[page];
    secondaryCleanup = await renderer(view, {request:workspaceRequest(), state, effects,getState:()=>state,onApplyProfile:applyProfile,getDraftScene:previewScene,onSavePreview:savePreviewProfile,onUndoApply:undoLastApply,canUndoApply:()=>Boolean(lastApplied?.after)&&!edits.size,initialRuleId:requestedRuleId,onBeforeMutation:confirmDraft,onHelp:()=>navigate('Help'),onSessionChange:async(next,notice)=>{await acceptSession(next);if(notice)notify(notice.message,notice.error);},onCloseWorkspace:closeWorkspace,onChange: async () => {await refresh(false); }});
    requestedRuleId=undefined;
  }
  updateDraftBar();
}

function renderStudio() {
  view.innerHTML = `<div class="studio"><section class="workspace" data-inspector-collapsed="${inspectorCollapsed}">
    <div class="workspace-toolbar"><div class="group-control"><label class="sr-only" for="group-select">Port group</label><select id="group-select"></select><button class="button quiet" id="manage-groups">Groups</button></div><span id="quick-profile"></span><button class="button quiet" id="save-scene">Profiles</button><button class="button quiet" id="undo-last" disabled>Undo last apply</button><button class="button quiet" id="scene-history">History</button><button class="button quiet" id="saved-selections">Saved selections</button><button class="button quiet" id="find-port">Find port</button><button class="button quiet" id="toggle-output"></button></div>
    <div class="studio-content"><section class="canvas-area">
      <div class="port-map" role="group" aria-label="Switch ports" aria-describedby="selection-hint">
        <div class="faceplate-toolbar">
          <div class="canvas-heading"><div><h1><span id="switch-title">${escape(switchTitle(state))}</span> ${session.simulated?'<span class="demo-badge">Demo</span>':''}</h1><p class="muted" id="map-output-status">Select ports to preview and apply lighting.</p></div><div id="uplink-bandwidth"></div></div>
          <div class="selection-toolbar"><div class="selection-chips" role="group" aria-label="Select ports"><button class="button quiet" id="select-all">All ports</button><button class="button quiet" data-select="ethernet">Ethernet</button><button class="button quiet" data-select="sfp">SFP</button><button class="button quiet" id="select-none">None</button></div><button class="button quiet" id="select-multiple" aria-pressed="${multipleSelection}">Select multiple</button><button class="button quiet" id="port-details">Port details</button><button class="button quiet" id="expand-inspector" aria-controls="inspector" aria-expanded="false">${icon('panel')} Port controls</button><button class="button primary mobile-only" id="open-port-controls">Port controls</button><button class="button quiet rack-only" id="rack-workspace-toggle" aria-haspopup="dialog">Workspace</button></div>
        </div>
        <section class="port-group"><div class="port-group-heading"><h2>Ethernet</h2><span id="copper-range"></span></div><div class="port-bank copper-bank" id="copper-ports"></div></section><section class="port-group sfp-group"><div class="port-group-heading"><h2>SFP</h2><span id="sfp-range"></span></div><div class="port-bank sfp-bank" id="sfp-ports"></div></section>
        <div class="rack-meter-slot"></div>
        <div class="canvas-footer"><div class="link-legend"><span class="current-speed-legend"></span><span class="uplink-label">${uplinkIcon()} Uplink</span><span>${poeIcon()} PoE</span></div><span id="selected-label" class="selection-count"></span></div>
      </div>
    </section><section class="inspector" id="inspector" aria-label="Port controls"></section></div>
      <dialog class="dialog map-key" aria-labelledby="map-key-title">
        <form method="dialog" class="dialog-header"><h2 id="map-key-title">Port map key</h2><button class="button quiet">Done</button></form>
        <div class="link-legend">${speedLegend()}<span class="uplink-label">${uplinkIcon()} Uplink</span><span>${poeIcon()} PoE</span></div>
        <p id="selection-hint">${escape(currentSelectionHint())}</p>
        <p id="brightness-description">Default brightness applies to effects and SFP lighting. Fixed port colors and rules with their own brightness keep that setting.</p>
        <button class="button quiet" id="lighting-help">Lighting help</button>
      </dialog>
    <div class="output-dock">
      <div class="brightness-controls"><div class="output-control"><label for="master">Default brightness</label><input id="master" aria-describedby="brightness-description" type="range" min="0" max="100" step="1" value="${number(draft.brightness??state.brightness,100)}"><output id="master-value">${number(draft.brightness??state.brightness,100)}%</output></div><button class="button quiet" id="open-map-key" aria-haspopup="dialog">Map key</button><span class="help-text ownership-preview" id="ownership-preview" hidden></span></div>
      <div class="draft-actions" id="draft-actions" hidden><span data-draft-label>Unsaved preview</span><button class="button quiet" id="compare-draft">Show applied</button><button class="button quiet" id="cancel-draft">Discard</button><button class="button primary" id="apply-draft">Apply changes</button></div>
    </div></section></div>`;
  const meterHost=view.querySelector('#uplink-bandwidth'), meterHome=meterHost.parentNode;
  const legend=view.querySelector('.canvas-footer .link-legend'), legendHome=legend.parentNode;
  const dock=view.querySelector('.output-dock'), meterSlot=view.querySelector('.rack-meter-slot');
  const rack=matchMedia('(min-width: 900px) and (min-height: 361px) and (max-height: 500px)');
  function placeInRack() {
    (rack.matches?meterSlot:meterHome).append(meterHost);
    if(rack.matches)dock.append(legend);else legendHome.prepend(legend);
  }
  placeInRack();
  rack.addEventListener('change',placeInRack);
  secondaryCleanup=()=>rack.removeEventListener('change',placeInRack);
  uplinkBandwidth?.dispose();uplinkBandwidth=mountUplinkBandwidth(meterHost);
  renderPorts();updateGroups();renderInspector();updateLive();updateDraftBar();renderQuickProfiles();updateUndo();
  document.querySelector('#apply-draft').onclick=()=>run(flushEdits);
  document.querySelector('#cancel-draft').onclick=discardDraft;
  document.querySelector('#compare-draft').onclick=()=>{compareApplied=!compareApplied;updateDraftBar();};
  document.querySelector('#group-select').onchange=event=>{const option=portScopeOptions(state).find(item=>item.value===event.target.value);if(option)setSelection(option.ports);};
  document.querySelector('#manage-groups').onclick=()=>run(openGroups);
  document.querySelector('#rack-workspace-toggle').onclick=openWorkspaceTools;
  document.querySelector('#select-all').onclick=()=>setSelection(allPorts());
  document.querySelector('#select-none').onclick=()=>setSelection([]);
  document.querySelector('[data-select="ethernet"]').onclick=()=>setSelection(state.ports.map(p=>p.port));
  document.querySelector('[data-select="sfp"]').onclick=()=>setSelection(state.sfp.map(p=>p.port));
  document.querySelector('#select-multiple').onclick=event=>{multipleSelection=!multipleSelection;event.currentTarget.setAttribute('aria-pressed',String(multipleSelection));document.querySelector('#selection-hint').textContent=currentSelectionHint();};
  document.querySelector('#save-scene').onclick=()=>run(()=>navigate('Profiles'));
  document.querySelector('#open-map-key').onclick=()=>document.querySelector('.map-key').showModal();
  document.querySelector('#lighting-help').onclick=()=>{document.querySelector('.map-key').close();run(()=>navigate('Help'));};
  document.querySelector('#port-details').onclick=openPortDetails;
  document.querySelector('#find-port').onclick=()=>openFindPorts({getState:()=>state,onSelect:setSelection});
  document.querySelector('#saved-selections').onclick=()=>run(()=>openSavedLibrary({kind:'selection',request:workspaceRequest(),getState:()=>state,getValue:()=>({ports:[...selected]}),onUse:value=>setSelection(value.ports)}));
  document.querySelector('#scene-history').onclick=()=>run(async()=>{
    if(!await confirmDraft())return;
    const before=await request('/api/scene');
    await openSceneHistory({request:workspaceRequest(),onRestore:async entry=>{
      await request('/api/history/'+encodeURIComponent(entry.id)+'/restore','POST',{expected_revision:before.revision});
      await refresh();notify('Lighting setup restored');
    }});
  });
  document.querySelector('#expand-inspector').onclick=()=>toggleInspector(false);
  document.querySelector('#open-port-controls').onclick=()=>{const studio=document.querySelector('.studio-content');studio.dataset.canvasScroll=String(studio.scrollTop);studio.classList.add('is-editing-ports');studio.scrollTop=0;document.querySelector('#back-to-ports')?.focus({preventScroll:true});};
  document.querySelector('#undo-last').onclick=()=>run(undoLastApply);
  document.querySelector('#toggle-output').onclick=()=>{if(draft.mode!==undefined){delete draft.mode;edits.delete('mode');compareApplied=false;saveUiState();portAnimator.setPreview(edits.size?previewDraft:null);updateDraftBar();updateLive();}else{draft.mode=state.mode==='running'?'released':'running';queueEdit('mode');}};
  document.querySelector('#master').oninput=event=>{const value=Number(event.target.value);document.querySelector('#master-value').textContent=`${value}%`;if(value===state.brightness){delete draft.brightness;edits.delete('master');if(!edits.size){draftBaseline=null;draftBaseScene=null;}saveUiState();updateDraftBar();portAnimator.setPreview(edits.size?previewDraft:null);updateLive();}else{draft.brightness=value;queueEdit('master');}};
  updateRangeProgress();
}
function openWorkspaceTools() {
  const toolbar=document.querySelector('.workspace-toolbar');
  if(!toolbar||toolbar.closest('dialog'))return;
  const dialog=document.createElement('dialog');dialog.className='dialog rack-workspace-dialog';dialog.setAttribute('aria-label','Workspace');
  dialog.innerHTML='<header class="dialog-header"><h2>Workspace</h2><button class="button quiet" type="button">Done</button></header>';
  const phone=matchMedia('(max-width: 700px)');
  const layout=phone.matches?phone:matchMedia('(max-height: 500px) and (min-width: 900px)');
  const sections=phone.matches
    ? [document.querySelector('.brightness-controls'),toolbar,document.querySelector('.app-shell > .statusbar')]
    : [toolbar];
  const homes=sections.map(section=>{
    const place=document.createComment('Workspace tools');
    section.before(place);
    dialog.append(section);
    return place;
  });
  document.body.append(dialog);
  const close=()=>dialog.close();
  const action=event=>{if(event.target.closest('button'))close();};
  dialog.querySelector('.dialog-header button').onclick=close;
  toolbar.addEventListener('click',action,true);
  layout.addEventListener('change',close);
  dialog.addEventListener('close',()=>{
    toolbar.removeEventListener('click',action,true);
    layout.removeEventListener('change',close);
    sections.forEach((section,index)=>homes[index].replaceWith(section));
    dialog.remove();
    if(page==='Lighting'&&!document.querySelector('dialog[open]'))document.querySelector('#rack-workspace-toggle')?.focus({preventScroll:true});
  },{once:true});
  dialog.showModal();
}
function allPorts(){return [...(state.ports||[]),...(state.sfp||[])].map(p=>p.port);}
function updateUndo(){const button=document.querySelector('#undo-last');if(button){button.disabled=!lastApplied?.after||Boolean(edits.size);button.title=edits.size?'Apply or discard the preview before undoing.':!lastApplied?.after?'Apply a lighting change to enable Undo.':'Restore the lighting before the last Apply.';}}
async function undoLastApply(){
  if(!lastApplied?.after||!await confirmDraft())return false;
  await serialize(async()=>{
    const saved=lastApplied;
    if(saved.mode!==state.mode)throw new Error('Lighting control changed. Review the current setup before undoing.');
    if(saved.workspace!==session.workspace_id)throw new Error('The connected switch changed.');
    await request('/api/scene','PUT',{scene:saved.before.scene,expected_revision:saved.after.revision,confirm_output:true});
    lastApplied=null;await refresh(false);renderInspector();updateUndo();notify('Previous lighting restored');
  });
  focusLightingControls();
}
function openPortDetails(){
  if(!selected.size)return;
  livePortDetails?.close();
  livePortDetails=openLivePortDetails({ports:[...selected].sort((a,b)=>a-b),getState:()=>state,effects,
    history:portHistory,request:workspaceRequest(),onRefresh:()=>refresh(false),onError:notify,isDisconnected:()=>disconnected});
}
function openEffectSettings(){
  return openSavedLibrary({kind:'effect',request:workspaceRequest(),getState:()=>state,
    getValue:()=>{const config=currentEffect();if(!config)throw new Error('Choose an effect first.');return {id:config.id,params:clone(config.params||{})};},
    onUse:value=>{if(!selected.size)throw new Error('Select ports for these settings.');stageEffect({...value,ports:effectSelection(),clearOverrides:false});editor='effect';renderInspector();return true;}});
}

function saveUiState(){clearTimeout(saveUiTimer);const key=workspaceStorageKey(session);if(!key||!workspaceReady)return;try{sessionStorage.setItem(key,JSON.stringify({page,editor,selected:[...selected],draft,draftBaseline,draftBaseScene,recovery:pendingPlan?{before:lastApplied?.before,after:lastApplied?.after,mode:lastApplied?.mode}:null,galleryState,effects:[...rememberedEffects]}));}catch{}}

async function applyProfile(reference){
  if(!await confirmDraft())return false;
  await serialize(async()=>{
    if(disconnected||!state.connected)throw new Error('Reconnect the switch before applying a profile.');
    const send=workspaceRequest(),workspace=session.workspace_id;
    const [saved,before]=await Promise.all([send('/api/profiles'),send('/api/scene')]);
    const record=saved.profiles.find(item=>typeof reference==='string'?item.name===reference:item.id===reference.id);
    if(!record||reference.revision&&record.revision!==reference.revision)throw new Error('This profile changed. Refresh profiles and choose it again.');
    const notice=profileLayoutNotice(record.scene,state);if(notice)throw new Error(notice);
    const history={before,mode:state.mode,workspace};
    try{history.after=await commitScene(before,record.scene,send,{preserveUnchanged:false});}
    catch(error){if(error.acceptedScene)history.after=error.acceptedScene;if(workspace===session.workspace_id)lastApplied=history;throw error;}
    if(workspace!==session.workspace_id)return;
    lastApplied=history;await refresh(false);if(page==='Lighting')renderInspector();updateUndo();notify('Profile applied');
  });
}
async function renderQuickProfiles(){
  const host=document.querySelector('#quick-profile');if(!host)return;
  host.innerHTML='<label class="sr-only" for="quick-profile-select">Profile</label><select id="quick-profile-select" disabled><option>Loading profiles…</option></select><button class="button quiet" disabled>Apply profile</button>';
  host.setAttribute('aria-busy','true');
  try{
    const records=(await request('/api/profiles')).profiles;if(!host.isConnected)return;
    profiles=Object.fromEntries(records.map(item=>[item.name,item.scene]));
    if(!records.length){host.querySelector('option').textContent='No saved profiles';return;}
    host.innerHTML='<label class="sr-only" for="quick-profile-select">Profile</label><select id="quick-profile-select"></select><button class="button quiet" id="apply-profile">Apply profile</button>';
    const select=host.querySelector('select'),button=host.querySelector('button');
    for(const item of records){const option=document.createElement('option');option.value=item.id;option.textContent=item.name;select.append(option);}
    const update=()=>{const item=records.find(record=>record.id===select.value),notice=profileLayoutNotice(item.scene,state);button.disabled=Boolean(notice);button.title=notice;};
    select.onchange=update;update();button.onclick=()=>run(()=>applyProfile(records.find(item=>item.id===select.value)));
  }catch(error){if(host.isConnected){host.querySelector('option').textContent='Open Profiles to retry';notify(error.message,true);}}
  finally{host.removeAttribute('aria-busy');}
}

function previewDraft(time){return compareApplied?{}:previewDraftColors(state,draft,time,{previewClocks,previewStates});}

function speedLegend() {
  const entries=[...LINK_SPEEDS,['offline','Disconnected'],['disabled','Disabled'],['unknown','Not reported']];
  const markup=items=>items.map(([key,name])=>`<span><i class="speed-dot speed-${key}" aria-hidden="true"></i>${name}</span>`).join('');
  return markup(entries);
}
function speedMarkup(link) {
  const label=link.key==='offline'?'<span class="link-long">Disconnected</span><span class="link-short">No link</span>':`<span>${escape(link.label)}</span>`;
  return `<i class="speed-dot speed-${link.key}" aria-hidden="true"></i>${label}`;
}
function poeTier(poe) {
  const pdClass=poe?.pd_class;
  const plus=Number.isInteger(pdClass)&&pdClass>=0&&pdClass<=8?(pdClass>=7?3:pdClass>=5?2:pdClass===4?1:0):0;
  return {plus,name:'PoE'+'+'.repeat(plus)};
}
function poeIcon(poe) {
  const tier=poeTier(poe);
  const plus=Array.from({length:tier.plus},(_,i)=>`<path d="M20 ${3+i*6}v4m-2-2h4" fill="none" stroke="currentColor" stroke-width="1.5"/>`).join('');
  return `<span class="poe-mark" role="img" aria-label="${tier.name}" title="${tier.name}"><svg class="poe-icon" viewBox="0 0 26 24" aria-hidden="true"><path d="M12.5 2 4 13h6l-1 9 8-12h-6l1.5-8Z" fill="currentColor"/>${plus}</svg></span>`;
}
function uplinkIcon(){return '<svg class="uplink-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m3 15 9-9 9 9"/></svg>';}
function portAccessibleName(port,sfp=false){
  const name=portLabel(port,sfp).text,id=`${sfp?'SFP':'Port'} ${port.port}`,power=portPower(port);
  return [id,name===id?'':name,linkStatus(port).label,!sfp&&power.delivering?`${poeTier(port.poe).name}, ${power.description}`:''].filter(Boolean).join(', ');
}
function portMarkup(port, sfp = false) {
  const id = number(port.port); const link = linkStatus(port);
  const power=portPower(port),powered=power.delivering===true,uplink=isUplink(port),name=portLabel(port,sfp);
  return `<button class="port-tile ${selected.has(id)?'selected':''} ${sfp?'sfp':''}" data-port="${id}" style="--port-color:rgb(${portDisplayRgb(port,state.output?.gamma).join(' ')});--port-ink:${portInk(portDisplayRgb(port,state.output?.gamma))}" aria-label="${escape(portAccessibleName(port,sfp))}" aria-pressed="${selected.has(id)}"><span class="port-heading"><span class="port-number">${id}</span><span class="uplink-badge" title="${uplink?'Uplink':powered?poeTier(port.poe).name:''}">${uplink?uplinkIcon():powered?poeIcon(port.poe):''}</span></span><span class="port-led"><span class="port-socket-rim" aria-hidden="true"></span><span class="port-socket-well" aria-hidden="true"></span>${sfp?'<span class="sfp-cage" aria-hidden="true"><i class="sfp-rail left"></i><i class="sfp-rail right"></i><i class="sfp-contacts"></i><i class="sfp-latch"></i></span>':`<span class="rj45-cage" aria-hidden="true"></span><span class="port-socket-contacts" aria-hidden="true">${'<i></i>'.repeat(8)}</span>`}<span class="port-socket-center" aria-hidden="true"></span></span><span class="port-label ${uplink?'uplink-label':''}" title="${escape(name.text)}">${name.lines.map(line=>`<span>${escape(line)}</span>`).join('')}</span><span class="port-speed" title="${escape(link.label)}">${speedMarkup(link)}</span><span class="port-power" title="${escape(power.description)}">${power.watts||'&nbsp;'}</span></button>`;
}
function renderPorts() {
  const copper = document.querySelector('#copper-ports'); if (!copper) return;
  const ports = state.ports || [];
  copper.style.setProperty('--port-count',Math.max(1,ports.length));
  copper.classList.toggle('dense-bank',ports.length>16);
  copper.closest('.port-group').hidden=!ports.length;
  document.querySelector('#copper-range').textContent=ports.length?`Ports ${ports[0].port}–${ports.at(-1).port}`:'';
  copper.innerHTML = ports.map(port => portMarkup(port)).join('');
  const sfp=document.querySelector('#sfp-ports'),lights=state.sfp||[];
  const map=copper.closest('.port-map');
  const inlineBanks=ports.length>0 && lights.length>0 && ports.length<=8 && lights.length<=2;
  map.classList.toggle('inline-banks',inlineBanks);
  if(inlineBanks)map.style.setProperty('--bank-widths',`minmax(0, ${ports.length}fr) minmax(0, ${lights.length}fr)`);
  sfp.style.setProperty('--port-count',Math.max(1,lights.length));
  sfp.classList.toggle('dense-bank',lights.length>16);
  sfp.classList.toggle('wide-bank',lights.length>4||!ports.length);
  document.querySelector('.sfp-group').classList.toggle('optical-only',!ports.length);
  document.querySelector('#sfp-range').textContent=lights.length?`Ports ${lights[0].port}–${lights.at(-1).port}`:'';
  sfp.innerHTML = lights.map(port => portMarkup(port, true)).join('');sfp.hidden=!lights.length;
  document.querySelector('.sfp-group').hidden=!lights.length;
  portGlow?.dispose();portGlow=createPortGlow(document.querySelector('.port-map'));
  telemetryPaintAt=0;
  document.querySelectorAll('[data-port]').forEach(button => button.addEventListener('click', async event => {
    const port = Number(button.dataset.port);
    const next=selectPort({ports:allPorts(),selected,anchor:selectionAnchor,port,additive:multipleSelection||event.ctrlKey||event.metaKey,range:event.shiftKey});
    if(await setSelection(next.ports))selectionAnchor=next.anchor;
  }));
}
function updateLive() {
  portHistory.update(state,disconnected);
  livePortDetails?.update();
  const legend=document.querySelector('.current-speed-legend');
  if(legend){const keys=new Set([...(state.ports||[]),...(state.sfp||[])].map(port=>linkStatus(port).key));const entries=[...LINK_SPEEDS,['offline','Disconnected'],['disabled','Disabled'],['unknown','Not reported']].filter(([key])=>keys.has(key));const signature=entries.map(([key])=>key).join(',');if(legend.dataset.keys!==signature){legend.innerHTML=entries.map(([key,name])=>`<span><i class="speed-dot speed-${key}" aria-hidden="true"></i>${name}</span>`).join('');legend.dataset.keys=signature;}}
  uplinkBandwidth?.update(state,disconnected);
  if(document.hidden)return;
  if(page==='Effects'){secondaryCleanup?.update?.({state,selectedPorts:[...selected],effectDrafts:rememberedEffects,draft});return;}
  if (page !== 'Lighting') return;
  const date=hostScheduleDate(state);
  portAnimator.pushFrame(Object.fromEntries([...(state.ports||[]),...(state.sfp||[])].map(port=>[port.port,portDisplayRgb(port,state.output?.gamma)])));
  const now=performance.now();
  if(now-telemetryPaintAt>=250){
    telemetryPaintAt=now;
    const heading=document.querySelector('#switch-title'),title=switchTitle(state);
    if(heading&&heading.textContent!==title)heading.textContent=title;
    for (const port of [...(state.ports || []), ...(state.sfp || [])]) {
      const tile=document.querySelector(`[data-port="${port.port}"]`);if(!tile)continue;
      const staged=draft.sfp?.[port.port],sync=sfpSync(state,draft),linked=isSfpPort(port.port)&&sync.mode!=='independent';
      const effective=linked?effectiveSfpLight(state,port.port,draft):{...port,...staged};
      const grouped=isSfpPort(port.port)&&Boolean(portEffect(state,compareApplied?{}:draft,port.port));
      const unknown=grouped?false:linked?sync.mode==='together'&&effective.mode==='unmanaged'&&!matchingSfpRule(state.rules||[],effective,date):staged?(staged.mode??port.mode)==='unmanaged'&&!matchingSfpRule(state.rules||[],{...port,...staged},date):port.display_rgb===null&&port.mode!=='off';
      tile.querySelector('.port-led')?.classList.toggle('is-unknown',unknown);
      tile.querySelector('.port-led')?.setAttribute('title',unknown?'Link-managed color has not been reported':'');
      const link=linkStatus(port),sfp=isSfpPort(port.port);
      const power=portPower(port),powered=power.delivering===true,uplink=isUplink(port),watts=power.watts||'\u00a0';
      const speedKey=`${link.key}:${link.label}`,badgeKey=`${uplink}:${powered}:${poeTier(port.poe).name}`;
      const name=portLabel(port,sfp),deviceName=JSON.stringify(name.lines),aria=portAccessibleName(port,sfp);
      const signature=JSON.stringify([watts,power.description,speedKey,link.label,badgeKey,deviceName]);
      let cached=telemetryNodes.get(tile);
      if(cached?.signature===signature)continue;
      cached??={power:tile.querySelector('.port-power'),speed:tile.querySelector('.port-speed'),badge:tile.querySelector('.uplink-badge')};
      if(cached.watts!==watts)cached.power.textContent=watts;
      if(cached.powerDescription!==power.description)cached.power.title=power.description;
      if(cached.speedKey!==speedKey)cached.speed.innerHTML=speedMarkup(link);
      if(cached.linkLabel!==link.label)cached.speed.title=link.label;
      if(cached.badgeKey!==badgeKey&&cached.badge){cached.badge.innerHTML=uplink?uplinkIcon():powered?poeIcon(port.poe):'';cached.badge.title=uplink?'Uplink':powered?poeTier(port.poe).name:'';}
      if(cached.aria!==aria)tile.setAttribute('aria-label',aria);
      tile.querySelector('.port-label')?.classList.toggle('uplink-label',uplink);
      if(cached.deviceName!==deviceName){
        const field=tile.querySelector('.port-label');
        field.title=name.text;
        field.replaceChildren(...name.lines.map(line=>{const item=document.createElement('span');item.textContent=line;return item;}));
      }
      Object.assign(cached,{signature,watts,powerDescription:power.description,speedKey,linkLabel:link.label,badgeKey,deviceName,aria});telemetryNodes.set(tile,cached);
    }
  }
  const master = document.querySelector('#master');
  const mapStatus=document.querySelector('#map-output-status');
  if(mapStatus){const text=disconnected||!state.connected?'Last confirmed colors · Connection interrupted':state.mode==='released'?'UniFi controls the lighting':edits.size&&!compareApplied?'Previewing unapplied lighting':'Select ports to preview and apply lighting.';mapStatus.hidden=!disconnected&&state.connected&&state.mode!=='released'&&(!edits.size||compareApplied);if(mapStatus.textContent!==text)mapStatus.textContent=text;}
  updateControlSource();
  if (master && document.activeElement !== master && !edits.has('master') && !busy) {
    const value=String(number(state.brightness,100));if(master.value!==value)master.value=value;
    const output=document.querySelector('#master-value');if(output.textContent!==`${value}%`)output.textContent=`${value}%`;
  }
  const toggle=document.querySelector('#toggle-output');if(toggle){const text=draft.mode!==undefined?'Cancel control preview':state.mode==='running'?'Preview return to UniFi':'Preview EE lighting';if(toggle.textContent!==text){toggle.textContent=text;toggle.setAttribute('aria-label',text);}const preview=document.querySelector('#ownership-preview');preview.hidden=draft.mode===undefined;preview.textContent=draft.mode===undefined?'':`After Apply: ${draft.mode==='running'?'EE controls lighting':'UniFi controls lighting'}`;}
  updateRangeProgress();

}
function updateGroups() {
  const select = document.querySelector('#group-select'); if (!select) return;
  const options=portScopeOptions(state),signature=JSON.stringify(options);
  if (select.dataset.signature !== signature) {
    select.dataset.signature = signature;
    select.innerHTML = options.map(option=>`<option value="${escape(option.value)}">${escape(option.label)}</option>`).join('')+'<option value="__selection__" disabled>Selected ports</option>';
  }
  select.value=options.find(option=>option.ports.length===selected.size&&option.ports.every(port=>selected.has(port)))?.value||'__selection__';
  document.querySelector('#selected-label').textContent=selectionName();
  const scopes={'#select-all':allPorts(),'[data-select="ethernet"]':(state.ports||[]).map(p=>p.port),'[data-select="sfp"]':(state.sfp||[]).map(p=>p.port),'#select-none':[]};
  for(const [selector,ids] of Object.entries(scopes))document.querySelector(selector)?.setAttribute('aria-pressed',String(ids.length===selected.size&&ids.every(id=>selected.has(id))));
  document.querySelector('[data-select="sfp"]').disabled=!state.sfp?.length;
  document.querySelector('[data-select="ethernet"]').disabled=!state.ports?.length;
  const details=document.querySelector('#port-details');if(details)details.disabled=!selected.size;
}
async function setSelection(ports, mode) {
  if(ports.some(port=>!allPorts().includes(port)))throw new Error('Choose ports on the connected switch.');
  if(pendingPlan&&!await confirmDraft()){updateGroups();return false;}
  const invalid=view.querySelector('input:invalid');if(invalid){invalid.focus();notify('Correct the highlighted value before selecting other ports.',true);updateGroups();return false;}
  activeColorPopover?.();selected = new Set(ports); editor = mode||([...selected].length&&[...selected].every(id=>draft.colors?.[id]||(!draft.returnToEffect?.includes(id)&&!effectDraftForPort(draft,id)?.clearOverrides&&controlSource(state,id,effects).kind==='color'))?'color':'effect'); inspectorSignature='';saveUiState();
  document.querySelectorAll('[data-port]').forEach(tile => { const active=selected.has(Number(tile.dataset.port)); tile.classList.toggle('selected',active); tile.setAttribute('aria-pressed', String(active)); });
  updateGroups();  renderInspector();
  return true;
}

function controlSection(title, content, symbol = '', className = '') {
  return `<section class="control-section ${className}">${className.includes('primary-appearance')?'':`<h3 class="control-section-heading">${symbol?icon(symbol):''}<span>${escape(title)}</span></h3>`}<div class="parameter-grid control-section-body">${content}</div></section>`;
}
function colorControl(key, caption, value, brightness=100, randomKey, random=false, relative=false) {
  return `<div class="parameter color-parameter" ${relative?'data-relative-level="true"':''}><label>${escape(caption)}</label><div class="color-control"><button class="swatch-button color-swatch" type="button" data-color="${escape(key)}" value="${hex(value).toLowerCase()}" style="--color:${hex(value)}" aria-label="${escape(caption)}" aria-haspopup="dialog"></button><input class="hex-input" data-hex="${escape(key)}" value="${hex(value).toUpperCase()}" aria-label="${escape(caption)} hex color" maxlength="7" spellcheck="false"></div><div class="parameter-meta"><label for="brightness-${escape(key)}">${relative?'Color level':'Brightness'}</label><output>${number(brightness,100)}%</output></div><input id="brightness-${escape(key)}" data-brightness="${escape(key)}" type="range" min="0" max="100" step="1" value="${number(brightness,100)}" aria-label="${escape(caption)} ${relative?'color level':'brightness'}"><span class="help-text configured-level" ${relative&&number(draft.brightness??state.brightness,100)<100?'':'hidden'}>Configured peak: ${Math.round(number(brightness,100)*number(draft.brightness??state.brightness,100)/100)}%</span>${randomKey?`<label class="check-row"><input type="checkbox" data-random="${escape(randomKey)}" ${random?'checked':''}><span>Randomize</span></label>`:''}</div>`;
}
function paramControl(spec, value) {
  const attr=`data-param="${escape(spec.key)}"`;
  const helpId=`param-help-${escape(spec.key)}`,description=spec.description?`<p class="help-text parameter-help" id="${helpId}">${escape(spec.description)}</p>`:'',help=description?` aria-describedby="${helpId}"`:'';
  if (spec.type==='bool') return `<div class="parameter"><label class="check-row"><input ${attr}${help} type="checkbox" ${value?'checked':''}><span>${escape(spec.label)}</span></label>${description}</div>`;
  if (spec.type==='choice') return `<div class="parameter parameter-inline"><label for="param-${escape(spec.key)}">${escape(spec.label)}</label><select id="param-${escape(spec.key)}" ${attr}${help}>${spec.choices.map(choice=>`<option value="${escape(choice)}" ${choice===value?'selected':''}>${escape(label(choice))}</option>`).join('')}</select>${description}</div>`;
  return `<div class="parameter"><div class="parameter-meta"><label for="param-${escape(spec.key)}">${escape(spec.label)}</label><output for="param-${escape(spec.key)}">${escape(paramValue(spec,value))}</output></div><input id="param-${escape(spec.key)}" ${attr}${help} type="range" min="${number(spec.min)}" max="${number(spec.max,100)}" step="${number(spec.step,spec.type==='int'?1:.1)}" value="${number(value)}" aria-label="${escape(spec.label)}" aria-valuetext="${escape(paramValue(spec,value))}">${description}</div>`;
}
function paramValue(spec,value){
  if(spec.key==='floor')return `${Math.round(value*100)}%`;
  if(spec.key==='pace')return ({1:'Slow · 3.08 s',2:'Medium · 2 s',3:'Fast · 1 s'})[value]||value;
  if(spec.unit==='%')return `${value}%`;
  if(spec.unit==='cycles/s')return `${value} cycles / second`;
  return `${value}${spec.unit?' '+spec.unit:''}`;
}

function preserveInspectorFocus(render,selector){
  const active=document.activeElement, key=selector||(active?.id?`#${CSS.escape(active.id)}`:['param','random','color','hex','editor'].map(name=>active?.dataset?.[name]?`[data-${name}="${CSS.escape(active.dataset[name])}"]`:null).find(Boolean));
  const open=document.querySelector('#inspector .more-settings')?.open;
  render();const details=document.querySelector('#inspector .more-settings');if(details&&open)details.open=true;
  if(key)document.querySelector('#inspector')?.querySelector(key)?.focus({preventScroll:true});
}
function sourceSummary(){
  const sources=[...selected].map(id=>controlSource(state,id,effects)),rules=sources.filter(source=>source.kind==='rule');
  if(rules.length)return `${rules.length===sources.length?'Controlled by automation':`${rules.length} of ${sources.length} ports controlled by automation`}: ${[...new Set(rules.map(source=>source.label))].join(', ')}`;
  const labels=[...new Set(sources.map(source=>source.label))];
  return labels.length===1?`${state.mode==='released'?'Saved':'Applied'}: ${labels[0]}`:'Selected ports use different lighting';
}
function updateControlSource(){
  const host=document.querySelector('.control-source');if(!host)return;
  const rules=[...selected].map(id=>controlSource(state,id,effects)).filter(source=>source.kind==='rule');
  const text=sourceSummary(),signature=JSON.stringify([text,rules.map(source=>source.rule?.id)]);
  if(host.dataset.signature===signature)return;host.dataset.signature=signature;host.replaceChildren();
  const caption=document.createElement('span');caption.textContent=text;host.append(caption);
  if(rules.length){const button=document.createElement('button');button.className='button quiet';button.type='button';button.dataset.openRule=rules[0].rule?.id||'';button.textContent='Edit automation';button.onclick=()=>{requestedRuleId=button.dataset.openRule;run(()=>navigate('Automation'));};host.append(button);}
}
function previewScene(){
  if(!edits.size||pendingPlan)return null;
  return lightingSnapshot({zones:state.zones,rules:state.rules,overrides:state.overrides||{},brightness:state.brightness,sfp:Object.fromEntries((state.sfp||[]).map(light=>[light.port,sfpSettings(light)])),sfp_sync:state.sfp_sync},state,draft);
}
async function savePreviewProfile(name){
  if(pendingPlan||draft.mode!==undefined)throw new Error('Apply or discard the lighting control change before saving this preview.');
  return serialize(async()=>{
    const send=workspaceRequest(),before=await send('/api/scene');
    if(draftBaseline&&sceneFingerprint(before.scene)!==draftBaseline)throw new Error('The lighting setup changed while you were editing. Review the current setup before saving this preview.');
    const scene=lightingSnapshot(before.scene,state,draft);
    return send('/api/presets','POST',{name,scene,expected_revision:before.revision,overwrite:false});
  });
}
function copperInspectorKey(){
  const colors=editor==='color'?[...selected].map(id=>{const settings=fixedColorSettings(state,id,draft);return {...settings,effectiveBrightness:fixedColorBrightness(settings,state,draft)};}):null;
  return JSON.stringify([editor,[...selected],selectionEffects(),colors]);
}
function sfpColorFromAppearance(settings){
  const master=number(draft.brightness??state.brightness,100),brightness=settings.brightness??master;
  if(brightness>master)throw new Error(`Raise Default brightness to at least ${brightness}% before pasting this appearance onto SFP lights.`);
  return {mode:'color',effect:'steady',color:settings.color,brightness:100,
    color_brightness:settings.brightness==null?100:master===0?0:Math.round(brightness/master*100)};
}
function toggleInspector(collapsed){
  activeColorPopover?.();
  inspectorCollapsed=collapsed;
  view.querySelector('.workspace').dataset.inspectorCollapsed=String(collapsed);
  document.querySelector(collapsed?'#expand-inspector':'#collapse-inspector')?.focus({preventScroll:true});
  try{setInspectorCollapsed(collapsed);}catch(error){notify(error.message,true);}
}
function renderInspector() {
  const host=document.querySelector('#inspector');if(!host)return;
  const scope=[...selected].sort((a,b)=>a-b).join(','),same=host.dataset.scope===scope,scroll=same?host.scrollTop:0,open=same&&host.querySelector('.more-settings')?.open;
  renderInspectorContent();host.dataset.scope=scope;
  const header=host.querySelector('.inspector-header');
  if(header){const back=document.createElement('button');back.type='button';back.className='button quiet mobile-only';back.id='back-to-ports';back.textContent='Back to ports';back.onclick=()=>{const studio=document.querySelector('.studio-content');studio?.classList.remove('is-editing-ports');if(studio)studio.scrollTop=number(studio.dataset.canvasScroll);document.querySelector('#open-port-controls')?.focus({preventScroll:true});};header.prepend(back);
    const collapse=document.createElement('button');
    collapse.id='collapse-inspector';collapse.type='button';collapse.className='button quiet icon-button';
    collapse.title='Hide port controls';collapse.setAttribute('aria-label',collapse.title);
    collapse.setAttribute('aria-controls','inspector');collapse.setAttribute('aria-expanded','true');
    collapse.innerHTML=icon('panel');collapse.onclick=()=>toggleInspector(true);header.append(collapse);
  }
  const details=host.querySelector('.more-settings');if(details&&open)details.open=true;
  updateControlSource();host.scrollTop=scroll;
  enterInspector(host.querySelector('.inspector-sections')||host.querySelector('.sfp-controls'));
}
function renderInspectorContent() {
  activeColorPopover?.();const host=document.querySelector('#inspector');if(!host)return;
  if(!selected.size){host.innerHTML='<header class="inspector-header"><div><h2>Port controls</h2><p class="muted">Select a port on the map, or choose All ports.</p></div></header>';return;}
  if(useSfpInspector()){renderSfp(host);return;}
  const both=Boolean(sfpSelection().length),current=currentEffect(),mixed=hasMixedEffects(),spec=effectSpec(current?.id),values={...defaultConfig(current?.id||'static').params,...current?.params};
  const opticalOnly=both&&!copperSelection().length;
  if(opticalOnly)editor='effect';
  inspectorSignature=copperInspectorKey();
  host.innerHTML=`<header class="inspector-header"><div><h2>Port controls</h2><p class="muted">${escape(selectionName())}</p></div></header><div class="control-source"></div><div class="inspector-mode-row"><div class="editor-tabs" role="group" aria-label="Edit mode"><button class="button ${editor==='color'?'selected':''}" data-editor="color" aria-pressed="${editor==='color'}">Fixed color</button><button class="button ${editor==='effect'?'selected':''}" data-editor="effect" aria-pressed="${editor==='effect'}">Effect</button></div><div class="effect-choice-slot"></div></div><div class="inspector-sections" id="parameter-grid"></div><div class="inspector-actions"><button class="button quiet" id="copy-appearance" ${selected.size!==1?'disabled':''}>Copy appearance</button><button class="button quiet" id="paste-appearance" ${!copiedAppearance?'disabled':''}>Paste appearance</button></div>`;
  host.querySelectorAll('[data-editor]').forEach(button=>button.onclick=()=>{editor=button.dataset.editor;preserveInspectorFocus(renderInspector,`[data-editor="${editor}"]`);});
  if(opticalOnly)host.querySelector('.editor-tabs').hidden=true;
  const groupedOptical=groupedSfpSelection();
  if(groupedOptical.length){
    const restore=document.createElement('button');restore.type='button';restore.className='button quiet';restore.textContent='Return to SFP lighting';
    restore.onclick=()=>{draft=leaveSfpEffect(draft,groupedOptical);queueEdit('sfp');renderInspector();};
    host.querySelector('.inspector-actions').prepend(restore);
  }
  const copyButton=host.querySelector('#copy-appearance');
  if(selected.size===1&&!currentEffect()&&!draft.colors?.[[...selected][0]]&&!state.overrides?.[[...selected][0]]){copyButton.disabled=true;copyButton.title='Choose a color or effect for this port first.';}
  copyButton.onclick=()=>{const id=[...selected][0],p=[...state.ports,...state.sfp].find(p=>p.port===id),manual=draft.colors?.[id]||(!draft.returnToEffect?.includes(id)&&!effectDraftForPort(draft,id)?.clearOverrides?state.overrides?.[id]:null);copiedAppearance=isSfpPort(id)&&!portEffect(state,draft,id)?{type:'sfp',settings:sfpSettings({...p,...draft.sfp?.[id]})}:manual?{type:'color',settings:clone(manual)}:{type:'effect',settings:clone(currentEffect())};host.querySelector('#paste-appearance').disabled=false;notify('Appearance copied');};
  host.querySelector('#paste-appearance').onclick=()=>{
    if(copiedAppearance.type==='effect'){
      stageEffect({...clone(copiedAppearance.settings),ports:effectSelection(),clearOverrides:true});editor='effect';
    }else if(copiedAppearance.type==='sfp'){
      if(!sfpSelection().length){notify('Select SFP ports for these settings.');return;}
      if(sfpSync(state,draft).mode==='ethernet'){notify('Select Independent or Together in SFP controls before pasting this appearance.');return;}
      draft=leaveSfpEffect(draft,groupedOptical);
      draft.sfp??={};for(const id of sfpEditTargets(state,sfpSelection(),draft))draft.sfp[id]=normalizeSfpBrightness(clone(copiedAppearance.settings));queueEdit('sfp');
    }else{
      const sfpTargets=sfpSync(state,draft).mode==='ethernet'?[]:sfpEditTargets(state,sfpSelection(),draft);
      let sfpColor;try{if(sfpTargets.length)sfpColor=sfpColorFromAppearance(copiedAppearance.settings);}catch(error){notify(error.message,true);return;}
      draft.colors??={};for(const id of copperSelection())draft.colors[id]=clone(copiedAppearance.settings);
      if(sfpTargets.length)draft=leaveSfpEffect(draft,groupedOptical);
      draft.sfp??={};for(const id of sfpTargets)draft.sfp[id]=clone(sfpColor);
      editor='color';queueEdit('colors');
    }
    renderInspector();
  };
  const grid=host.querySelector('#parameter-grid');
  if(editor==='color'){
    const settings=[...selected].map(id=>fixedColorSettings(state,id,draft));
    const sameColor=equalSetting(settings.map(s=>s.color)),sameBrightness=equalSetting(settings.map(s=>fixedColorBrightness(s,state,draft)));
    const followingSfp=both&&sfpSync(state,draft).mode==='ethernet';
    grid.innerHTML=controlSection('Appearance',colorControl('manual','Color',settings[0].color,fixedColorBrightness(settings[0],state,draft))+`<p class="help-text">Apply keeps these ports at their previewed color and brightness.${followingSfp?' SFP lights continue following their selected Ethernet port.':both&&(draft.brightness??state.brightness)<100?' SFP colors also follow Default brightness.':''}</p><button class="button quiet" id="restore-effect" ${!canFollowGroup()?'disabled':''}>Resume group effect</button>`,'blend','primary-appearance');
    if(!sameColor){const field=grid.querySelector('[data-hex]');field.value='';field.placeholder='#409EFF';field.dataset.allowEmpty='true';setMixedColor(grid.querySelector('[data-color]'),true);}
    if(!sameBrightness)grid.querySelector('output').textContent='Mixed';
    bindColors(grid,(_key,value,brightness,changed)=>{
      draft.colors??={};draft.sfp??={};
      if(changed==='resetColor'){
        for(const id of copperSelection()){const original=fixedColorSettings(state,id);if(draft.colors[id]){draft.colors[id].color=original.color;if(equalSetting([draft.colors[id],original]))delete draft.colors[id];}}
        for(const id of followingSfp?[]:sfpEditTargets(state,sfpSelection(),draft)){if(draft.sfp[id])draft.sfp[id].color=fixedColorSettings(state,id,{sfp_sync:draft.sfp_sync}).color;}
        queueEdit('colors');return;
      }
      for(const id of copperSelection()){const original=draft.colors[id]??state.overrides?.[id]??settings[[...selected].indexOf(id)];draft.colors[id]={...original,[changed]:changed==='color'?value:brightness};}
      if(!followingSfp&&groupedOptical.length)draft=leaveSfpEffect(draft,groupedOptical);
      for(const id of followingSfp?[]:sfpEditTargets(state,sfpSelection(),draft))draft.sfp[id]={...sfpSettings(editableSfpLight(state,id,draft)),mode:'color',effect:'steady',[changed==='brightness'?'color_brightness':changed]:changed==='color'?value:brightness};
      queueEdit('colors');
    });
    grid.querySelector('#restore-effect').onclick=()=>{draft.returnToEffect=[...new Set([...(draft.returnToEffect||[]),...copperSelection()])];for(const id of copperSelection())if(draft.colors)delete draft.colors[id];queueEdit('return-effect');editor='effect';renderInspector();};
    updateDraftBar();return;
  }
  const savedSettings=document.createElement('button');savedSettings.className='button quiet';savedSettings.textContent='Saved effect settings';savedSettings.onclick=()=>run(openEffectSettings);host.querySelector('.inspector-actions').append(savedSettings);
  const description=EFFECTS.find(effect=>effect.id===spec?.id)?.description||spec?.description;
  host.querySelector('.effect-choice-slot').innerHTML=`<div class="effect-summary effect-choice"><button class="button" id="browse-effects" aria-label="Choose effect: ${mixed?'Multiple effects':escape(spec?.name||'None')}" title="${escape(description||'Browse lighting effects')}">${mixed?'Multiple effects':escape(spec?.name||'Choose effect')} ${icon('chevron')}</button></div>`;
  host.querySelector('#browse-effects').onclick=()=>{galleryState.type='ethernet';run(()=>navigate('Effects'));};
  if(!spec)return;
  const ports=effectSelection(),source={id:current.id,params:clone(values)};
  const editParams=change=>{for(const [left,right] of spec.exclusive_params||[]){if(change[left]===true)change[right]=false;if(change[right]===true)change[left]=false;}Object.assign(source.params,change);stageEffect({...clone(source),ports});};
  const visible=spec.params.filter(p=>!p.randomizes&&!p.dims).filter(p=>{
    if(spec.id==='traffic'&&p.key==='uplink_color')return values.custom_uplink===true;
    if(p.key==='color_transition')return spec.params.some(color=>color.random_param&&values[color.random_param]);
    if(spec.id==='breathing'){if(p.key==='pace')return values.playback==='synchronized';if(['period','floor','ripple'].includes(p.key))return values.playback!=='synchronized';}
    return true;
  });
  const controls=visible.map(p=>({param:p,html:p.type==='color'?colorControl(p.key,p.label,values[p.key]??p.default,values[p.brightness_param]??100,values.playback==='synchronized'?null:p.random_param,values[p.random_param]??false,true):paramControl(p,values[p.key]??p.default)}));
  const uplink=spec.id==='traffic'?controls.filter(c=>['custom_uplink','uplink_color'].includes(c.param.key)):[];
  const colors=controls.filter(c=>c.param.type==='color'&&!uplink.includes(c));
  const motion=controls.filter(c=>['speed','rate','period','pace','playback','color_transition'].includes(c.param.key)||spec.id==='daylight');
  const more=controls.filter(c=>!colors.includes(c)&&!motion.includes(c)&&!uplink.includes(c));
  grid.insertAdjacentHTML('beforeend',(colors.length?controlSection('Appearance',colors.map(c=>c.html).join(''),'blend','primary-appearance'):'')+(uplink.length?controlSection('Uplink',uplink.map(c=>c.html).join(''),'arrow'):'')+(motion.length?controlSection('Motion',motion.map(c=>c.html).join(''),'clock'):'')+(more.length?`<details class="more-settings control-section"><summary>More settings</summary><div class="parameter-grid control-section-body">${more.map(c=>c.html).join('')}</div></details>`:''));
  bindColors(grid,(key,value,brightness,changed)=>{const p=spec.params.find(p=>p.key===key);editParams(changed==='brightness'&&p.brightness_param?{[p.brightness_param]:brightness}:{[key]:value});});
  grid.querySelectorAll('[data-random]').forEach(input=>input.onchange=()=>{editParams({[input.dataset.random]:input.checked});preserveInspectorFocus(renderInspector,`[data-random="${input.dataset.random}"]`);});
  grid.querySelectorAll('[data-param]').forEach(input=>{const p=spec.params.find(p=>p.key===input.dataset.param);input.addEventListener(input.type==='range'?'input':'change',()=>{const value=p.type==='bool'?input.checked:p.type==='choice'?input.value:Number(input.value);const output=input.closest('.parameter').querySelector('output');if(output)output.textContent=paramValue(p,value);editParams({[p.key]:value});if(p.key==='playback'||p.key==='custom_uplink'||spec.exclusive_params?.some(pair=>pair.includes(p.key)))preserveInspectorFocus(renderInspector,`[data-param="${p.key}"]`);});});
  updateRangeProgress();updateDraftBar();
}
function setMixedColor(input,mixed){
  input.dataset.colorLabel??=input.getAttribute('aria-label');input.classList.toggle('is-mixed',mixed);
  input.setAttribute('aria-label',input.dataset.colorLabel+(mixed?', mixed colors. Choose a color.':''));
  if(mixed)input.value='#808080';
}
function bindColors(host,onEdit) {
  host.querySelectorAll('[data-color]').forEach(input=>{
    const key=input.dataset.color,field=host.querySelector(`[data-hex="${CSS.escape(key)}"]`),brightness=host.querySelector(`[data-brightness="${CSS.escape(key)}"]`);
    const error=document.createElement('p');error.className='inline-error color-error';error.id=`hex-error-${key}`;error.setAttribute('role','alert');field.setAttribute('aria-describedby',error.id);field.closest('.color-control').after(error);
    const apply=changed=>onEdit(key,input.value,Number(brightness.value),changed);
    const validate=valid=>{const message=valid?'':'Enter a six-digit hex color.';field.setCustomValidity(message);field.setAttribute('aria-invalid',String(!valid));error.textContent=message;};
    input.addEventListener('click',event=>openColorPopover(input,event.pointerType));
    input.addEventListener('input',()=>{input.style.setProperty('--color',input.value);setMixedColor(input,false);field.value=input.value.toUpperCase();validate(true);apply('color');});
    const emptyAllowed=()=>field.dataset.allowEmpty==='true'&&!field.value.trim();
    field.addEventListener('input',()=>{validate(emptyAllowed()||Boolean(normalizeHex(field.value)));updateDraftBar();});
    field.addEventListener('change',()=>{const normalized=normalizeHex(field.value);validate(emptyAllowed()||Boolean(normalized));if(emptyAllowed()){setMixedColor(input,true);apply('resetColor');return;}if(!normalized)return;setMixedColor(input,false);input.value=normalized;input.style.setProperty('--color',normalized);field.value=normalized.toUpperCase();apply('color');});
    brightness.addEventListener('input',()=>{const parameter=brightness.closest('.parameter');parameter.querySelector('output').textContent=brightness.value+'%';const configured=parameter.querySelector('.configured-level');if(configured)configured.textContent=`Configured peak: ${Math.round(Number(brightness.value)*number(draft.brightness??state.brightness,100)/100)}%`;apply('brightness');});
  });updateRangeProgress();
}
function sfpInspectorKey(){return JSON.stringify(['sfp',sfpSelection(),sfpSync(state,draft),...sfpSelection().map(id=>sfpSettings(effectiveSfpLight(state,id,draft)))]);}
function sfpSyncControls(sync) {
  if(!state.sfp_sync)return '';
  const modes=[['independent','Independent'],['together','Together']];
  if(state.ports?.length)modes.push(['ethernet','Follow Ethernet port']);
  const targets=(state.sfp||[]).filter(light=>!portEffect(state,draft,light.port));
  const group=targets.length===(state.sfp||[]).length?(targets.length===2?'both SFP lights':`all ${targets.length} SFP lights`):`SFP ${portRanges(targets.map(light=>light.port))}`;
  const mode=`<div class="parameter parameter-inline"><label for="sfp-sync">Mode</label><select id="sfp-sync">${modes.map(([value,text])=>`<option value="${value}" ${value===sync.mode?'selected':''}>${text}</option>`).join('')}</select></div>`;
  const source=sync.mode==='ethernet'?`<div class="parameter parameter-inline"><label for="sfp-source">Follow port</label><select id="sfp-source">${(state.ports||[]).map(port=>`<option value="${port.port}" ${port.port===sync.source_port?'selected':''}>Port ${port.port}${port.name?' · '+escape(port.name):''}</option>`).join('')}</select></div><p class="help-text">The SFP lights track this port’s color and brightness with smooth fades. SFP automations can override the shared lighting.</p>`:sync.mode==='together'?`<p class="help-text">Use these controls to set ${group}. Independent returns each indicator to its separate controls.</p>`:'';
  return controlSection('Synchronization',mode+source,'link','sfp-sync-controls');
}
function bindSfpSync(host){
  if(!state.sfp_sync)return;
  const sync=sfpSync(state,draft);
  host.querySelector('#sfp-sync').onchange=event=>{
    const mode=event.target.value;
    draft.sfp_sync={...sync,mode};
    if(mode!=='independent')selected=new Set((state.sfp||[]).map(port=>port.port).filter(id=>!portEffect(state,draft,id)));
    document.querySelectorAll('[data-port]').forEach(tile=>{const active=selected.has(Number(tile.dataset.port));tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));});
    queueEdit('sfp-sync');updateGroups();preserveInspectorFocus(renderInspector,'#sfp-sync');
  };
  const source=host.querySelector('#sfp-source');if(source)source.onchange=()=>{draft.sfp_sync={...sync,source_port:Number(source.value)};queueEdit('sfp-sync');};
}
function renderSfp(host){
  const sync=sfpSync(state,draft),ports=sfpSelection(),lights=ports.map(id=>editableSfpLight(state,id,draft)),current=lights[0];
  const targets=sync.mode==='ethernet'?(state.sfp||[]).map(port=>port.port).filter(id=>!portEffect(state,draft,id)):ports;
  const header=`<header class="inspector-header"><div><h2>Port controls</h2><p class="muted">${targets.length>4?`SFP ports ${portRanges(targets)}`:targets.map(id=>`SFP ${id}`).join(', ')}</p></div></header><div class="control-source"></div>`;
  const browse='<button class="button quiet" id="sfp-gallery">Browse port effects</button>';
  const openGallery=()=>{galleryState.type='ethernet';run(()=>navigate('Effects'));};
  if(sync.mode==='ethernet'){
    host.innerHTML=`${header}<div class="inspector-sections">${sfpSyncControls(sync)}${browse}</div>`;
    host.querySelector('#sfp-gallery').onclick=openGallery;bindSfpSync(host);inspectorSignature=sfpInspectorKey();updateDraftBar();return;
  }
  const same=key=>equalSetting(lights.map(light=>light[key])),mode=same('mode')?current.mode:'mixed',effect=same('effect')?current.effect:'mixed';
  const modeControl=`<div class="parameter parameter-inline"><label for="sfp-mode">Lighting</label><select id="sfp-mode">${mode==='mixed'?'<option value="mixed" disabled selected>Mixed</option>':''}${[['unmanaged','Link status'],['color','Custom lighting'],['off','Off']].map(([id,name])=>`<option value="${id}" ${id===mode?'selected':''}>${name}</option>`).join('')}</select></div>`;
  let appearance=modeControl, motion='';
  if(mode==='color'){
    appearance+=`<div class="parameter parameter-inline"><label for="sfp-effect">Effect</label><select id="sfp-effect">${effect==='mixed'?'<option value="mixed" disabled selected>Mixed</option>':''}${[['steady','Steady'],['pulse','Gentle pulse'],['two_color','Two-color fade'],['color_cycle','Color cycle']].map(([id,name])=>`<option value="${id}" ${id===effect?'selected':''}>${name}</option>`).join('')}</select></div>`;
    if(effect!=='color_cycle')appearance+=colorControl('color','Primary color',current.color,current.color_brightness??100,null,false,true);
    if(effect==='two_color')appearance+=colorControl('secondary_color','Secondary color',current.secondary_color,current.secondary_brightness??100,null,false,true);
    if(effect==='color_cycle')appearance+=paramControl({key:'color_brightness',label:'Color level',type:'int',min:0,max:100,step:1,unit:'%'},current.color_brightness??100,null,false,true);
    if(!['steady','mixed'].includes(effect)){
      const range=sfpDurationRange(effect,current.period??6,sfpNativeAvailable(state));
      motion=paramControl({key:'period',label:'Cycle duration',type:'float',min:range.min,max:range.max,step:range.step,unit:'s',description:range.savedCustom?`Saved duration: ${current.period} s. Moving the slider selects a new duration.`:undefined},range.value);
    }
  }else appearance+=`<p class="help-text">${mode==='unmanaged'?'The indicator follows the port’s link status. Its color appears here when reported.':mode==='off'?'The selected lights are turned off.':'Choose a lighting mode for the selected SFP ports.'}</p>`;
  host.innerHTML=`${header}<div class="inspector-sections">${sfpSyncControls(sync)}${controlSection('Appearance',appearance+browse,'blend','primary-appearance')}${motion?controlSection('Motion',motion,'clock'):''}</div>`;
  bindSfpSync(host);
  const set=change=>{draft.sfp??={};for(const port of sfpEditTargets(state,ports,draft)){const light=editableSfpLight(state,port,draft);draft.sfp[port]=normalizeSfpBrightness({...sfpSettings(light),...(typeof change==='function'?change(light):change)});}queueEdit('sfp');};
  host.querySelector('#sfp-mode').onchange=event=>{set({mode:event.target.value});preserveInspectorFocus(renderInspector,'#sfp-mode');};
  const chooser=host.querySelector('#sfp-effect');if(chooser)chooser.onchange=event=>{set(light=>sfpEffectChange(light,event.target.value,sfpNativeAvailable(state)));preserveInspectorFocus(renderInspector,'#sfp-effect');};
  host.querySelector('#sfp-gallery')?.addEventListener('click',openGallery);
  bindColors(host,(key,value,brightness,changed)=>{if(changed==='resetColor'){for(const port of sfpEditTargets(state,ports,draft)){if(draft.sfp?.[port])draft.sfp[port][key]=lights.find(light=>light.port===port)?.[key]??current[key];}queueEdit('sfp');return;}set(changed==='color'?{[key]:value}:{[key==='color'?'color_brightness':'secondary_brightness']:brightness});});
  host.querySelectorAll('[data-hex]').forEach(input=>{if(!same(input.dataset.hex)){input.value='';input.placeholder='#409EFF';input.dataset.allowEmpty='true';setMixedColor(host.querySelector(`[data-color="${CSS.escape(input.dataset.hex)}"]`),true);}});
  host.querySelectorAll('[data-param]').forEach(input=>{if(!same(input.dataset.param))input.closest('.parameter').querySelector('output').textContent='Mixed';input.oninput=()=>{const key=input.dataset.param;input.closest('.parameter').querySelector('output').textContent=input.value+(key==='period'?' s':'%');set({[key]:Number(input.value)});};});
  host.querySelectorAll('[data-brightness]').forEach(input=>{if(!same(input.dataset.brightness==='color'?'color_brightness':'secondary_brightness'))input.closest('.parameter').querySelector('output').textContent='Mixed';});
  host.insertAdjacentHTML('beforeend',`<div class="inspector-actions"><button class="button quiet" id="copy-sfp" ${ports.length!==1?'disabled':''}>Copy appearance</button><button class="button quiet" id="paste-sfp" ${!copiedAppearance?'disabled':''}>Paste appearance</button></div>`);
  host.querySelector('#copy-sfp').onclick=()=>{copiedAppearance={type:'sfp',settings:sfpSettings(current)};host.querySelector('#paste-sfp').disabled=false;notify('Appearance copied');};
  host.querySelector('#paste-sfp').onclick=()=>{if(copiedAppearance.type==='effect'){stageEffect({...clone(copiedAppearance.settings),ports,clearOverrides:true});editor='effect';renderInspector();return;}try{set(copiedAppearance.type==='sfp'?clone(copiedAppearance.settings):sfpColorFromAppearance(copiedAppearance.settings));renderInspector();}catch(error){notify(error.message,true);}};
  inspectorSignature=sfpInspectorKey();updateRangeProgress();updateDraftBar();
}

function reconcileEditor(force = false) {
  if(page!=='Lighting'||!document.querySelector('#inspector'))return;
  if(busy||edits.size||activeColorPopover||document.querySelector('#inspector')?.contains(document.activeElement))return;
  const signature=useSfpInspector()?sfpInspectorKey():copperInspectorKey();
  if(force||signature!==inspectorSignature){renderInspector();}
}

function updateRangeProgress() {
  document.querySelectorAll('[data-relative-level] .configured-level').forEach(output=>{
    const input=output.closest('.parameter').querySelector('[data-brightness]'),master=number(draft.brightness??state.brightness,100);
    output.hidden=master===100;output.textContent=`Configured peak: ${Math.round(number(input?.value,100)*master/100)}%`;
  });
  document.querySelectorAll('input[type="range"]').forEach(input=>{
    const output=input.closest('.parameter')?.querySelector('output')||(input.id==='master'?document.querySelector('#master-value'):null);
    if(output){input.setAttribute('aria-valuetext',output.textContent);if(input.id)output.setAttribute('for',input.id);}
    const min=number(input.min),max=number(input.max,100);
    const progress=`${max>min?(number(input.value)-min)/(max-min)*100:0}%`;
    if(rangeProgress.get(input)===progress)return;
    input.style.setProperty('--range-progress',progress);rangeProgress.set(input,progress);
  });
}
function openColorPopover(anchor,pointerType) {
  activeColorPopover?.();
  activeColorPopover=openColorPicker(anchor,{pointerType,onClose:()=>{activeColorPopover=undefined;},onError:message=>notify(message,true),onRangeChange:updateRangeProgress});
}

async function openGroups() {
  if(!await confirmDraft())return;
  const send=workspaceRequest(),workspace=session.workspace_id,before=await send('/api/scene');
  const dialog=document.createElement('dialog');dialog.className='dialog group-dialog';dialog.setAttribute('aria-label','Port groups');
  dialog.innerHTML=`<form method="dialog"><header class="dialog-header"><h2>Port groups</h2><button class="button icon-button" value="cancel" aria-label="Close">×</button></header><p class="muted">Select Ethernet or SFP ports on the map to create a group.</p><h3>Existing groups</h3><div class="group-list">${(state.zones||[]).map(zone=>`<div class="row"><label class="field"><span>Group name</span><input data-group-name="${escape(zone.id)}" value="${escape(zone.name)}" maxlength="${GROUP_NAME_LIMIT}" required></label><span class="muted">Ports ${escape(portRanges(zone.ports))}</span></div>`).join('')}</div><h3>New group</h3><div class="field"><label for="new-group-name">Group for ${escape(selectionName())}</label><input id="new-group-name" placeholder="Group name" maxlength="${GROUP_NAME_LIMIT}" ${!effectSelection().length?'disabled':''}></div><label class="field"><span>Group effect</span><select id="new-group-effect" ${!effectSelection().length?'disabled':''}><option value="" ${hasMixedEffects()?'':'selected'}>${hasMixedEffects()?'Choose an effect':'Keep selected effect'}</option>${effects.map(effect=>`<option value="${escape(effect.id)}">${escape(effect.name)}</option>`).join('')}</select></label><p class="help-text">Creating a group moves the selected ports into it. The chosen effect starts when you save.</p><p class="inline-error" id="group-error" role="alert"></p><footer class="dialog-footer"><button class="button quiet" value="cancel">Cancel</button><button class="button primary" id="save-groups" type="button">Save groups</button></footer></form>`;
  document.body.append(dialog);dialog.addEventListener('close',()=>dialog.remove());dialog.showModal();
  let saving=false;
  dialog.addEventListener('cancel',event=>{if(saving)event.preventDefault();});
  dialog.querySelector('input:not(:disabled)')?.focus();
  dialog.querySelectorAll('[value="cancel"]').forEach(button=>{button.type='button';button.onclick=()=>dialog.close('cancel');});
  dialog.querySelector('#save-groups').type='submit';
  dialog.querySelector('form').addEventListener('submit',async event=>{
    event.preventDefault();const button=dialog.querySelector('#save-groups');if(button.disabled)return;button.disabled=true;
    try{
      const names=[...dialog.querySelectorAll('[data-group-name]')],freshInput=dialog.querySelector('#new-group-name');
      for(const input of [...names,...(freshInput.value.trim()?[freshInput]:[])]){const error=groupNameError(input.value);if(error){input.focus();throw new Error(error);}}
      const fresh=freshInput.value.trim(),ports=effectSelection();
      const chosenEffect=dialog.querySelector('#new-group-effect').value;if(fresh&&ports.length&&hasMixedEffects()&&!chosenEffect)throw new Error('Choose the effect for the new group.');
      saving=true;dialog.setAttribute('aria-busy','true');const controls=[...dialog.querySelectorAll('input,select,button')];for(const control of controls){control.dataset.groupDisabled=String(control.disabled);control.disabled=true;}
      await serialize(async()=>{
        const zones=clone(before.scene.zones||[]);for(const input of names){const zone=zones.find(z=>z.id===input.dataset.groupName);if(zone)zone.name=input.value.trim();}
        if(fresh&&ports.length){const selectedPorts=new Set(ports);for(const zone of zones)zone.ports=zone.ports.filter(p=>!selectedPorts.has(p));zones.push({id:`group-${crypto.randomUUID()}`,name:fresh,ports,effect:clone(chosenEffect?defaultConfig(chosenEffect):currentEffect()||{id:'static',params:{}})});}
        await commitScene(before,{...before.scene,zones:zones.filter(zone=>zone.ports.length)},send);if(workspace===session.workspace_id){lastApplied=null;await refresh(false);}
      });
      dialog.close();updateGroups();notify('Port groups saved');
    }catch(error){dialog.querySelector('#group-error').textContent=error.message;}finally{saving=false;dialog.removeAttribute('aria-busy');for(const control of dialog.querySelectorAll('[data-group-disabled]')){control.disabled=control.dataset.groupDisabled==='true';delete control.dataset.groupDisabled;}button.disabled=false;}
  });
}

function connectFrames() {
  if(closingWorkspace)return;
  if(currentSocket&&currentSocket.readyState<2)return;
  clearTimeout(retryTimer); currentSocket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/ws`);
  currentSocket.addEventListener('open',()=>{disconnected=false;updateConnection();if(document.querySelector('#status').textContent==='The workspace connection was interrupted.')status('Workspace connection restored');updateDraftBar();});
  currentSocket.addEventListener('message',event=>{
    let message;try{message=JSON.parse(event.data);}catch{return;}
    if(message.type==='frame'&&workspaceReady){receiveScheduleClock(message.schedule_clock);state={...state,schedule_clock:message.schedule_clock??state.schedule_clock,telemetry:message.telemetry??state.telemetry,poe_summary:message.poe_summary??state.poe_summary,identify:Object.hasOwn(message,'identify')?message.identify:state.identify,ports:message.ports??state.ports,sfp:message.sfp??state.sfp,sfp_sync:message.sfp_sync??state.sfp_sync,controller:message.controller??state.controller,stats:message.stats??state.stats,uplink_bandwidth:Object.hasOwn(message,'uplink_bandwidth')?message.uplink_bandwidth:state.uplink_bandwidth,reconnect_required:message.reconnect_required??state.reconnect_required,error:Object.hasOwn(message,'error')?message.error:state.error,connected:message.connected,mode:message.mode};session={...session,connected:message.connected};updateConnection();updateLive();}
    else if(message.type==='state'&&workspaceReady){
      receiveScheduleClock(message.state.schedule_clock);
      state={...message.state,ports:state.ports};
      if(!busy&&lastApplied?.after&&sceneFingerprint(state)!==sceneFingerprint(lastApplied.after.scene))lastApplied=null;
      if(page==='Lighting'){updateGroups();updateLive();reconcileEditor();}
    }else if(message.type==='session'){void acceptSession(message.session);}
    if(!document.hidden){
      if(page==='Automation')secondaryCleanup?.update?.(state);
      if(page==='Profiles')secondaryCleanup?.update?.(state);
      if(page==='Settings')secondaryCleanup?.update?.(state,session,disconnected);
    }
  });
  currentSocket.addEventListener('close',()=>{
    if(closingWorkspace)return;
    disconnected=true;updateConnection();updateLive();if(page==='Settings')secondaryCleanup?.update?.(state,session,true);status('The workspace connection was interrupted.',true);retryTimer=setTimeout(connectFrames,1500);
  });
  currentSocket.addEventListener('error',()=>currentSocket.close());
}
document.querySelectorAll('[data-page]').forEach(button=>button.addEventListener('click',()=>run(()=>navigate(button.dataset.page))));
document.addEventListener('input',event=>{if(event.target.matches('input[type="range"]'))updateRangeProgress();});
document.addEventListener('focusout',()=>{clearTimeout(refreshTimer);refreshTimer=setTimeout(()=>reconcileEditor(),150);});
document.addEventListener('visibilitychange',()=>{
  if(document.hidden){saveUiState();portAnimator.stop();return;}
  updateConnection();updateLive();
  if(page==='Automation'||page==='Profiles')secondaryCleanup?.update?.(state);
  if(page==='Settings')secondaryCleanup?.update?.(state,session,disconnected);
});
window.addEventListener('beforeunload',event=>{saveUiState();if(busy||pendingPlan||edits.size||secondaryCleanup?.hasChanges?.()){event.preventDefault();event.returnValue='';}});
window.addEventListener('popstate',()=>{const historyEntry=routeHistory.observe();if(!historyEntry)return;const target=['Lighting','Effects','Automation','Profiles','Settings','Help'].find(name=>`#${name.toLowerCase()}`===historyEntry.hash)||'Lighting';run(()=>navigate(target,{fromHistory:true,historyEntry}));});

function resetWorkspace() {
  navigationVersion++;
  document.querySelectorAll('.group-dialog,.draft-dialog,.rack-workspace-dialog,.saved-library,.scene-history,.port-finder,.scene-conflict').forEach(dialog=>dialog.close('stay'));
  activeColorPopover?.();secondaryCleanup?.();secondaryCleanup=undefined;portAnimator.stop();portGlow?.dispose();portGlow=null;uplinkBandwidth?.dispose();uplinkBandwidth=null;
  livePortDetails?.close();livePortDetails=null;portHistory.clear();
  state={};selected=new Set();draft={};draftBaseline=null;draftBaseScene=null;edits.clear();pendingPlan=null;lastApplied=null;copiedAppearance=null;compareApplied=false;
  rememberedEffects.clear();previewClocks.clear();previewStates.clear();for(const key of Object.keys(galleryState))delete galleryState[key];
  inspectorSignature='';workspaceReady=false;
  multipleSelection=false;selectionAnchor=null;
}
function showConnection() {
  if(workspaceReady)resetWorkspace();
  secondaryCleanup?.();
  secondaryCleanup=renderSwitchConnection(view,{request,session,onConnected:acceptSession,onSessionChange:async(next,notice)=>{await acceptSession(next);if(notice)notify(notice.message,notice.error);},onHelp:showLoginHelp,
    restoreSaved:()=>request('/api/session/restore?resume=true','POST'),
    onDemo:workspaceOptions.live?undefined:async()=>acceptSession(await request('/api/session/login','POST',{host:'demo'}))});
  const handoff=handoffStatus(session);
  updateConnection();status(handoff?.message||(session.connecting?'Connecting with saved login…':'Connect your switch'),handoff?.error===true);
}
function showLoginHelp(){
  if(document.querySelector('.login-help'))return;
  const dialog=document.createElement('dialog');dialog.className='dialog login-help';dialog.setAttribute('aria-label','Help');
  const content=document.createElement('div'),close=document.createElement('button');close.className='button';close.textContent='Back to connection';close.onclick=()=>dialog.close();
  dialog.append(content,close);document.body.append(dialog);renderHelp(content,{state});dialog.addEventListener('close',()=>dialog.remove(),{once:true});dialog.showModal();close.focus();
}
async function acceptSession(next) {
  if(closingWorkspace)return;
  if(!next)return;
  next=await showSceneChoice(request,next);
  if(!next||closingWorkspace)return;
  const changed=session.workspace_id!==next.workspace_id||session.simulated!==next.simulated;
  if(changed&&(workspaceReady||openingWorkspace)){saveUiState();resetWorkspace();}
  session=next;updateConnection();
  if(!session.logged_in){if(!workspaceReady&&secondaryCleanup?.update)secondaryCleanup.update(session);else showConnection();const handoff=handoffStatus(session);if(handoff)status(handoff.message,handoff.error);return;}
  if(workspaceReady)return;
  const workspace=session.workspace_id;
  if(openingWorkspace?.workspace===workspace)return openingWorkspace;
  const initializing=(async()=>{
    secondaryCleanup?.();secondaryCleanup=undefined;
    await refresh(false);if(workspace!==session.workspace_id||!session.logged_in)return;selected=new Set((state.ports||[]).map(port=>port.port));
    let saved;try{const key=workspaceStorageKey(session);if(key)saved=JSON.parse(sessionStorage.getItem(key)||'null');}catch{}
    if(saved){
      editor=['color','effect'].includes(saved.editor)?saved.editor:'effect';
      selected=new Set((Array.isArray(saved.selected)?saved.selected:[]).filter(id=>allPorts().includes(id)));
      if(saved.galleryState&&typeof saved.galleryState==='object'&&!Array.isArray(saved.galleryState))Object.assign(galleryState,saved.galleryState);
      for(const entry of Array.isArray(saved.effects)?saved.effects:[]){if(!Array.isArray(entry))continue;const [id,config]=entry;if(!effectSpec(id))continue;try{lightingOperations(state,{effect:{...config,ports:config.ports||[state.ports[0]?.port]}});rememberedEffects.set(id,config);}catch{}}
      if(saved.draft&&Object.keys(saved.draft).length){try{lightingOperations(state,saved.draft);draft=saved.draft;draftBaseScene=saved.draftBaseScene||null;draftBaseline=typeof saved.draftBaseline==='string'?saved.draftBaseline:sceneFingerprint(state);edits.add('restored');}catch{notify('The saved preview has invalid settings. Your applied lighting is unchanged.',true);}}
      if(edits.size&&saved.recovery?.before){
        try{
          const current=await request('/api/scene');if(workspace!==session.workspace_id)return;
          const intended=lightingSnapshot(saved.recovery.before.scene,state,draft);
          if(sceneFingerprint(current.scene)===sceneFingerprint(intended)){
            lastApplied={before:saved.recovery.before,after:current,mode:saved.recovery.mode??state.mode,workspace};
            const remainingMode=draft.mode;draft={};edits.clear();draftBaseline=null;draftBaseScene=null;
            if(remainingMode!==undefined&&remainingMode!==state.mode){draft={mode:remainingMode};draftBaseline=sceneFingerprint(current.scene);edits.add('restored');}
          }
        }catch{ /* Keep the preview for review when confirmation is unavailable. */ }
      }
    }
    const pages=['Lighting','Effects','Automation','Profiles','Settings','Help'],target=edits.size?'Lighting':pages.find(name=>`#${name.toLowerCase()}`===location.hash)||(pages.includes(saved?.page)?saved.page:'Lighting');
    workspaceReady=true;updateConnection();await navigate(target,{keepDraft:true,fromHistory:true});portAnimator.setPreview(edits.size?previewDraft:null);status(edits.size?'Restored unsaved preview':'Lighting output');
  })();
  initializing.workspace=workspace;openingWorkspace=initializing;
  try{await initializing;}catch{
    if(workspace!==session.workspace_id)return;
    workspaceReady=false;updateConnection();
    view.innerHTML='<div class="empty-state"><h1>The switch is connected</h1><p>Its workspace could not load. Retry to read the current state.</p><button class="button primary" id="retry-workspace">Reopen workspace</button></div>';
    document.querySelector('#retry-workspace').onclick=()=>acceptSession(session);status('Workspace state unavailable',true);
  }finally{if(openingWorkspace===initializing)openingWorkspace=null;}
}
async function closeWorkspace() {
  closingWorkspace=true;clearTimeout(retryTimer);
  try{
    await request('/api/workspace/close','POST');currentSocket?.close();resetWorkspace();updateConnection();
    showConnectionStatus(document.querySelector('#connection'),{label:'Web session closed',tone:'is-disconnected'});status('Lighting connection released');
    view.innerHTML='<div class="empty-state"><h1>Web session closed</h1><p>Your lighting setup and saved switch login are preserved. You can now open the desktop app.</p></div>';
  }catch(error){closingWorkspace=false;connectFrames();throw error;}
}
async function start() {
  try{
    let initial;
    [effects,initial,workspaceOptions]=await Promise.all([request('/api/effects'),request('/api/session'),request('/api/workspace')]);setEffectCatalog(effects);
    session=initial;connectFrames();
    if(session.logged_in||session.pending_scene_choice)await acceptSession(session);else showConnection();
  }catch(error){
    view.innerHTML='<div class="empty-state"><h1>Couldn’t open the workspace</h1><p>The local service is unavailable. Try connecting again.</p><button class="button primary" id="retry-start">Try again</button></div>';
    document.querySelector('#retry-start').addEventListener('click',start);status('Connection unavailable',true);
  }
}
await start();
