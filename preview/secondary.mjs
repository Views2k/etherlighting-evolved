import {confirmAction} from './confirmation.mjs';
import {switchHealth} from './switch-health.mjs';
import {createOperationGate, matchingProfile, sceneFingerprint, profileNameError, scopeSummary, templateForRule, validateRuleDraft, conditionAvailable, ruleRateFromCycle, rulesSnapshot, profileSfpSummary} from './secondary-model.mjs';
import {effectById} from './effect-renderer.mjs';
import {createProfilePreviews, profileLayoutNotice} from './profile-preview.mjs';
import {lightingPorts} from './port-topology.mjs';
import {requestHandoff, renderControllerConnection, renderPrivateAccess, showConnectionStatus} from './connections.mjs';
import {switchConnectionStatus} from './workspace-session.mjs';
import {commitScene} from './scene-transaction.mjs';
import {receiveScheduleClock,scheduleClockLabel} from './schedule-clock.mjs';

const node = (tag, className = '', text) => {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = String(text);
  return element;
};
const copy = value => structuredClone(value);
const portName = (port, sfp) => sfp.includes(port) ? `SFP ${port}` : `Port ${port}`;
const operatorNames = {is:'is', is_not:'is not', in:'is any of', not_in:'is none of', gt:'is above', ge:'is at least', lt:'is below', le:'is at most', eq:'equals', ne:'does not equal', between:'is between'};
const styleNames = {solid:'Solid color', pulse:'Gentle pulse', blink:'Blink', flash:'Flash', effect:'Use port effect'};

function field(label, control) {
  const wrapper = node('label', 'field');
  wrapper.append(node('span', '', label), control);
  return wrapper;
}
function input(value = '', type = 'text') {
  const element = node('input'); element.type = type; element.value = value; return element;
}
function select(options, value) {
  const element = node('select');
  for (const [key, label] of options) {
    const option = node('option', '', label); option.value = key; element.append(option);
  }
  element.value = value ?? options[0]?.[0] ?? '';
  return element;
}
function check(label, checked = false) {
  const wrapper = node('label', 'check'); const control = input('', 'checkbox'); control.checked = checked;
  wrapper.append(control, node('span', '', label)); return {wrapper, control};
}
function button(label, action, className = '') {
  const element = node('button', `button ${className}`, label); element.type = 'button';
  element.addEventListener('click', async () => {
    if (element.disabled) return;
    const owner = element.closest('dialog,main'), restoreFocus = document.activeElement === element;
    element.disabled = true;
    try { await action(); } finally {
      element.disabled = false;
      if (restoreFocus && owner) requestAnimationFrame(() => {
        if (!owner.isConnected || owner.inert || document.activeElement !== document.body) return;
        const target = (element.isConnected ? element : [...owner.querySelectorAll('button')].find(control =>
          !control.disabled && (element.dataset.focus ? control.dataset.focus === element.dataset.focus
            : control.textContent === element.textContent))) || owner.querySelector('h1,h2');
        if (target?.matches('h1,h2')) target.tabIndex = -1;
        target?.focus({preventScroll:true});
      });
    }
  });
  return element;
}
function operationGate(host, status) {
  let previousFocus, focusKey, focusLabel;
  const gate = createOperationGate(busy => {
    if (busy) {
      previousFocus = host.contains(document.activeElement) ? document.activeElement : null;
      focusKey = previousFocus?.dataset.focus;
      focusLabel = previousFocus?.getAttribute('aria-label') || previousFocus?.textContent;
    }
    host.inert = busy;
    host.setAttribute('aria-busy', String(busy));
    host.classList.toggle('is-saving', busy);
    if (!busy && previousFocus) {
      const original = previousFocus, key = focusKey, label = focusLabel;
      previousFocus = null;
      requestAnimationFrame(() => {
        if (!gate.active || host.inert || !host.isConnected) return;
        if (document.activeElement !== document.body && document.activeElement !== original) return;
        const controls = [...host.querySelectorAll('button,input,select,[tabindex]')];
        const target = (original.isConnected ? original : key
          ? controls.find(control => control.dataset.focus === key)
          : controls.find(control => (control.getAttribute('aria-label') || control.textContent) === label)) || host.querySelector('h1,h2');
        if (target?.matches('h1,h2')) target.tabIndex = -1;
        if (target && !target.disabled) target.focus({preventScroll:true});
      });
    }
  }, error => status(error.message || 'The change could not be completed. Try again.', true));
  return gate;
}
function section(title, description) {
  const content = node('section', 'automation-section');
  content.append(node('h2', 'section-heading', title));
  if (description) content.append(node('p', 'help-text muted', description));
  return content;
}
function retryPanel(host, message, retry) {
  const panel = node('div', 'retry-panel');
  panel.append(node('p', 'muted', message), button('Retry', retry)); host.replaceChildren(panel);
}
function focusControl(form, key) {
  form.querySelector(`[data-focus="${key}"]`)?.focus({preventScroll:true});
}
function frame(host, title, description) {
  host.replaceChildren();
  const header = node('header', 'view-header');
  const text = node('div'); text.append(node('h1', '', title), node('p', 'muted', description)); header.append(text);
  const notice = node('p', 'notice'); notice.role = 'status'; notice.setAttribute('aria-live', 'polite'); notice.setAttribute('aria-atomic','true');
  host.append(header);
  const status = (message = '', error = false) => {
    notice.textContent = message; notice.classList.toggle('error', error);
  };
  return {header, notice, status};
}
function displayChoice(value) {
  if (value.startsWith('poe')) return 'PoE' + value.slice(3);
  return value.replaceAll('_', ' ').replace(/^./, c => c.toUpperCase());
}

function initialCondition(spec) {
  const op = spec.type === 'number' ? 'ge' : spec.operators[0];
  const value = spec.type === 'bool' ? true : spec.type === 'number' ? 100 : spec.type === 'time' ? ['22:00','07:00'] : spec.type === 'weekday' ? ['mon','tue','wed','thu','fri'] : spec.choices?.[0];
  return {field:spec.key, op, value};
}

