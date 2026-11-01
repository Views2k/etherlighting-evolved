import { FIELDS,fieldByKey,OPERATORS,RULE_RATE_LIMITS,ruleRate,rulePortLabel } from './rules.mjs';
import { esc,button } from './views.mjs';
import { icon } from './icons.mjs';
export function initialCondition(key) {
  const f = fieldByKey(key);
  return {field:key,op:['time','number'].includes(f.type) ? (f.type === 'time' ? 'between' : 'ge') : f.type === 'weekday' ? 'in' : 'is',
    value:f.type === 'time' ? ['22:00','07:00'] : f.type === 'weekday' ? ['mon','tue','wed','thu','fri'] : f.type === 'bool' ? true : f.type === 'number' ? 1000 : f.choices[0]};
}
function conditionControl(c,i) {
  const field = fieldByKey(c.field),prefix = `condition-${i}`;
  const ops = field.type === 'number' ? ['ge','gt','le','lt','eq','ne','between'] : field.type === 'choice' ? ['is','is_not','in','not_in'] : [c.op];
  let value;
  if (field.type === 'bool') value = `<select data-condition-value="${i}" aria-label="Condition value"><option value="true" ${c.value === true ? 'selected' : ''}>${field.trueLabel || 'Yes'}</option><option value="false" ${c.value === false ? 'selected' : ''}>${field.falseLabel || 'No'}</option></select>`;
  else if (field.type === 'choice' && ['in','not_in'].includes(c.op)) value = `<div class="choice-checks">${field.choices.map(v => `<label class="check-label"><input type="checkbox" data-condition-choice="${i}" value="${v}" ${c.value.includes(v) ? 'checked' : ''}>${v === 'off' ? 'No PoE' : v.startsWith('poe') ? v.toUpperCase() : v}</label>`).join('')}</div>`;
  else if (field.type === 'choice') value = `<select data-condition-value="${i}" aria-label="Condition value">${field.choices.map(v => `<option value="${v}" ${c.value === v ? 'selected' : ''}>${v === 'off' ? 'No PoE' : v.startsWith('poe') ? v.toUpperCase() : v}</option>`).join('')}</select>`;
  else if (field.type === 'weekday') value = `<div class="day-picker">${field.choices.map(d => `<label><input type="checkbox" data-condition-day="${i}" value="${d}" ${c.value.includes(d) ? 'checked' : ''}><span>${d[0].toUpperCase() + d.slice(1,2)}</span></label>`).join('')}</div>`;
  else if (field.type === 'time' || c.op === 'between') value = `<div class="form-grid"><input type="${field.type === 'time' ? 'time' : 'number'}" data-condition-start="${i}" value="${esc(c.value[0])}" aria-label="Start" required><input type="${field.type === 'time' ? 'time' : 'number'}" data-condition-end="${i}" value="${esc(c.value[1])}" aria-label="End" required></div>`;
  else value = `<input type="number" data-condition-value="${i}" value="${esc(c.value)}" min="0" step="any" aria-label="Threshold ${field.unit || ''}" required>`;
  return `<div class="condition-card"><div class="condition-heading"><span>WHEN ${i + 1}</span><button type="button" class="icon-button" data-action="remove-condition" data-index="${i}" aria-label="Remove condition ${i + 1}">${icon('close')}</button></div><div class="form-grid"><label class="field"><span>Property</span><select id="${prefix}-field" data-condition-field="${i}">${FIELDS.map(f => `<option value="${f.key}" ${f.key === c.field ? 'selected' : ''}>${f.label}</option>`).join('')}</select></label><label class="field"><span>Condition${field.unit ? ` · ${field.unit}` : ''}</span><select data-condition-op="${i}">${ops.map(op => `<option value="${op}" ${op === c.op ? 'selected' : ''}>${OPERATORS[op]}</option>`).join('')}</select></label></div>${value}</div>`;
}
export function ruleScope(draft,state) {
  if (!draft.ports?.length) return 'all';
  const sfp = (state.sfp || []).map(item => item.port);
  if (draft.ports.length === 1 && sfp.includes(draft.ports[0])) return `sfp-${draft.ports[0]}`;
  if (sfp.length === 2 && draft.ports.length === 2 && sfp.every(port => draft.ports.includes(port))) return 'sfp-all';
  return 'selected';
}
export function rulePortsForScope(scope,draft,state) {
  const sfp = (state.sfp || []).map(item => item.port);
  if (scope === 'all') return null;
  if (scope === 'sfp-all') return [...sfp];
  if (scope.startsWith('sfp-')) return sfp.filter(port => scope === `sfp-${port}`);
  return ruleScope(draft,state) === 'selected' ? [...draft.ports] : [...state.selected];
}
export function ruleScopeControl(draft,state) {
  const scope = ruleScope(draft,state),selected = rulePortsForScope('selected',draft,state);
  const sfp = (state.sfp || []).map(item => item.port);
  const options = [['all','All copper ports'],['selected',`Selected ports (${selected.length ? rulePortLabel(selected,sfp) : 'none'})`],
    ...sfp.map(port => [`sfp-${port}`,`SFP ${port}`]),...(sfp.length === 2 ? [['sfp-all','Both SFP ports']] : [])];
  return `<select name="scope">${options.map(([value,label]) => `<option value="${value}" ${scope === value ? 'selected' : ''}>${esc(label)}</option>`).join('')}</select>`;
}
export function ruleEditor(draft,state) {
  const rate = RULE_RATE_LIMITS[draft.look.style];
  const sfpScope = draft.ports?.some(port => state.sfp?.some(item => item.port === port));
  const scopeHelp = sfpScope ? '<p class="inspector-hint">SFP lights return to their regular lighting when the condition clears. Off keeps the light off.</p>' : '';
  return `<div class="dialog-head"><h2>${state.rules.some(r => r.id === draft.id) ? 'Edit rule' : 'New rule'}</h2><button class="icon-button" data-action="close-dialog" aria-label="Close">${icon('close')}</button></div><form id="rule-form"><label class="field"><span>Name</span><input name="ruleName" type="text" value="${esc(draft.name)}" maxlength="60" required></label><div class="form-grid"><label class="field"><span>Apply to</span>${ruleScopeControl(draft,state)}</label><label class="field"><span>Match</span><select name="match"><option value="all" ${draft.match === 'all' ? 'selected' : ''}>Every condition</option><option value="any" ${draft.match === 'any' ? 'selected' : ''}>Any condition</option></select></label></div>${scopeHelp}${draft.conditions.map(conditionControl).join('')}<button type="button" class="text-button" data-action="add-condition">${icon('plus')}Add condition</button><div class="control-section"><div class="form-grid"><label class="field"><span>Then show</span><select name="style">${[['solid','A solid color'],['pulse','A gentle pulse'],['blink','A blinking color'],['flash','A quick flash'],['effect','Keep the effect']].map(([value,label]) => `<option value="${value}" ${draft.look.style === value ? 'selected' : ''}>${label}</option>`).join('')}</select></label><label class="field"><span>Color</span><input name="color" type="color" value="${draft.look.color}" aria-label="Rule color"></label></div><div class="form-grid"><div class="rule-brightness"><label class="field"><span>Brightness · %</span><input name="brightness" type="number" min="0" max="100" value="${draft.look.brightness ?? state.brightness}" ${draft.look.brightness == null ? 'disabled' : ''} required></label><label class="check-label"><input name="inheritBrightness" type="checkbox" ${draft.look.brightness == null ? 'checked' : ''}>Use master brightness</label></div><label class="field" ${rate ? '' : 'hidden'}><span>Speed · cycles/s</span><input name="rate" type="number" min="${rate?.min || .05}" max="${rate?.max || 1}" step="${draft.look.style === 'pulse' ? .01 : .05}" value="${ruleRate(draft.look.style,draft.look.rate)}"></label></div><p class="inspector-hint">Schedules use the controller’s local time. Overnight windows are supported.</p></div><div id="rule-error" role="alert" hidden></div><div class="dialog-actions"><button type="button" class="button ghost" data-action="close-dialog">Cancel</button><button class="button primary" type="submit">Save rule ${icon('check')}</button></div></form>`;
}
export function readRuleForm(dialog,draft,state) {
  const form = dialog.querySelector('#rule-form');
  if (!form) return draft;
  const fields = new FormData(form),result = structuredClone(draft);
  result.name = String(fields.get('ruleName')).trim(); result.match = fields.get('match');
  result.ports = rulePortsForScope(String(fields.get('scope')),draft,state);
  result.look = readRuleLook(fields);
  result.conditions = draft.conditions.map((c,i) => {
    const field = fieldByKey(c.field),op = form.querySelector(`[data-condition-op="${i}"]`).value;
    let value;
    if (field.type === 'weekday') value = [...form.querySelectorAll(`[data-condition-day="${i}"]:checked`)].map(input => input.value);
    else if (field.type === 'choice' && ['in','not_in'].includes(c.op)) value = [...form.querySelectorAll(`[data-condition-choice="${i}"]:checked`)].map(input => input.value);
    else if (field.type === 'time' || c.op === 'between') {value = [form.querySelector(`[data-condition-start="${i}"]`).value,form.querySelector(`[data-condition-end="${i}"]`).value];if (field.type === 'number') value = value.map(Number);}
    else {value = form.querySelector(`[data-condition-value="${i}"]`).value;if (field.type === 'bool') value = value === 'true';if (field.type === 'number') value = Number(value);}
    return {field:c.field,op,value};
  });
  return result;
}
export function readRuleLook(fields) {
  return {color:fields.get('color'),style:fields.get('style'),brightness:fields.get('inheritBrightness') === 'on' ? null : Number(fields.get('brightness')),rate:ruleRate(fields.get('style'),Number(fields.get('rate')))};
}
