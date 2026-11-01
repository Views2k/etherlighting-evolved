import {createEffectRenderer} from '../src/effects.mjs';
import {clamp, hexToRgb as parseHex} from '../src/color.mjs';

export const rgb = color => Array.isArray(color) ? color.map(channel => clamp(channel,0,255)) : parseHex(color);
export const dim = (color, amount) => rgb(color).map(channel => channel * clamp(amount));
export const mix = (first, second, amount) => {
  const start = rgb(first), end = rgb(second), weight = clamp(amount);
  return start.map((channel,index) => channel + (end[index] - channel) * weight);
};
export function hsv(hue, saturation, value) {
  const h = ((hue % 360) + 360) % 360, s = clamp(saturation), v = clamp(value);
  const chroma = v * s, secondary = chroma * (1 - Math.abs((h / 60) % 2 - 1)), offset = v - chroma;
  const channels = h < 60 ? [chroma,secondary,0] : h < 120 ? [secondary,chroma,0] : h < 180 ? [0,chroma,secondary] : h < 240 ? [0,secondary,chroma] : h < 300 ? [secondary,0,chroma] : [chroma,0,secondary];
  return channels.map(channel => (channel + offset) * 255);
}
export {hsv as hsvToRgb};

const renderer = createEffectRenderer({clamp, mix, dim, hexToRgb:rgb, rgbToHex:rgb, hsvToHex:hsv});
export const {
  EFFECTS, LIBRARY, setEffectCatalog, effectById, synchronizedBreathing, breathingPeriod,
  speedParam, speedSliderValue, speedParamValue, speedText, defaultConfig,
  effectConfigForSelection, shareBreathingPace, paramsAfterChange, randomColor,
  resolvedColorParams, previewPhase, previewColorPhase, activityPeak, dataPreviewFacts, effectColor,
} = renderer;