function conditionEditor(condition, specs, onChange, remove, index) {
  const spec = specs.find(item => item.key === condition?.field);
  const row = node('div', 'condition-row');
  if (!conditionAvailable(condition,specs)) {
    const replacement = select([['','Choose a condition'],...specs.map(item=>[item.key,item.label])],'');
    replacement.setAttribute('aria-label','Replace unavailable condition');replacement.dataset.focus=`condition-${index}-property`;
    replacement.addEventListener('change',()=>{const next=specs.find(item=>item.key===replacement.value);if(next)onChange(initialCondition(next),'property');});
    row.append(node('p','help-text','This condition is unavailable. Choose a replacement or remove it.'),replacement,button('Remove',remove,'quiet'));
    return {row,read:()=>copy(condition)};
  }
  const property = select(specs.map(item => [item.key, item.label]), condition.field);
  const operator = select(spec.operators.map(key => [key, operatorNames[key] || key]), condition.op);
  property.setAttribute('aria-label', 'Condition property'); operator.setAttribute('aria-label', 'Condition operator');
  property.dataset.focus = `condition-${index}-property`; operator.dataset.focus = `condition-${index}-operator`;
  const values = node('div', 'condition-value');
  let readValue;
  if (spec.type === 'bool') {
    const boolean = select([['true','Yes'],['false','No']], String(condition.value));
    boolean.setAttribute('aria-label', 'Condition value'); values.append(boolean); readValue = () => boolean.value === 'true';
  } else if (spec.type === 'weekday' || (spec.type === 'choice' && ['in','not_in'].includes(condition.op))) {
    const choices = node('div', 'port-checks'); const controls = [];
    for (const value of spec.choices) {
      const item = check(displayChoice(value), Array.isArray(condition.value) && condition.value.includes(value));
      controls.push([value, item.control]); choices.append(item.wrapper);
    }
    values.append(choices); readValue = () => controls.filter(([,control]) => control.checked).map(([value]) => value);
  } else if (spec.type === 'choice') {
    const choices = select(spec.choices.map(value => [value, displayChoice(value)]), condition.value);
    choices.setAttribute('aria-label', 'Condition value'); values.append(choices); readValue = () => choices.value;
  } else if (spec.type === 'time' || condition.op === 'between') {
    const pair = node('div', 'row');
    const type = spec.type === 'time' ? 'time' : 'number';
    const first = input(condition.value[0], type), last = input(condition.value[1], type);
    first.required = last.required = true; first.setAttribute('aria-label', 'Start'); last.setAttribute('aria-label', 'End');
    if (type === 'number') first.step = last.step = 'any';
    pair.append(first, node('span', 'muted', 'to'), last); values.append(pair);
    readValue = () => spec.type === 'time' ? [first.value,last.value] : [Number(first.value),Number(last.value)];
  } else {
    const threshold = input(condition.value, 'number'); threshold.step = 'any'; threshold.required = true;
    threshold.setAttribute('aria-label', `Value${spec.unit ? ` in ${spec.unit}` : ''}`); values.append(threshold);
    if (spec.unit) values.append(node('span', 'muted unit', spec.unit));
    readValue = () => Number(threshold.value);
  }
  const read = () => ({field:condition.field, op:condition.op, value:readValue()});
  property.addEventListener('change', () => onChange(initialCondition(specs.find(item => item.key === property.value)), 'property'));
  operator.addEventListener('change', () => {
    const next = read(); next.op = operator.value;
    if (next.op === 'between' && !Array.isArray(next.value)) next.value = [next.value, Number(next.value) + 100];
    else if (['in','not_in'].includes(next.op) && !Array.isArray(next.value)) next.value = [next.value];
    else if (!['between','in','not_in'].includes(next.op) && Array.isArray(next.value)) next.value = next.value[0];
    onChange(next, 'operator');
  });
  const removeButton = button('Remove', remove, 'quiet'); removeButton.setAttribute('aria-label', 'Remove condition');
  removeButton.dataset.focus = `condition-${index}-remove`;
  row.append(property, operator, values, removeButton);
  return {row, read};
}

