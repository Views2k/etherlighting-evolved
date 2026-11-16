import {hostScheduleDate,scheduleClockLabel} from './schedule-clock.mjs';
import {lightingPorts} from './port-topology.mjs';
import {field,node,randomId} from './dom.mjs';
const display=value=>value===null||value===undefined?'Not reported':Array.isArray(value)?value.join(' to '):typeof value==='boolean'?value?'Yes':'No':String(value);
const operators={is:'is',is_not:'is not',in:'is any of',not_in:'is none of',gt:'is above',ge:'is at least',lt:'is below',le:'is at most',eq:'equals',ne:'does not equal',between:'is between'};
const units={speed:' Mbps',traffic:' Mbps',poe_power:' W'};
function conditionValue(condition,value,actual=false){
  if(value===null||value===undefined)return 'Not reported';
  if(condition.field==='time'&&actual&&typeof value==='number')return `${String(Math.floor(value/60)).padStart(2,'0')}:${String(value%60).padStart(2,'0')}`;
  if(condition.field==='weekday')return (Array.isArray(value)?value:[value]).map(day=>String(day)[0].toUpperCase()+String(day).slice(1)).join(', ');
  return display(value)+(units[condition.field]||'');
}
export function openRuleTester({request,state,rules,signal}) {
  const dialog=node('dialog','dialog rule-test-dialog'),title=node('h2','','Test rules'),form=node('form'),port=node('select'),at=node('input'),output=node('div','rule-test-result'),error=node('p','inline-error');
  title.id=`rule-test-${randomId()}`;dialog.setAttribute('aria-labelledby',title.id);at.type='datetime-local';at.required=true;
  const clock=hostScheduleDate(state);at.value=Number.isFinite(clock.getTime())?new Date(clock.getTime()).toISOString().slice(0,16):'';
  const topology=lightingPorts(state);for(const id of [...topology.copper,...topology.sfp]){const option=node('option','',`${topology.sfp.includes(id)?'SFP':'Port'} ${id}`);option.value=id;port.append(option);}
  const fields=node('div','form-grid');fields.append(field('Port',port),field(`Date and time · ${scheduleClockLabel(state)}`,at));
  const actions=node('div','dialog-actions'),test=node('button','button primary','Test rules'),close=node('button','button','Done');test.type='submit';close.type='button';actions.append(test,close);form.append(fields,error,output,actions);dialog.append(title,node('p','help-text','Evaluate these rule edits and their priority using the latest port readings.'),form);document.body.append(dialog);
  let active=true;const previous=document.activeElement;const dispose=()=>{if(!active)return;active=false;signal?.removeEventListener('abort',dispose);dialog.close();dialog.remove();if(previous?.isConnected)previous.focus({preventScroll:true});};close.onclick=dispose;dialog.addEventListener('cancel',event=>{event.preventDefault();dispose();});signal?.addEventListener('abort',dispose,{once:true});
  form.addEventListener('submit',async event=>{event.preventDefault();if(!form.reportValidity())return;test.disabled=true;error.textContent='';try{const result=await request('/api/rules/test','POST',{rules,port:Number(port.value),at:at.value});if(!active)return;output.replaceChildren();const sources={individual_color:'Individual port color',rule:result.winner?.name||'Rule',port_effect:'Port effect',sfp_setting:'SFP settings'};output.append(node('h3','',`Displayed lighting: ${sources[result.source]}`));if(result.source==='individual_color'&&result.winner)output.append(node('p','muted',`Matching rule: ${result.winner.name}. The individual port color takes priority.`));if(!result.output_running)output.append(node('p','muted','EE output is currently stopped.'));for(const [index,rule] of result.rules.entries()){const row=node('section','automation-section');row.append(node('strong','',`${index+1}. ${rule.name}`),node('p','muted',!rule.enabled?'Disabled':!rule.in_scope?'Outside selected ports':rule.matches?'Conditions match':'Conditions do not match'));for(const condition of rule.conditions)row.append(node('p','rule-condition-result',`${condition.passes?'✓':'–'} ${condition.label}: ${conditionValue(condition,condition.actual,true)} · ${operators[condition.op]||condition.op} ${conditionValue(condition,condition.value)}`));output.append(row);}}catch(exception){if(active)error.textContent=exception.message;}finally{if(active)test.disabled=false;}});
  dialog.showModal();port.focus();form.requestSubmit();return dispose;
}
