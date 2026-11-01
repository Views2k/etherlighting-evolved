import {dim, rgb} from './effect-renderer.mjs';
import {matchingRule, matchingSfpRule} from '../src/rules.mjs';

const validRgb = value => Array.isArray(value) && value.length === 3 && value.every(Number.isFinite);
const percent = value => Math.max(0, Math.min(100, Number(value))) / 100;

export function portDisplayRgb(port, gamma = 2.2) {
  if (Object.hasOwn(port, 'display_rgb')) return validRgb(port.display_rgb) ? rgb(port.display_rgb) : [0, 0, 0];
  if (port.mode === 'off' || port.mode === 'unmanaged' && !port.rule) return [0, 0, 0];
  const encoded = port.preview ?? port.output?.color;
  if (!/^#[\da-f]{6}$/i.test(encoded ?? '')) return [0, 0, 0];
  const curve = Number.isFinite(gamma) && gamma > 0 ? gamma : 2.2;
  const level = port.preview == null ? percent(port.output?.level ?? 100) : 1;
  return rgb(encoded).map(channel => 255 * (channel / 255 * level) ** (1 / curve));
}

export function manualDisplayRgb(color, brightness, inheritedBrightness = 100) {
  return dim(color, percent(brightness ?? inheritedBrightness));
}

export function masterDisplayRgb(port, brightness, previousBrightness, rules = [], gamma = 2.2, date = new Date()) {
  const shown = portDisplayRgb(port, gamma);
  const sfp = ['color', 'off', 'unmanaged'].includes(port.mode);
  const rule = sfp ? matchingSfpRule(rules, port, date)
    : matchingRule(rules, {id: port.port, facts: port.facts || {}}, date);
  if (port.override?.brightness != null || rule?.look.brightness != null) return shown;
  if (sfp && (port.mode === 'off' || port.mode === 'unmanaged' && !rule
    || Object.hasOwn(port, 'display_rgb') && port.display_rgb === null)) return shown;
  if (previousBrightness > 0) return rgb(shown.map(channel => channel * brightness / previousBrightness));
  const scale = sfp ? percent(port.brightness ?? 100) * percent(port.color_brightness ?? 100) : 1;
  const color = rule?.look.style !== 'effect' && rule?.look.color ? rule.look.color : port.color;
  return dim(color || '#000000', percent(brightness) * scale);
}
