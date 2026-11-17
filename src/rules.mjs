import { dim,clamp } from './color.mjs';
export const RULE_RATE_LIMITS = {pulse:{min:.05,max:.33,default:.25},blink:{min:.05,max:1,default:.5},flash:{min:.05,max:.5,default:.5}};
export function ruleRate(style,value) {
  const limits = RULE_RATE_LIMITS[style] || {min:.05,max:1,default:.5};
  return clamp(Number.isFinite(value) ? value : limits.default,limits.min,limits.max);
}
const minutes = value => {const [h,m] = String(value).split(':').map(Number); return h * 60 + m;};
export function conditionMatches(condition,facts,date) {
  const {field,op,value} = condition; let actual = facts[field];
  if (field === 'time') actual = date.getHours() * 60 + date.getMinutes();
  if (field === 'weekday') actual = ['sun','mon','tue','wed','thu','fri','sat'][date.getDay()];
  if (actual === null || actual === undefined) return false;
  if (field === 'time') {const [a,b] = value.map(minutes); return a <= b ? actual >= a && actual < b : actual >= a || actual < b;}
  if (op === 'in') return value.includes(actual);
  if (op === 'not_in') return !value.includes(actual);
  if (op === 'between') return actual >= value[0] && actual <= value[1];
  return {is:() => actual === value,is_not:() => actual !== value,eq:() => actual === value,ne:() => actual !== value,gt:() => actual > value,ge:() => actual >= value,lt:() => actual < value,le:() => actual <= value}[op]?.() || false;
}
export function matchingRule(rules,port,date) {
  return rules.find(r => r.enabled && (!r.ports?.length || r.ports.includes(port.id)) && (!r.conditions.length ||
    (r.match === 'any' ? r.conditions.some(c => conditionMatches(c,port.facts,date)) : r.conditions.every(c => conditionMatches(c,port.facts,date)))));
}
export function matchingSfpRule(rules,item,date = new Date()) {
  if (item.mode === 'off') return null;
  const facts = {...item.facts,link:item.link,anomaly:item.anomaly};
  const rule = matchingRule(rules.filter(rule => rule.ports?.includes(item.port)),{id:item.port,facts},date);
  return rule && (item.mode === 'color' || rule.look.style !== 'effect') ? rule : null;
}
export function renderLook(look,underneath,t) {
  const phase = (t * ruleRate(look.style,look.rate)) % 1,color = look.color || '#FFFFFF';
  if (look.style === 'effect') return underneath;
  if (look.style === 'blink') return phase < .5 ? color : '#000000';
  if (look.style === 'flash') return phase < .2 ? color : '#000000';
  if (look.style === 'pulse') return dim(color,.15 + .85 * (.5 - .5 * Math.cos(phase * Math.PI * 2)));
  return color;
}
