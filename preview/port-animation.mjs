const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
const rgbChannels = value => Array.isArray(value) && value.length === 3 && [0, 1, 2].every(index => Number.isFinite(value[index]));
const rgb = value => rgbChannels(value)
  ? value.map(channel => clamp(channel, 0, 255))
  : typeof value === 'string' && /^#[\da-f]{6}$/i.test(value)
    ? [1, 3, 5].map(offset => parseInt(value.slice(offset, offset + 2), 16))
    : [0, 0, 0];
const blend = (a, b, amount) => a.map((channel, index) => channel + (b[index] - channel) * amount);

function colorRecord(value) {
  try {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
    const entries = Object.entries(value);
    if (entries.some(([, color]) => !rgbChannels(color)
      && !(typeof color === 'string' && /^#[\da-f]{6}$/i.test(color)))) return null;
    return Object.fromEntries(entries.map(([port, color]) => [port, rgb(color)]));
  } catch { return null; }
}

export const rgbCss = color => `color(srgb ${color.map(value => clamp(value / 255, 0, 1).toFixed(6)).join(' ')})`;
export function smoothColor(previous, target, elapsed) {
  const channels = typeof target === 'string' ? rgb(target) : target;
  return previous ? blend(previous, channels, 1 - Math.exp(-clamp(elapsed, 0, 80) / 42)) : channels;
}

export const portInk = () => '#ffffff';

// Buffer confirmed frames; slow arrivals never grant permission to predict output.
export class ColorFrames {
  constructor(delay = 120) {
    this.minimumDelay = clamp(Number.isFinite(delay) ? delay : 120, 0, 500);
    this.delay = this.minimumDelay;
    this.frames = []; this.intervals = []; this.playback = null; this.sampledAt = null;
  }

  push(colors, time) {
    if (!Number.isFinite(time)) return false;
    const previous = this.frames.at(-1);
    if (previous && time <= previous.time) return false;
    const decoded = colorRecord(colors);
    if (!decoded) return false;
    if (previous) {
      const interval = time - previous.time;
      if (interval <= 1000) {
        this.intervals.push(interval);
        if (this.intervals.length > 24) this.intervals.shift();
        this.delay = clamp(Math.max(...this.intervals) + 50, this.minimumDelay, 500);
      } else {
        this.intervals.length = 0;
        this.delay = this.minimumDelay;
        this.playback = null; this.sampledAt = null;
      }
    }
    this.frames.push({time, colors: decoded});
    while (this.frames.length > 2 && this.frames[1].time < time - 1200) this.frames.shift();
    return true;
  }

  latest() { return {...this.frames.at(-1)?.colors}; }

  sample(now) {
    if (!this.frames.length) return {};
    const desired = now - this.delay, elapsed = this.sampledAt === null ? null : now - this.sampledAt;
    if (elapsed === null || elapsed < 0 || elapsed > 1000) this.playback = desired;
    else {
      const correction = clamp((desired - this.playback - elapsed) * (1 - Math.exp(-elapsed / 150)), -elapsed * .1, elapsed * .1);
      this.playback += elapsed + correction;
    }
    this.sampledAt = now;
    const time = this.playback = Math.min(this.playback, this.frames.at(-1).time);
    let before = this.frames[0], after = before;
    for (const frame of this.frames) {
      after = frame;
      if (frame.time >= time) break;
      before = frame;
    }
    const amount = after.time === before.time ? 1 : clamp((time - before.time) / (after.time - before.time), 0, 1);
    return Object.fromEntries(Object.entries(after.colors).map(([port, color]) => [port, blend(before.colors[port] || color, color, amount)]));
  }
}

// The switch and the engine stream update about 20 to 30 times a second, so painting faster only costs CPU and GPU time.
export const PAINT_INTERVAL = 1000 / 30;
// After the last visible change the loop stops until a different frame or a preview arrives.
export const SETTLE_TIME = 600;

export function createPortAnimator(getElement, environment = globalThis, {onPaint} = {}) {
  const frames = new ColorFrames();
  const displayed = new Map(), elements = new Map(), painted = new WeakMap();
  const failedPorts = new Set(), reported = new Set();
  const checking = new URLSearchParams(environment.location.search).has('motion-check');
  const reducedMotion = environment.matchMedia('(prefers-reduced-motion: reduce)');
  let active = false, disposed = false, request = 0, last = 0, preview = null, previewFailed = false, frozenTime = null;
  let waiting = 0, idle = false, changedAt = 0;
  let previewPorts = [];

  function schedule() {
    if (!active || request || waiting) return;
    const wait = last && environment.setTimeout ? PAINT_INTERVAL - (environment.performance.now() - last) : 0;
    if (wait > 4) waiting = environment.setTimeout(() => { waiting = 0; if (active && !request) request = environment.requestAnimationFrame(draw); }, wait);
    else request = environment.requestAnimationFrame(draw);
  }
  function report(kind) {
    if (reported.has(kind)) return;
    reported.add(kind);
    environment.console?.warn?.(kind === 'preview'
      ? 'Port preview failed; showing confirmed lighting.'
      : 'A port display could not be updated.');
  }

  function draw(now) {
    request = 0;
    if (!active) return;
    let changed = false;
    try {
      const started = checking ? environment.performance.now() : 0;
      const elapsed = last ? clamp(now - last, 0, 80) : 16;
      last = now;
      const colors = reducedMotion.matches ? frames.latest() : frames.sample(now);
      const previewTime = reducedMotion.matches ? (frozenTime ??= now / 1000) : now / 1000;
      if (preview && !previewFailed) {
        try {
          const staged = colorRecord(preview(previewTime) ?? {});
          if (!staged) throw new TypeError('Invalid preview colors');
          Object.assign(colors, staged);
          previewPorts = Object.keys(staged);
        } catch {
          previewFailed = true;
          for (const port of previewPorts) displayed.delete(port);
          previewPorts = [];
          report('preview');
        }
      }
      if (!active) return;
      for (const [port, target] of Object.entries(colors)) {
        if (!active) break;
        if (failedPorts.has(port)) continue;
        try {
          let element = elements.get(port);
          if (!element?.isConnected) { element = getElement(Number(port)); if (element) elements.set(port, element); }
          if (!element) continue;
          const previous = displayed.get(port);
          const color = reducedMotion.matches ? target : smoothColor(previous, target, elapsed);
          const css = rgbCss(color);
          const prior = painted.get(element);
          const ink = portInk();
          if (prior?.css !== css) { element.style.setProperty('--port-color', css); changed = true; }
          if (prior?.ink !== ink) element.style.setProperty('--port-ink', ink);
          displayed.set(port, color);
          painted.set(element, {css, ink});
        } catch {
          failedPorts.add(port);
          report('paint');
        }
      }
      if (active && onPaint) { try { onPaint(displayed); } catch { report('paint'); } }
      if (checking && active) environment.dispatchEvent(new CustomEvent('ee-render-sample', {detail:{kind:'Lighting', ms:environment.performance.now()-started}}));
    } finally {
      if (changed || !changedAt) changedAt = now;
      // A running preview is time-based, so it keeps painting; otherwise stop once the colors have settled.
      idle = !(preview && !previewFailed) && now - changedAt >= SETTLE_TIME;
      if (!reducedMotion.matches && !idle) schedule();
    }
  }

  function start() {
    if (disposed) return;
    if (!active) { active = true; last = 0; }
    idle = false; changedAt = 0;
    schedule();
  }
  function stop() {
    active = false; environment.cancelAnimationFrame(request); request = 0; last = 0; elements.clear(); failedPorts.clear();
    if (waiting) { environment.clearTimeout?.(waiting); waiting = 0; }
    idle = false; changedAt = 0;
  }
  const motionChanged = () => { frozenTime = null; last = 0; schedule(); };
  reducedMotion.addEventListener('change', motionChanged);
  return {
    pushFrame(colors, time = environment.performance.now()) {
      if (disposed) return;
      const before = idle ? JSON.stringify(frames.latest()) : null;
      if (!frames.push(colors, time)) return;
      failedPorts.clear();
      // A settled display wakes only for a frame that differs from the one it already shows.
      if (before !== null && active && before === JSON.stringify(frames.latest())) return;
      start();
    },
    setPreview(render) { if (!disposed) { preview = render; previewFailed = false; idle = false; changedAt = 0; schedule(); } },
    start,
    stop,
    dispose() { disposed = true; stop(); reducedMotion.removeEventListener('change', motionChanged); displayed.clear(); frames.frames.length = 0; preview = null; }
  };
}