export async function renderAutomation(host, {request, state, onChange, getState = () => state}) {
  const controller = new AbortController();
  const {header, notice, status} = frame(host, 'Automation', 'Choose when your ports change color.');
  const gate = operationGate(host, status);
  const split = node('div', 'secondary-split split automation-workspace'), list = node('aside', 'secondary-list list');
  list.setAttribute('aria-label', 'Lighting rules');
  const editor = node('section', 'secondary-editor panel rule-editor'); split.append(list, editor); host.append(split, notice);
  let dirty = false, orderDirty = false, specs = [], templates = [], rules = [], savedOrder = [], draft, form, readers = [], selectedId, summary, sceneBase, scheduleHint;
  const ruleStatusNodes = new Map();
  let outputState = getState();
  const sceneKey = value => Number.isSafeInteger(value?.revision) ? `revision:${value.revision}` : sceneFingerprint(value);
  const sceneRefreshError = 'The lighting setup changed. Refresh rules to load it.';
  let observedSceneKey = sceneKey(outputState), latestSceneKey = observedSceneKey, sceneRead;
  const {copper:copperPorts, sfp:sfpPorts} = lightingPorts(state), allPorts = [...copperPorts, ...sfpPorts];
  const blank = () => ({name:'', enabled:true, match:'all', ports:[...(copperPorts.length ? copperPorts : sfpPorts)], conditions:[], look:{style:'solid',color:'#ff667e',rate:.25,brightness:null}});
  const discard = () => !dirty || confirmAction(host, 'Discard rule changes?', 'Your unapplied rule changes will be discarded.', 'Discard', controller.signal);
  const scopeText = ports => scopeSummary(ports, copperPorts, sfpPorts);
  function sync() {
    if (!form || !draft) return;
    const controls = form.elements;
    draft.name = controls.ruleName.value.trim(); draft.enabled = controls.enabled.checked; draft.match = controls.match.value;
    draft.ports = [...form.querySelectorAll('[data-port]:checked')].map(control => Number(control.dataset.port));
    draft.conditions = readers.map(item => item.read());
    draft.look = {...draft.look,color:controls.ruleColor.value, style:controls.style.value, rate:ruleRateFromCycle(Number(controls.cycle.dataset.rate),controls.cycle.dataset.initial,controls.cycle.value), brightness:controls.customBrightness.checked ? Number(controls.brightness.value) : null};
  }
  function updateSummary() {
    sync();
    const conditions = draft.conditions.map(condition => {
      if (!conditionAvailable(condition,specs)) return 'Unavailable condition';
      const spec = specs.find(item => item.key === condition.field);
      const value = Array.isArray(condition.value) ? condition.value.join(condition.op === 'between' ? ' to ' : ', ') : typeof condition.value === 'boolean' ? condition.value ? 'Yes' : 'No' : condition.value;
      return `${spec?.label || condition.field} ${operatorNames[condition.op] || condition.op} ${value}${spec?.unit ? ` ${spec.unit}` : ''}`;
    });
    summary.textContent = !draft.name || !conditions.length ? 'Enter a rule name and add a condition, or choose a template.'
      : `${scopeText(draft.ports)} · ${conditions.join(draft.match === 'any' ? ' or ' : ' and ')} → ${styleNames[draft.look.style]}${draft.look.brightness != null ? ` at ${draft.look.brightness}%` : ''}`;
    form.querySelector('button[type="submit"]').disabled = Boolean(validateRuleDraft(draft,specs));
  }
  async function applyRules(nextRules) {
    if (!sceneBase?.revision) throw new Error('Refresh the rules before applying changes.');
    const scene = rulesSnapshot(sceneBase.scene,nextRules);
    let result, confirmationError;
    try { result = await commitScene(sceneBase,scene,request); }
    catch(error) {
      if (!error.acceptedScene) throw error;
      result = error.acceptedScene;
      confirmationError = new Error('Rule changes are saved, but lighting output was not confirmed. Use Refresh rules to check the current state.');
    }
    sceneBase = result; rules = copy(result.scene.rules); savedOrder = rules.map(rule=>rule.id);
    return confirmationError;
  }
  async function refreshAfterWrite(message, redraw, confirmationError) {
    let refreshed = true;
    try { await onChange?.(); } catch { refreshed = false; }
    if (!gate.active) return;
    try { redraw?.(); } catch { refreshed = false; }
    if (confirmationError) throw confirmationError;
    status(refreshed ? message : `${message} Status could not be refreshed. Use Refresh rules to check it.`, !refreshed);
  }
  function updateRuleOutput(nextState = outputState) {
    if (!gate.active) return;
    outputState = nextState;
    receiveScheduleClock(nextState.schedule_clock);
    if (scheduleHint) {
      const zone = scheduleClockLabel(nextState);
      const text = `Schedule time: ${zone === 'EE computer' ? zone : `${zone} · EE computer`}. A time range can continue past midnight.`;
      if (scheduleHint.textContent !== text) scheduleHint.textContent = text;
    }
    const counts = new Map();
    const available = (nextState.ports || []).some(port => port.source != null)
      || (nextState.sfp || []).some(port => Object.hasOwn(port, 'rule'));
    if (nextState.mode === 'running') {
      for (const port of nextState.ports || []) {
        if (port.source?.kind === 'rule' && port.source.id) counts.set(port.source.id, (counts.get(port.source.id) || 0) + 1);
      }
      for (const port of nextState.sfp || []) {
        if (port.rule?.id) counts.set(port.rule.id, (counts.get(port.rule.id) || 0) + 1);
      }
    }
    for (const [id, element] of ruleStatusNodes) {
      const count = counts.get(id) || 0;
      const text = nextState.mode !== 'running' ? 'EE output is stopped'
        : available ? `Controlling ${count} ${count === 1 ? 'port' : 'ports'}` : 'Waiting for output status';
      if (element.textContent !== text) element.textContent = text;
    }
  }
  function refreshCleanScene() {
    if (!gate.active || !sceneBase || dirty || orderDirty || gate.pending || sceneRead || latestSceneKey === observedSceneKey) return;
    const key = latestSceneKey, base = sceneBase;
    observedSceneKey = key;
    sceneRead = request('/api/scene').then(result => {
      if (!gate.active || base !== sceneBase || key !== latestSceneKey) return;
      if (dirty || orderDirty || gate.pending) { observedSceneKey = null; return; }
      const previousRules = JSON.stringify(sceneBase.scene.rules), previousDraft = JSON.stringify(draft);
      sceneBase = result;
      if (notice.textContent === sceneRefreshError) status('');
      if (JSON.stringify(result.scene.rules) === previousRules) return;
      const focused = document.activeElement, focusInside = host.contains(focused);
      rules = copy(result.scene.rules); savedOrder = rules.map(rule => rule.id);
      if (selectedId != null && !rules.some(rule => rule.id === selectedId)) selectedId = rules[0]?.id;
      draft = copy(rules.find(rule => rule.id === selectedId) || blank());
      drawList();
      if (JSON.stringify(draft) !== previousDraft) drawEditor();
      if (focusInside && !focused.isConnected && (document.activeElement === document.body || document.activeElement === focused)) {
        const controls = [...host.querySelectorAll('button,input,select')];
        const replacement = controls.find(control =>
          focused.dataset.ruleId ? control.dataset.ruleId === focused.dataset.ruleId
            : focused.dataset.focus ? control.dataset.focus === focused.dataset.focus
            : focused.name ? control.name === focused.name
            : focused.getAttribute('aria-label') && control.getAttribute('aria-label') === focused.getAttribute('aria-label'))
          || (focused.dataset.ruleId ? controls.find(control => control.dataset.ruleId === selectedId) || newRule : null);
        replacement?.focus({preventScroll:true});
      }
    }).catch(() => {
      if (gate.active && base === sceneBase && key === latestSceneKey && !dirty && !orderDirty && !gate.pending) status(sceneRefreshError, true);
    }).finally(() => { sceneRead = null; refreshCleanScene(); });
  }
  function drawList() {
    list.replaceChildren();
    ruleStatusNodes.clear();
    rules.forEach((rule, index) => {
      const item = button('', () => gate.run(async () => {
        if (!await discard()) return;
        selectedId = rule.id; draft = copy(rule); dirty = false; drawList(); drawEditor();
      }), `rule-item ${rule.id === selectedId ? 'selected' : ''}`);
      item.dataset.ruleId = rule.id;
      const heading = node('span', 'rule-list-heading'), swatch = node('i', 'rule-swatch');
      swatch.style.background = rule.look?.color || '#8b83ba'; swatch.setAttribute('aria-hidden', 'true');
      heading.append(swatch, node('span','item-name',`${index + 1}. ${rule.name}`));
      const output = node('span', 'rule-output-status muted');
      ruleStatusNodes.set(rule.id, output);
      item.append(heading, node('span', 'muted', `${rule.enabled ? 'Enabled' : 'Disabled'} · ${scopeText(rule.ports)}`), output);
      item.setAttribute('aria-pressed', String(rule.id === selectedId)); list.append(item);
    });
    updateRuleOutput(getState());
    if (!rules.length) list.append(node('p', 'muted empty-state', 'Create a rule to respond to port activity or time.'));
    list.append(node('p', 'muted list-hint', 'Rules run from top to bottom. The first match sets the port lighting.'));
    if (orderDirty) {
      const actions = node('div','rule-order-actions');
      actions.append(node('p','help-text','Priority changes'), button('Apply priority', () => gate.run(async () => {
        const confirmationError = await applyRules(rules); orderDirty = false;
        await refreshAfterWrite('Rule priority applied.',drawList,confirmationError);
      }), 'primary'), button('Cancel priority', () => {
        const order = new Map(savedOrder.map((id,index) => [id,index]));
        rules.sort((a,b) => (order.get(a.id) ?? rules.length) - (order.get(b.id) ?? rules.length));
        orderDirty = false; drawList(); drawEditor();
      })); list.append(actions);
    }
  }
  function drawEditor(focusKey) {
    if (!gate.active || !draft) return;
    const scroll = editor.querySelector('[data-rule-scroll]')?.scrollTop || 0;
    editor.replaceChildren(); readers = []; scheduleHint = null; form = node('form', 'rule-form');
    const body = node('div', 'rule-form-body'); body.dataset.ruleScroll = '';
    const nameRow = node('div', 'form-grid');
    const name = input(draft.name); name.name = 'ruleName'; name.maxLength = 60; name.required = true; name.dataset.focus = 'rule-name';
    const enabled = check('Enabled', draft.enabled !== false); enabled.control.name = 'enabled';
    nameRow.append(field('Rule name', name), enabled.wrapper); body.append(nameRow);
    const when = section('When', 'Choose the conditions that trigger this rule.');
    const templateRow = node('div','row template-row'), template = select([['','Choose a template'], ...templates.map((item,index) => [String(index),item.name])], '');
    template.setAttribute('aria-label','Rule template');
    templateRow.append(template, button('Use template', () => gate.run(async () => {
      if (!template.value) return;
      sync();
      draft = templateForRule(templates[Number(template.value)], draft, copperPorts);
      dirty = true; drawEditor('condition-0-property');
    })));
    when.append(templateRow);
    const match = select([['all','All conditions'],['any','Any condition']], draft.match || 'all'); match.name = 'match';
    when.append(field('Match',match));
    const conditionList = node('div','condition-list');
    if (!draft.conditions?.length) {
      draft.conditions = [];
      when.append(node('p','help-text','Add a condition or choose a template.'));
    }
    draft.conditions.forEach((condition,index) => {
      const item = conditionEditor(condition, specs, (next,control) => {
        sync(); draft.conditions[index] = next; dirty = true; drawEditor(`condition-${index}-${control}`);
      }, () => {
        sync(); draft.conditions.splice(index,1); dirty = true;
        drawEditor(draft.conditions.length ? `condition-${Math.min(index,draft.conditions.length-1)}-property` : 'condition-add');
      }, index);
      readers.push(item); conditionList.append(item.row);
    });
    const addCondition = button('Add condition', () => {
      sync(); draft.conditions.push(initialCondition(specs.find(item => item.key === 'anomaly') || specs[0])); dirty = true; drawEditor(`condition-${draft.conditions.length-1}-property`);
    }, 'quiet'); addCondition.dataset.focus = 'condition-add';
    when.append(conditionList, addCondition);
    if (draft.conditions.some(condition => ['time','weekday'].includes(condition.field))) {
      scheduleHint = node('p','help-text');
      when.append(scheduleHint); updateRuleOutput(getState());
    }
    body.append(when);
    const scope = section('On these ports');
    const toolbar = node('div','scope-toolbar'), count = node('span','scope-count'), boxes = [];
    const all = check('All ports', false); all.control.setAttribute('aria-label','All ports');
    const updateScope = () => {
      const selected = boxes.filter(item => item.control.checked).length;
      all.control.checked = selected === boxes.length; all.control.indeterminate = selected > 0 && selected < boxes.length;
      count.textContent = `${selected} of ${boxes.length} selected`;
    };
    const choose = ports => { for (const item of boxes) item.control.checked = ports.includes(Number(item.control.dataset.port)); dirty = true; updateScope(); updateSummary(); };
    all.control.addEventListener('change', () => choose(all.control.checked ? allPorts : []));
    toolbar.append(all.wrapper, button('Ethernet', () => choose(copperPorts), 'quiet'), button('SFP', () => choose(sfpPorts), 'quiet'), button('None', () => choose([]), 'quiet'), count);
    const portChecks = node('div','port-checks');
    for (const port of allPorts) {
      const box = check(portName(port,sfpPorts), (draft.ports ?? copperPorts).includes(port));
      box.control.dataset.port = port; box.control.addEventListener('change',updateScope); boxes.push(box); portChecks.append(box.wrapper);
    }
    scope.append(toolbar,portChecks); body.append(scope); updateScope();
    const appearance = section('Show this');
    const look = draft.look || {}, lookRow = node('div','form-grid look-fields');
    const style = select(Object.entries(styleNames), look.style || 'solid'); style.name = 'style';
    const colorRow = node('div','row color-field');
    const color = input(/^#[a-f\d]{6}$/i.test(look.color) ? look.color : '#ffffff','color'); color.name = 'ruleColor'; color.setAttribute('aria-label','Rule color');
    const hex = input(color.value); hex.maxLength = 7; hex.pattern = '#[a-fA-F0-9]{6}'; hex.required = true; hex.setAttribute('aria-label','Hex color');
    color.addEventListener('input', () => hex.value = color.value);
    hex.addEventListener('input', () => { if (/^#[a-f\d]{6}$/i.test(hex.value)) color.value = hex.value; }); colorRow.append(color,hex);
    const originalRate = look.rate ?? .25;
    const cycle = input(String(1 / originalRate),'number'); cycle.name = 'cycle'; cycle.step = 'any'; cycle.required = true;
    cycle.dataset.rate=String(originalRate);cycle.dataset.initial=cycle.value;
    const cycleField = field('Cycle time · seconds',cycle), colorField = field('Color',colorRow);
    function updateStyle() {
      colorField.hidden = style.value === 'effect'; color.disabled = hex.disabled = style.value === 'effect';
      const animated = ['pulse','blink','flash'].includes(style.value); cycleField.hidden = !animated; cycle.disabled = !animated;
      cycle.min = String(Math.min(style.value === 'pulse' ? 3.04 : style.value === 'flash' ? 2 : 1,Number(cycle.dataset.initial)));
      cycle.max = String(Math.max(20,Number(cycle.dataset.initial)));
    }
    style.addEventListener('change',updateStyle); updateStyle();
    lookRow.append(field('Appearance',style), colorField,cycleField); appearance.append(lookRow);
    const levelRow = node('div','row brightness-row'), custom = check('Set brightness',look.brightness != null); custom.control.name = 'customBrightness';
    const level = input(look.brightness ?? getState().brightness ?? 100,'range'); level.name = 'brightness'; level.min = 0; level.max = 100; level.step = 1; level.disabled = !custom.control.checked; level.setAttribute('aria-label','Rule brightness');
    const output = node('output','',`${level.value}%`);
    custom.control.addEventListener('change', () => level.disabled = !custom.control.checked);
    const updateLevel=()=>{output.value=`${level.value}%`;level.style.setProperty('--range-progress',`${level.value}%`);level.setAttribute('aria-valuetext',`${level.value} percent`);};
    level.addEventListener('input',updateLevel);updateLevel();
    levelRow.append(custom.wrapper,level,output); appearance.append(levelRow,node('p','help-text','Set brightness replaces the default brightness for matching ports. Use port effect keeps their effect and changes only the brightness.'));
    body.append(appearance);
    summary = node('p','rule-summary'); body.append(summary);
    const actions = node('div','view-actions rule-actions'), save = node('button','button primary',orderDirty?'Apply rule and priority':'Apply rule'); save.type = 'submit';
    actions.append(save,button('Cancel rule changes', () => { dirty = false; draft = copy(rules.find(rule => rule.id === selectedId) || blank()); drawEditor('rule-name'); }));
    if (selectedId) {
      const priority = node('div','priority-controls'), index = rules.findIndex(rule => rule.id === selectedId);
      const move = direction => {
        const target = index + direction; if (target < 0 || target >= rules.length) return;
        sync(); [rules[index],rules[target]] = [rules[target],rules[index]]; orderDirty = true; drawList(); drawEditor();
      };
      const up = button('Move up', () => move(-1),'quiet'), down = button('Move down', () => move(1),'quiet');
      up.disabled = index === 0; down.disabled = index === rules.length - 1; priority.append(up,down); actions.append(priority);
      actions.append(button('Delete rule', () => gate.run(async () => {
        const id = selectedId;
        if (!await confirmAction(host,'Delete rule?','This rule will be removed from your lighting setup.','Delete',controller.signal)) return;
        const pendingOrder=orderDirty?rules.filter(rule=>rule.id!==id).map(rule=>rule.id):null;
        const confirmationError = await applyRules(sceneBase.scene.rules.filter(rule => rule.id !== id));
        if(pendingOrder){const positions=new Map(pendingOrder.map((value,index)=>[value,index]));rules.sort((first,second)=>positions.get(first.id)-positions.get(second.id));orderDirty=rules.some((rule,index)=>rule.id!==savedOrder[index]);}
        selectedId = rules[0]?.id; draft = copy(rules[0] || blank()); dirty = false;
        await refreshAfterWrite('Rule deleted.',() => { drawList(); drawEditor(); },confirmationError);
      }),'quiet danger'));
    }
    form.append(body,actions);
    const changed = () => { dirty = true; updateSummary(); };
    form.addEventListener('input',changed); form.addEventListener('change',changed);
    form.addEventListener('submit', event => {
      event.preventDefault(); if (!form.reportValidity()) return; sync();
      const error = validateRuleDraft(draft,specs); if (error) { status(error,true); return; }
      const payload = copy(draft), id = selectedId || crypto.randomUUID().replaceAll('-',''); payload.id=id;
      void gate.run(async () => {
        const index = rules.findIndex(rule => rule.id === id);
        const next=copy(rules);if(index<0)next.push(payload);else next[index]=payload;
        const confirmationError = await applyRules(next);
        selectedId = id; draft = copy(rules.find(rule=>rule.id===id)); dirty = orderDirty = false;
        await refreshAfterWrite('Rule applied.',() => { drawList(); drawEditor(); },confirmationError);
      });
    });
    editor.append(form); updateSummary(); body.scrollTop = scroll;
    if (focusKey) void (gate.pending || Promise.resolve()).then(() => { if (gate.active) focusControl(form,focusKey); });
  }
  async function load() {
    observedSceneKey = latestSceneKey;
    const results = await Promise.all([request('/api/rules/fields'),request('/api/rules/templates'),request('/api/scene')]);
    if (!gate.active) return;
    specs = results[0].fields; templates = results[1]; sceneBase = results[2];rules = copy(sceneBase.scene.rules); savedOrder = rules.map(rule => rule.id);
    selectedId = rules.find(rule => rule.id === selectedId)?.id ?? rules[0]?.id;
    draft = copy(rules.find(rule => rule.id === selectedId) || blank()); dirty = orderDirty = false;
    newRule.disabled = false; drawList(); drawEditor(); status('');
  }
  const reload = () => gate.run(async () => {
    if ((dirty || orderDirty) && !await confirmAction(host,'Discard changes?','Refresh will discard unapplied rule and priority changes.','Refresh',controller.signal)) return;
    try { await load(); } catch(error) { newRule.disabled = true; retryPanel(editor,'Rules could not be loaded. Your saved rules are unchanged.',reload); throw error; }
  });
  const newRule = button('New rule', () => gate.run(async () => {
    if (!await discard()) return;
    selectedId = null; draft = blank(); dirty = false; drawList(); drawEditor('rule-name');
  }),'primary'); newRule.disabled = true;
  header.append(button('Refresh rules',reload,'quiet'),newRule);
  editor.append(node('p','secondary-loading','Loading rules…')); void reload();
  const cleanup = () => { gate.close(); controller.abort(); host.inert = false; host.removeAttribute('aria-busy'); };
  cleanup.canLeave = async () => await gate.settle() && ((!dirty && !orderDirty) || await confirmAction(host,'Discard changes?','Your unapplied rule and priority changes will be discarded.','Discard',controller.signal));
  cleanup.hasChanges = () => dirty || orderDirty;
  cleanup.update = nextState => {
    updateRuleOutput(nextState);
    latestSceneKey = sceneKey(nextState);
    refreshCleanScene();
  };
  return cleanup;
}

function nameDialog(title, current, scenes, signal, unchanged) {
  return new Promise(resolve => {
    const dialog = node('dialog','dialog'), heading = node('h2','',title); heading.id = `profile-name-${crypto.randomUUID()}`;
    dialog.setAttribute('aria-labelledby',heading.id);
    const form = node('form'), name = input(current), error = node('p','inline-error'); name.maxLength = 64; name.required = true; name.setAttribute('aria-label','Profile name');
    const close = value => { dialog.close(); dialog.remove(); signal?.removeEventListener('abort',abort); resolve(value); };
    const abort = () => close(null), actions = node('div','dialog-footer'), submit = node('button','button primary',title); submit.type = 'submit';
    actions.append(button('Cancel',() => close(null)),submit);
    form.append(field('Profile name',name),error,actions); dialog.append(heading,form); document.body.append(dialog);
    form.addEventListener('submit',event => {
      event.preventDefault(); const value = name.value.trim();if(value===unchanged){close(value);return;}
      const invalid = profileNameError(value) || (Object.hasOwn(scenes,value) ? 'A profile with this name already exists.' : '');
      if (invalid) { error.textContent = invalid; name.setAttribute('aria-invalid','true'); name.focus(); return; } close(value);
    });
    dialog.addEventListener('cancel',event => { event.preventDefault(); close(null); }); signal?.addEventListener('abort',abort,{once:true});
    dialog.showModal(); name.focus(); name.select();
  });
}

export async function renderProfiles(host, {request, state, onChange, getState = () => state, effects = [], onApplyProfile}) {
  const controller = new AbortController();
  const {header,notice,status} = frame(host,'Profiles','Save a complete lighting setup and apply it when you want.');
  const gate = operationGate(host,status);
  let scenes = {}, selected, current = getState(), loaded = false;
  const previews=createProfilePreviews(current);
  let currentFingerprint=sceneFingerprint(current);
  const list = node('aside','secondary-list list'); list.setAttribute('aria-label','Saved profiles');
  const detail = node('section','secondary-editor panel'), split = node('div','secondary-split split'); split.append(list,detail); host.append(split,notice);
  const effectName = id => effects.find(effect => effect.id === id)?.name || effectById(id).name;
  async function refreshCurrent() { await onChange?.(); current = {...getState(),...await request('/api/state')};previews.update(current); }
  async function afterWrite(message) {
    let refreshed = true;
    try { await refreshCurrent(); } catch { refreshed = false; }
    if (!gate.active) return;
    try { draw(); } catch { refreshed = false; }
    status(refreshed ? message : `${message} Status could not be refreshed. Use Refresh profiles to check it.`, !refreshed);
  }
  function draw() {
    if (!gate.active) return;
    previews.clear();list.replaceChildren(); detail.replaceChildren();
    currentFingerprint=sceneFingerprint(current);
    const match = matchingProfile(scenes,current);
    list.append(node('p','profile-state',match ? `Active: ${match}` : Object.keys(scenes).length ? 'Current setup: Modified' : 'Current setup'));
    for (const key of Object.keys(scenes).sort((a,b) => a.localeCompare(b))) {
      const scene = scenes[key], item = button('',() => { selected = key; draw(); },`scene-item ${key === selected ? 'selected' : ''}`);
      item.append(previews.add(scene,key===match),node('span','item-name',key),node('span','muted',`${scene.brightness ?? 100}% brightness${key === match ? ' · Active' : ''}`));
      item.setAttribute('aria-pressed',String(key === selected)); list.append(item);
    }
    if (!selected || !scenes[selected]) { detail.append(node('h2','','Save your first profile'),node('p','muted','Choose Save new profile to keep your current colors, effects, port groups, and rules.')); return; }
    const scene = scenes[selected], heading = node('div','profile-heading');
    heading.append(node('h2','',selected),node('span','profile-state',selected === match ? 'Active profile' : 'Saved profile')); detail.append(heading,previews.add(scene,selected===match));
    const layoutNotice = profileLayoutNotice(scene,current);
    if (layoutNotice) detail.append(node('p','help-text',layoutNotice));
    const actions = node('div','row profile-actions');
    actions.append(button('Apply profile',() => gate.run(async () => {
      const target = selected;
      if(onApplyProfile){await onApplyProfile(target);if(!gate.active)return;current=getState();previews.update(current);draw();status('Profile applied.');}
      else {
        const before=await request('/api/scene');
        await request('/api/scene','PUT',{scene:copy(scenes[target]),expected_revision:before.revision,preserve_unchanged:true,confirm_output:true});
        await afterWrite('Profile applied.');
      }
    }),'primary'));
    const updateProfile=button('Update from current',() => gate.run(async () => {
      const target = selected;
      if (!await confirmAction(host,'Update profile?',`Replace “${target}” with your current lighting setup?`,'Update',controller.signal)) return;
      scenes = await request('/api/presets','POST',{name:target}); await afterWrite('Profile updated.');
    }));
    if(profileNameError(selected)){updateProfile.disabled=true;updateProfile.title='Rename this profile before updating it.';}
    actions.append(updateProfile);
    actions.append(button('Rename',() => gate.run(async () => {
      const target = selected, name = await nameDialog('Rename profile',target,scenes,controller.signal,target); if (!name) return;
      if(name===target){status('Profile name unchanged.');return;}
      scenes = await request('/api/presets/action','POST',{action:'rename',name:target,target:name}); selected = name; await afterWrite('Profile renamed.');
    }),'quiet'),button('Duplicate',() => gate.run(async () => {
      const target = selected, name = await nameDialog('Duplicate profile',`${target.slice(0,59)} copy`,scenes,controller.signal); if (!name) return;
      scenes = await request('/api/presets/action','POST',{action:'duplicate',name:target,target:name}); selected = name; await afterWrite('Profile duplicated.');
    }),'quiet'),button('Delete',() => gate.run(async () => {
      const target = selected;
      if (!await confirmAction(host,'Delete profile?',`Remove “${target}” from your saved profiles? Your current lighting keeps running.`,'Delete',controller.signal)) return;
      scenes = await request('/api/presets/action','POST',{action:'delete',name:target}); selected = Object.keys(scenes)[0]; await afterWrite('Profile deleted.');
    }),'quiet danger')); detail.append(actions);
    const summary = node('div','scene-summary'); summary.append(node('h3','','Ethernet groups'));
    for (const group of scene.zones || []) {
      const row = node('div','scene-group'); row.append(node('strong','',group.name),node('p','muted',`${effectName(group.effect?.id || 'static')} · Ports ${(group.ports || []).join(', ')}`)); summary.append(row);
    }
    const overrides = Object.entries(scene.overrides || {});
    if (overrides.length) {
      summary.append(node('h3','','Individual port colors'));
      for (const [port,override] of overrides) {
        const row = node('div','row'), swatch = node('i','rule-swatch'); swatch.style.background = override.color;
        row.append(swatch,node('span','muted',`Port ${port} · ${override.color.toUpperCase()} · ${override.brightness == null ? 'Default brightness' : `${override.brightness}% brightness`}`)); summary.append(row);
      }
    }
    const sfp = profileSfpSummary(scene);
    if (sfp.length) {
      summary.append(node('h3','','SFP lights'));
      for (const {port,label} of sfp) summary.append(node('p','muted',`SFP ${port} · ${label}`));
    }
    const count = scene.rules?.length || 0; summary.append(node('p','muted',`${count} ${count === 1 ? 'rule' : 'rules'} saved with this profile.`)); detail.append(summary);
  }
  const reload = () => gate.run(async () => {
    try {
      const results = await Promise.all([request('/api/presets'),request('/api/state')]); if (!gate.active) return;
      scenes = results[0]; current = {...getState(),...results[1]};previews.update(current); if (!Object.hasOwn(scenes,selected)) selected = Object.keys(scenes)[0];
      loaded = true; saveNew.disabled = false; draw(); status('');
    } catch(error) { if (!loaded) retryPanel(detail,'Profiles could not be loaded. Your saved profiles are unchanged.',reload); throw error; }
  });
  const saveNew = button('Save new profile',() => gate.run(async () => {
    const name = await nameDialog('Save profile','',scenes,controller.signal); if (!name) return;
    scenes = await request('/api/presets','POST',{name}); selected = name; await afterWrite('Profile saved.');
  }),'primary'); saveNew.disabled = true;
  header.append(button('Refresh profiles',reload,'quiet'),saveNew); detail.append(node('p','secondary-loading','Loading profiles…')); void reload();
  const cleanup = () => { previews.dispose();gate.close(); controller.abort(); host.inert = false; host.removeAttribute('aria-busy'); };
  cleanup.update=next=>{if(!gate.active)return;current=next;previews.update(current);if(loaded&&sceneFingerprint(current)!==currentFingerprint)draw();};
  cleanup.canLeave = () => gate.settle(); return cleanup;
}

export {renderProfiles as renderScenes};

export async function renderSettings(host, {request, onHelp, onSessionChange, onCloseWorkspace, onChange}) {
  const {header,notice,status} = frame(host,'Settings','Connection and application details.');
  const gate = operationGate(host,status), content = node('div','settings-content panel'), controllerHost = node('div','controller-settings');
  const lifecycle = new AbortController();
  const health = section('Switch health','Lighting command activity, CPU, memory, temperature, and storage readings.');
  const healthStatus = node('p','help-text'), healthDetails = node('dl','settings-details');
  health.append(healthStatus,healthDetails);
  let healthKey, lastStats, statsAt = 0, healthPaintedAt = -Infinity;
  const updateHealth = (state, unavailable = false) => {
    const now = performance.now();
    if (state.stats !== lastStats) {lastStats = state.stats; statsAt = now;}
    if (!unavailable && now - healthPaintedAt < 250) return;
    healthPaintedAt = now;
    const sampleAge=key=>{const age=lastStats?.[key];return typeof age==='number'&&Number.isFinite(age)?age+now-statsAt:age;};
    const agedState = {...state,stats:{...lastStats,cpu_sample_age_ms:sampleAge('cpu_sample_age_ms'),health_sample_age_ms:sampleAge('health_sample_age_ms')}};
    const value = switchHealth(agedState,unavailable), key = JSON.stringify(value);
    if (key === healthKey) return;
    healthKey = key; healthStatus.textContent = value.status;
    healthDetails.replaceChildren();
    for (const [label,reading] of value.rows) {
      const term = node('dt','muted',label);
      if (label === 'Load average') term.title = 'Average running and waiting tasks; this is not a CPU percentage.';
      healthDetails.append(term,node('dd','',reading));
    }
  };
  const about = node('section','settings-about panel');
  about.append(node('h2','','Etherlighting Evolved'),node('p','muted','Open-source UniFi Etherlighting controller.'),node('p','muted','Unofficial community project. Not affiliated with Ubiquiti.'));
  if (onHelp) about.append(button('Open help',onHelp,'quiet'));
  let controllerCleanup, switchState;
  const privateHost=node('div','private-access-settings');
  host.append(content,health,controllerHost,privateHost,about,notice);
  const privateCleanup=renderPrivateAccess(privateHost,request,{signal:lifecycle.signal});
  const reload = () => gate.run(async () => {
    try {
      const [session, state] = await Promise.all([request('/api/session'),request('/api/state')]); if (!gate.active) return;
      health.hidden = Boolean(session.simulated); updateHealth(state);
      content.replaceChildren();
      switchState = node('p','connection-state');
      showConnectionStatus(switchState,switchConnectionStatus(session));
      content.append(node('h2','','Connection'),switchState);
      if (session.simulated) content.append(node('p','muted','Use this preview to review the lighting workspace, effects, rules, and profiles.'));
      else {
        const handoff=button('Handoff to UniFi',()=>gate.run(async()=>{
          const unsaved=controllerCleanup?.hasChanges?.()||privateCleanup.hasChanges();
          const message='Stop EE lighting and its switch helpers, close the SSH connection, and return lighting control to UniFi. Saved logins, profiles, and your lighting setup are kept. EE will wait until you connect again.'+(unsaved?' Unapplied connection changes will be discarded.':'');
          if(!await confirmAction(host,'Handoff to UniFi?',message,'Handoff to UniFi',lifecycle.signal)||!gate.active)return;
          handoff.textContent='Handing off…';status('Handing lighting back to UniFi…');
          try{
            const result=await requestHandoff(request,()=>gate.active);if(!result)return;
            status(result.error||'Lighting returned to UniFi.',Boolean(result.error));
            if(result.session?.logged_in===false)await onSessionChange?.(result.session,result.error?{message:result.error,error:true}:undefined);
          }finally{if(gate.active)handoff.textContent='Handoff to UniFi';}
        }));
        content.append(node('p','help-text','Return lighting control to UniFi and keep your EE setup for next time.'),handoff);
        if (session.has_saved_login) content.append(button('Forget saved switch login',()=>gate.run(async()=>{
          if (!await confirmAction(host,'Forget saved switch login?','Remove the saved SSH login from this PC. The current switch connection stays open.','Forget login',lifecycle.signal)||!gate.active) return;
          await request('/api/session/forget','POST');
          content.querySelector('[data-forget-login]')?.remove();
          status('Saved switch login removed.');
        }),'quiet'));
        const forget = [...content.querySelectorAll('button')].find(item=>item.textContent==='Forget saved switch login');
        if (forget) forget.dataset.forgetLogin = 'true';
        if(!controllerCleanup)controllerCleanup=renderControllerConnection(controllerHost,{request,onChanged:()=>onChange?.()});
      }
      const details = node('dl','settings-details');
      for (const [label,value] of [['Lighting',state.mode === 'released' ? 'Controlled by UniFi' : 'Controlled by EE'],['Ethernet ports',lightingPorts(state).copper.length],['SFP lights',lightingPorts(state).sfp.length]]) details.append(node('dt','muted',label),node('dd','',value));
      content.append(details); status('');
      if (onCloseWorkspace) content.append(button('Close web session',()=>gate.run(async()=>{
        if(await confirmAction(host,'Close web session?','This releases the switch connection and closes the web service. Your saved setup is preserved.','Close web session',lifecycle.signal)&&gate.active)await onCloseWorkspace();
      }),'quiet'));
    } catch(error) { retryPanel(content,'Connection details could not be loaded.',reload); throw error; }
  });
  header.append(button('Refresh connection',reload)); content.append(node('p','secondary-loading','Loading connection details…')); void reload();
  const cleanup = () => { lifecycle.abort();controllerCleanup?.();privateCleanup();gate.close(); host.inert = false; host.removeAttribute('aria-busy'); };
  cleanup.update = (state,session,unavailable = false) => {
    if (!gate.active) return;
    if (switchState) showConnectionStatus(switchState,switchConnectionStatus(session,unavailable));
    updateHealth(state,unavailable);
    if (state.controller) controllerCleanup?.update?.(state.controller,unavailable);
  };
  cleanup.hasChanges=()=>Boolean(controllerCleanup?.hasChanges?.()||privateCleanup.hasChanges());
  cleanup.canLeave=async()=>{await gate.settle();await privateCleanup.canLeave();if(!gate.active)return false;if(cleanup.hasChanges()&&!await confirmAction(host,'Leave connection settings?','Your unapplied connection changes will be discarded.','Leave',lifecycle.signal))return false;return gate.active&&(await controllerCleanup?.canLeave?.()??true);};return cleanup;
}

export function renderHelp(host) {
  frame(host,'Help','Start with your ports, preview a change, then apply it.');
  const chapters = [
    ['Change a port’s lighting', [
      'Open Lighting and select a port. All ports selects every Ethernet and SFP port. Ethernet and SFP select those ports together.',
      'Turn on Select multiple to add or remove ports with a click or tap. Shift-click selects a range; Ctrl-click on Windows or Command-click on Mac adds individual ports.',
      'In Port controls, choose a fixed color or an effect, then adjust its settings. Apply changes sends the changes; Discard returns to your applied settings.',
      'Copy appearance keeps a selected port’s settings. Select compatible ports and choose Paste appearance to preview the same look before applying it.'
    ]],
    ['Preview and apply changes', [
      'Editing colors, brightness, or effect settings creates a preview. The Apply bar identifies changes waiting to be sent.',
      'The port map previews those changes. Show applied lets you compare them with the applied lighting; Show preview returns to your edits.',
      'When you leave an editor with changes, choose whether to apply or discard them. Profiles apply a saved setup with their own Apply profile button.',
      'Undo last apply on Lighting restores the setup from before your most recent Apply changes on that page. If the setup has since changed elsewhere, refresh and review the current lighting first.',
      'Refreshing the page restores your selection and pending lighting draft. Review the preview before applying it.'
    ]],
    ['Brightness and colors', [
      'Default brightness sets the brightness for effects and SFP lighting.',
      'Fixed Ethernet colors and rules can set their own brightness instead of following the default. Adjust an SFP color’s brightness beside its color picker.',
      'Each effect color’s brightness adjusts that color within the effect.',
      'A fixed port color stays on that port. Follow group effect restores the port’s effect. Color swatches open the color picker; enter a hex value for an exact color.',
      'Save favorite colors in the picker for quick access. Named palettes keep a set of colors together for reuse.'
    ]],
    ['Choose an effect', [
      'Effects displays an animated gallery using your selected colors. Choose an Ethernet or SFP effect to preview it on compatible selected ports.',
      'Favorite effects are easy to find again. Recent effects keep the effects you have chosen close at hand.',
      'Speed controls how quickly an effect moves or completes a cycle. Color controls set its primary and secondary colors.',
      'Random color options vary the colors you enable. Leave the primary color fixed and randomize the background for a multicolor Marquee.',
      'PoE Usage blends between Low power and High power using each port’s delivered watts. Logarithmic scaling makes low-power differences visible; Fade speed controls how quickly colors follow a change.'
    ]],
    ['Understand the port map', [
      'The illuminated socket shows the lighting color. The link-speed legend explains the dot beneath each port.',
      'The uplink symbol identifies the port connecting to the rest of your network. PoE symbols and watts describe power supplied by a port.',
      'SFP lights have their own color and animation controls. Link status lets the switch manage the indicator.'
    ]],
    ['Sync SFP lights', [
      'Select an SFP port, then choose a sync mode in Port controls. Together shares one effect, color palette, and timing across both indicators.',
      'Follow Ethernet port tracks a chosen Ethernet port’s color and brightness with smooth fades on both SFP lights. Targeted SFP automations can override it.',
      'Independent returns each indicator to its separate controls. Review the preview, then choose Apply changes. Sync settings are included in saved profiles.'
    ]],
    ['Create an automation', [
      'Create a rule, choose when it should match, select the ports, then choose what they should show.',
      'Use All ports, Ethernet, or SFP to select targets quickly. Rules are checked from top to bottom; the first matching rule wins.',
      'Apply rule saves its conditions and appearance. Apply priority saves changes to the rule order. Time rules follow the EE computer’s local time.',
      'For an SFP alert, select the Anomaly template, select the SFP targets, choose the alert color, and apply the rule.'
    ]],
    ['Save and reuse profiles', [
      'Save new profile captures your applied colors, effects, groups, individual port colors, SFP lights, brightness, and rules.',
      'Select a saved profile to review it, then choose Apply profile. The Active label identifies a profile matching the current setup.',
      'Update from current replaces the selected saved setup after confirmation. Rename and Duplicate organize profiles without changing your lighting.'
    ]],
    ['Connection and recovery', [
      'Settings shows the current connection and which application controls the lighting. Refresh connection checks again.',
      'Choose Handoff to UniFi in Settings to stop EE lighting and return control to UniFi. Your saved logins, profiles, and lighting setup are kept. EE waits until you connect again.',
      'When you want to resume EE, choose Connect with saved login or enter your SSH login. Choose Retry handoff when it is offered. If EE requests a recovery review, keep it disconnected until that review is complete.',
      'Connect UniFi Network in Settings to display device names from your controller. Enter its HTTPS address and account details. If prompted, verify the controller certificate fingerprint before approving it.',
      'To reconnect when EE opens, check Reconnect controller automatically and choose Apply setting. Turning it off and applying removes the saved controller login while keeping the current connection.',
      'If applying lighting fails, keep your changes and retry after the connection returns. A saved profile gives you a known setup to apply.',
      'The Demo badge identifies simulated ports used to explore the interface.'
    ]]
  ];
  const content = node('div','help-grid');
  chapters.forEach(([title,paragraphs],index) => {
    const card = node('details','help-card'); card.open = index === 0;
    card.append(node('summary','',title)); const steps = node('div','help-steps');
    paragraphs.forEach(text => steps.append(node('p','',text))); card.append(steps); content.append(card);
  }); host.append(content); return () => {};
}
