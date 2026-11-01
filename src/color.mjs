export const clamp = (n, low = 0, high = 1) => Math.min(high, Math.max(low, n));
export function normalizeHex(value) {
  const clean = String(value).trim().replace(/^#/, '');
  if (/^[\da-f]{3}$/i.test(clean)) return '#' + [...clean].map(c => c + c).join('').toUpperCase();
  if (/^[\da-f]{6}$/i.test(clean)) return '#' + clean.toUpperCase();
  return null;
}
export function hexToRgb(hex) {
  const n = Number.parseInt(normalizeHex(hex)?.slice(1) || 'FFFFFF', 16);
  return [n >> 16, (n >> 8) & 255, n & 255];
}
export function rgbToHex(rgb) { return '#' + rgb.map(v => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, '0')).join('').toUpperCase(); }
export function hsvToHex(h, s, v) {
  h = ((h % 360) + 360) % 360;
  s = clamp(s); v = clamp(v);
  const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
  const rgb = h < 60 ? [c,x,0] : h < 120 ? [x,c,0] : h < 180 ? [0,c,x] : h < 240 ? [0,x,c] : h < 300 ? [x,0,c] : [c,0,x];
  return rgbToHex(rgb.map(n => (n + m) * 255));
}
export function hexToHsv(hex) {
  const [r,g,b] = hexToRgb(hex).map(n => n / 255), max = Math.max(r,g,b), min = Math.min(r,g,b), d = max - min;
  let h = d === 0 ? 0 : max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [((h * 60) + 360) % 360, max ? d / max : 0, max];
}
export const mix = (a, b, t) => rgbToHex(hexToRgb(a).map((v,i) => v + (hexToRgb(b)[i] - v) * clamp(t)));
export const dim = (hex, value) => rgbToHex(hexToRgb(hex).map(v => v * clamp(value)));
