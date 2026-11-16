import assert from 'node:assert/strict';
import test from 'node:test';
import {readFile} from 'node:fs/promises';
import {THEMES,DEFAULT_THEME} from '../preview/theme-catalog.mjs';
import {ColorFrames, smoothColor, portInk, createPortAnimator, rgbCss} from '../preview/port-animation.mjs';

test('uneven network arrivals render an even fade between confirmed samples', () => {
  const frames = new ColorFrames(120);
  for (const [time, value] of [[0,0], [40,20], [104,52], [120,60], [190,95], [240,120]]) {
    const color = `#${value.toString(16).padStart(2,'0').repeat(3)}`;
    frames.push({1:color,8:color}, time);
  }
  const samples = Array.from({length:29}, (_,i) => frames.sample(120+i*1000/120));
  const steps = samples.slice(1).map((sample,i) => sample[1][0]-samples[i][1][0]);
  assert.ok(steps.every(step => step > 3.9 && step < 4.4));
  assert.ok(samples.some(sample => sample[1][0] % 1 !== 0), 'intermediate colors must retain fractional intensity');
  for (const sample of samples) assert.deepEqual(sample[1],sample[8], 'all ports share the same playback clock');
});

test('a disconnected stream holds its last confirmed color without extrapolation', () => {
  const frames = new ColorFrames();
  frames.push({1:'#000000'},0);
  frames.push({1:'#804020'},40);
  assert.deepEqual(frames.sample(10000),{1:[128,64,32]});
  frames.push({1:'#ffffff'},20);
  assert.deepEqual(frames.sample(10000),{1:[128,64,32]});
});

test('long sessions keep a bounded interpolation history', () => {
  const frames = new ColorFrames();
  for (let time=0;time<60000;time+=40) frames.push({1:'#101010'},time);
  assert.ok(frames.frames.length <= 33);
  assert.deepEqual(frames.sample(60120),{1:[16,16,16]});
});

for (const fps of [30, 60, 120]) {
  for (const intervals of [[40], [200], [1000 / 3.75], [40, 80, 240, 60, 180, 50]]) {
    test(`causal ${intervals.join('/')}ms arrivals fade evenly at ${fps}Hz`, () => {
      const frames = new ColorFrames(), step = 1000 / fps, rate = .04;
      let next = 0, arrival = 0, previous, displayed;
      for (let i = 0; i < fps * 5; i++) {
        const now = i * step;
        while (next <= now + 1e-6) {
          const channel = next * rate;
          frames.push({1: [channel, channel, channel], 8: [channel, channel, channel]}, next);
          next += intervals[arrival++ % intervals.length];
        }
        const sample = frames.sample(now), before = displayed;
        displayed = smoothColor(displayed, sample[1], step);
        assert.deepEqual(sample[1], sample[8]);
        assert.ok(sample[1][0] <= frames.latest()[1][0], 'never predict an unconfirmed color');
        if (now >= 2000) {
          const expected = step * rate;
          assert.ok(sample[1][0] - previous >= expected * .98, 'steady arrival cadence must not hold');
          assert.ok(sample[1][0] - previous <= expected * 1.02, 'steady arrival cadence must not catch up in bursts');
          assert.ok(Math.abs(displayed[0] - before[0] - expected) < expected * .02);
        }
        previous = sample[1][0];
      }
    });
  }
}

test('a late sample changes playback pace without rewinding or jumping ahead', () => {
  const frames = new ColorFrames(), step = 1000 / 120;
  let previous = null;
  for (let now = 0; now < 1600; now += step) {
    if (now < 500 || now > 820) frames.push({1: [now / 10, 0, 0]}, now);
    const value = frames.sample(now)[1][0];
    if (previous !== null) {
      assert.ok(value >= previous - 1e-9);
      assert.ok(value - previous <= step / 10 * 1.101);
    }
    previous = value;
  }
  assert.ok(frames.delay <= 500);
});

test('cadence evidence expires after interruption and stale output holds exactly', () => {
  const frames = new ColorFrames();
  for (let time = 0; time <= 1600; time += 400) frames.push({1: [time / 10, 0, 0]}, time);
  assert.equal(frames.delay, 450);
  assert.deepEqual(frames.sample(5000), {1: [160, 0, 0]});
  assert.deepEqual(frames.sample(6000), {1: [160, 0, 0]});
  frames.push({1: [0, 255, 0]}, 10000);
  assert.equal(frames.delay, 120);
  assert.deepEqual(frames.sample(11000), {1: [0, 255, 0]});
  frames.push({1: [255, 255, 255]}, 9999);
  assert.deepEqual(frames.latest(), {1: [0, 255, 0]});
});

test('neutral port labels retain 4.5 contrast in every theme through lighting colors and selection', async () => {
  const [css, palettes] = await Promise.all(['style.css','themes.css'].map(name => readFile(new URL('../preview/'+name, import.meta.url), 'utf8')));
  const channels = hex => {assert.match(hex,/^#[\da-f]{6}(?:[\da-f]{2})?$/i);return [1,3,5].map(offset=>parseInt(hex.slice(offset,offset+2),16));};
  const declarations = selector => {
    const escaped=selector.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
    const block=palettes.match(new RegExp(escaped+'\\s*\\{([^}]+)\\}'))?.[1];
    assert.ok(block,'Palette declarations exist for '+selector);
    return Object.fromEntries([...block.matchAll(/--([\w-]+):\s*([^;]+);/g)].map(([,name,value])=>[name,value.trim()]));
  };
  const defaults=declarations(':root'),derived=declarations(':root[data-theme]:not([data-theme="evolved"])');
  const glow = Number(css.match(/\.port-glow\s*\{[^}]*opacity:\s*([.\d]+)/)[1]);
  const selection = css.match(/\.port-tile\.selected\s*\{[^}]*background:\s*(#[\da-f]{8})/i)[1];
  const composite = (foreground, background, alpha) => foreground.map((value, index) => value * alpha + background[index] * (1 - alpha));
  const luminance = channels => channels.map(channel => {
    const value = channel / 255;
    return value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
  }).reduce((sum, value, i) => sum + value * [.2126, .7152, .0722][i], 0);
  const colors=Array.from({length:256},(_,value)=>[value,value,value]);
  for(let r=0;r<=255;r+=17)for(let g=0;g<=255;g+=17)for(let b=0;b<=255;b+=17)colors.push([r,g,b]);
  for(const color of colors)assert.equal(portInk(color),'#ffffff');
  for(const theme of THEMES){
    const values={...defaults,...theme.id===DEFAULT_THEME?{}:derived,...declarations(':root[data-theme="'+theme.id+'"]')};
    const token=name=>{assert.ok(values[name],'Palette token '+name);return values[name];};
    const ink=luminance(channels(token('text')));
    const tints=[...token('canvas-tint').matchAll(/(#[\da-f]{8})|color-mix\(in srgb, var\(--([\w-]+)\) ([\d.]+)%, transparent\)/gi)].map(([,hex,name,percent])=>({color:channels(hex||token(name)),alpha:hex?parseInt(hex.slice(7),16)/255:Number(percent)/100}));
    assert.ok(tints.length,'Canvas tint colors are accounted for in '+theme.id);
    const backgrounds=['surface','panel','raised'].map(name=>tints.reduce((background,tint)=>composite(tint.color,background,tint.alpha),channels(token(name))));
    for(const color of colors)for(const base of backgrounds)for(const selected of [false,true]){
      const lit=composite(color,base,glow);
      const background=selected?composite(channels(selection),lit,parseInt(selection.slice(7),16)/255):lit;
      const backdrop=luminance(background),contrast=(Math.max(ink,backdrop)+.05)/(Math.min(ink,backdrop)+.05);
      assert.ok(contrast>=4.5,`${theme.id}, ${color}, selected=${selected}: contrast ${contrast}`);
    }
  }
  assert.match(css, /\.port-tile\s*\{[^}]*color:\s*var\(--text\)/);
  assert.doesNotMatch(css.match(/\.port-number\s*\{[^}]*\}/)[0], /--port-(?:color|ink)/);
});

function animatorEnvironment() {
  let now = 0, sequence = 0;
  const queue = new Map(), listeners = new Set(), warnings = [];
  const media = {matches: true, addEventListener: (_, listener) => listeners.add(listener), removeEventListener: (_, listener) => listeners.delete(listener)};
  const environment = {
    location: {search: ''}, performance: {now: () => now}, matchMedia: () => media,
    console: {warn: message => warnings.push(message)},
    requestAnimationFrame: callback => { queue.set(++sequence, callback); return sequence; },
    cancelAnimationFrame: request => queue.delete(request),
  };
  return {environment, queue, listeners, media, warnings,
    flush(time) { now = time; const callbacks = [...queue.values()]; queue.clear(); for (const callback of callbacks) callback(now); },
    motion(reduce) { media.matches = reduce; for (const listener of listeners) listener(); },
  };
}

test('ambient feedback receives the displayed frame and follows the animator lifecycle', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const styles = new Map(), samples = [];
  const animator = createPortAnimator(port => ({isConnected:true,style:{setProperty(key,value) {
    if (key==='--port-color') styles.set(String(port),value);
  }}}),mock.environment,{onPaint(colors) {
    for (const [port,rgb] of colors) assert.equal(rgbCss(rgb),styles.get(port));
    samples.push(structuredClone(colors));
  }});
  animator.pushFrame({1:'#0011ff',8:'#ff22aa'},0); mock.flush(10);
  animator.pushFrame({1:'#ffaa00',8:'#aa33ff'},400); mock.flush(500);
  assert.equal(samples.length,2);
  animator.stop(); mock.flush(510);
  assert.equal(samples.length,2);
  mock.motion(true); animator.pushFrame({1:'#ffffff',8:'#ffffff'},600); mock.flush(610);
  assert.equal(samples.length,3);
  assert.equal(mock.queue.size,0);
  animator.dispose(); mock.flush(620);
  assert.equal(samples.length,3);
});

test('optional feedback failure cannot interrupt port playback', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const styles = new Map();
  const animator = createPortAnimator(() => ({isConnected:true,style:{setProperty:(key,value)=>styles.set(key,value)}}),mock.environment,
    {onPaint(){throw new Error('canvas unavailable');}});
  animator.pushFrame({1:'#00aaff'},0);
  assert.doesNotThrow(()=>mock.flush(10));
  assert.equal(styles.get('--port-color'),rgbCss([0,170,255]));
  assert.equal(mock.queue.size,1);
  mock.flush(20);
  assert.equal(mock.warnings.length,1);
  animator.dispose();
});

test('copper and SFP labels keep one ink while their live colors fade', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const values = new Map(), inks = new Map(), colors = new Map();
  for (const port of [1, 8, 9, 10]) {
    inks.set(port, []); colors.set(port, new Set());
    values.set(port, {isConnected: true, style: {setProperty(key, value) {
      if (key === '--port-ink') inks.get(port).push(value);
      if (key === '--port-color') colors.get(port).add(value);
    }}});
  }
  const animator = createPortAnimator(port => values.get(port), mock.environment);
  const palette = ['#000000', '#ffffff', '#00aaff', '#ff00ff', '#ffff00', '#000000'];
  for (let frame = 0; frame < 720; frame++) {
    const now = frame * 1000 / 120;
    if (frame % 6 === 0) animator.pushFrame(Object.fromEntries([...values.keys()].map((port, i) => [port, palette[(Math.floor(frame / 120) + i) % palette.length]])), now);
    mock.flush(now);
  }
  for (const port of values.keys()) {
    assert.deepEqual(inks.get(port), ['#ffffff'], `port ${port} must not flip label color`);
    assert.ok(colors.get(port).size > 100, 'live fill must keep animating');
  }
  animator.dispose();
});

test('reduced motion paints confirmed frames and frozen drafts without a perpetual loop', () => {
  const mock = animatorEnvironment(), styles = new Map(), previewTimes = [];
  const element = {isConnected: true, style: {setProperty: (key, value) => styles.set(key, value)}};
  const animator = createPortAnimator(() => element, mock.environment);
  animator.pushFrame({1: '#00aaff'}, 0); mock.flush(10);
  assert.equal(styles.get('--port-color'), rgbCss([0, 170, 255]));
  assert.equal(mock.queue.size, 0);
  animator.pushFrame({1: '#ff0000'}, 200); mock.flush(210);
  assert.equal(styles.get('--port-color'), rgbCss([255, 0, 0]));
  animator.setPreview(time => { previewTimes.push(time); return {1: '#0011ff'}; }); mock.flush(220);
  animator.pushFrame({1: '#00ff00'}, 800); mock.flush(810);
  assert.equal(new Set(previewTimes).size, 1);
  assert.equal(mock.queue.size, 0);
  animator.setPreview(null); mock.flush(820);
  assert.equal(styles.get('--port-color'), rgbCss([0, 255, 0]));
  mock.motion(false); mock.flush(830);
  assert.equal(mock.queue.size, 1);
  mock.motion(true); mock.flush(840);
  assert.equal(mock.queue.size, 0);
  animator.dispose();
  assert.equal(mock.listeners.size, 0);
  assert.equal(mock.queue.size, 0);
});

test('multiple telemetry messages coalesce into one reduced-motion paint', () => {
  const mock = animatorEnvironment(), styles = new Map();
  const animator = createPortAnimator(() => ({isConnected: true, style: {setProperty: (key, value) => styles.set(key, value)}}), mock.environment);
  animator.pushFrame({1: '#ff0000'}, 0);
  animator.pushFrame({1: '#00ff00'}, 1);
  animator.pushFrame({1: '#0000ff'}, 2);
  assert.equal(mock.queue.size, 1);
  mock.flush(3);
  assert.equal(styles.get('--port-color'), rgbCss([0, 0, 255]));
  animator.stop(); mock.motion(false);
  assert.equal(mock.queue.size, 0);
  animator.dispose();
});

test('a failing preview yields to confirmed output and is not retried on each frame', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const styles = new Map(); let calls = 0;
  const element = {isConnected: true, style: {setProperty: (key, value) => styles.set(key, value)}};
  const animator = createPortAnimator(() => element, mock.environment);
  animator.setPreview(() => { calls++; throw new Error('invalid draft'); });
  animator.pushFrame({1: '#ff0000'}, 0);
  assert.doesNotThrow(() => mock.flush(10));
  assert.equal(styles.get('--port-color'), rgbCss([255, 0, 0]));
  assert.equal(mock.queue.size, 1);
  for (const time of [30, 50, 70]) mock.flush(time);
  animator.pushFrame({1: '#00ff00'}, 1000); mock.flush(2000);
  assert.equal(calls, 1, 'a broken draft must not throw again on every paint or telemetry update');
  assert.deepEqual(mock.warnings, ['Port preview failed; showing confirmed lighting.']);
  assert.equal(styles.get('--port-color'), rgbCss(smoothColor([255, 0, 0], [0, 255, 0], 80)));
  animator.setPreview(() => ({1: '#0000ff'})); mock.flush(2016);
  assert.equal(styles.get('--port-color'), rgbCss(smoothColor(smoothColor([255, 0, 0], [0, 255, 0], 80), [0, 0, 255], 16)));
  assert.equal(mock.queue.size, 1);
  animator.dispose();
});

test('a malformed preview cannot partially overwrite a confirmed frame', () => {
  const mock = animatorEnvironment(), styles = new Map(); let calls = 0;
  const animator = createPortAnimator(port => ({isConnected: true, style: {setProperty(key, value) {
    if (key === '--port-color') styles.set(port, value);
  }}}), mock.environment);
  animator.setPreview(() => { calls++; return {1: '#ffffff', 8: Symbol('invalid color')}; });
  animator.pushFrame({1: '#00aaff', 8: '#00aaff'}, 0);
  assert.doesNotThrow(() => mock.flush(10));
  assert.deepEqual([...styles.values()], [rgbCss([0, 170, 255]), rgbCss([0, 170, 255])]);
  assert.equal(mock.queue.size, 0, 'reduced motion does not retry a failed draft');
  animator.pushFrame({1: '#ff0000', 8: '#ff0000'}, 100); mock.flush(110);
  assert.deepEqual([...styles.values()], [rgbCss([255, 0, 0]), rgbCss([255, 0, 0])]);
  assert.equal(calls, 1);
  animator.setPreview(() => ({1: '#00ff00'})); mock.flush(120);
  assert.equal(styles.get(1), rgbCss([0, 255, 0]));
  assert.equal(styles.get(8), rgbCss([255, 0, 0]));
  assert.equal(mock.queue.size, 0);
  animator.dispose();
});

test('a draft that fails after painting returns to confirmed color without blending the failed draft', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const styles = new Map(); let broken = false, calls = 0;
  const animator = createPortAnimator(port => ({isConnected: true, style: {setProperty(key, value) {
    if (key === '--port-color') styles.set(port, value);
  }}}), mock.environment);
  const preview = () => { calls++; if (broken) throw new Error('invalid changed draft'); return {1: '#00ff00'}; };
  animator.setPreview(preview); animator.pushFrame({1: '#00aaff', 8: '#ff0000'}, 0); mock.flush(10);
  assert.equal(styles.get(1), rgbCss([0, 255, 0]));
  broken = true; animator.pushFrame({1: '#00aaff', 8: '#0000ff'}, 1000); mock.flush(2000);
  assert.equal(styles.get(1), rgbCss([0, 170, 255]));
  assert.equal(styles.get(8), rgbCss(smoothColor([255, 0, 0], [0, 0, 255], 80)), 'unrelated live ports retain their interpolation');
  mock.flush(2010); assert.equal(calls, 2);
  broken = false; animator.setPreview(preview); mock.flush(2020);
  assert.equal(calls, 3, 'an explicit edit may retry the same preview callback');
  assert.equal(styles.get(1), rgbCss(smoothColor([0, 170, 255], [0, 255, 0], 10)));
  animator.dispose();
});

test('malformed telemetry leaves confirmed colors and cadence intact before valid recovery', () => {
  const frames = new ColorFrames();
  frames.push({1: '#00aaff'}, 0); frames.push({1: '#00ff00'}, 200);
  const snapshot = {latest: frames.latest(), delay: frames.delay, intervals: [...frames.intervals], length: frames.frames.length};
  const throwing = {get 1() { throw new Error('invalid frame'); }};
  for (const value of [null, [], {1: [NaN, 0, 0]}, {1: new Array(3)}, {1: '#ffffff', 8: Symbol('invalid color')}, throwing]) {
    assert.doesNotThrow(() => frames.push(value, 10000));
    assert.deepEqual({latest: frames.latest(), delay: frames.delay, intervals: frames.intervals, length: frames.frames.length}, snapshot);
  }
  frames.push({1: '#ff0000'}, 400);
  assert.deepEqual(frames.latest(), {1: [255, 0, 0]});
  assert.deepEqual(frames.intervals, [200, 200]);
});

test('one failed port paint does not block other ports or retry between confirmed updates', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  const styles = new Map(); let failing = true, attempts = 0;
  const nodes = new Map([1, 8].map(port => [port, {isConnected: true, style: {setProperty(key, value) {
    if (port === 1) { attempts++; if (failing) throw new Error('detached paint target'); }
    if (key === '--port-color') styles.set(port, value);
  }}}]));
  const animator = createPortAnimator(port => nodes.get(port), mock.environment);
  animator.pushFrame({1: '#00aaff', 8: '#00aaff'}, 0);
  assert.doesNotThrow(() => mock.flush(10));
  assert.equal(styles.get(8), rgbCss([0, 170, 255]));
  assert.equal(mock.queue.size, 1);
  mock.flush(20); mock.flush(30);
  assert.equal(attempts, 1, 'a failing paint target is only reconsidered with new confirmed data');
  assert.deepEqual(mock.warnings, ['A port display could not be updated.']);
  failing = false;
  animator.pushFrame({1: '#00aaff', 8: '#00aaff'}, 100); mock.flush(110);
  assert.equal(styles.get(1), rgbCss([0, 170, 255]));
  assert.equal(mock.queue.size, 1);
  animator.dispose();
});

test('disposal during a failed draft cannot paint or restart the animation', () => {
  const mock = animatorEnvironment(); mock.media.matches = false;
  let paints = 0;
  const animator = createPortAnimator(() => ({isConnected: true, style: {setProperty() { paints++; }}}), mock.environment);
  animator.setPreview(() => { animator.dispose(); throw new Error('disposed draft'); });
  animator.pushFrame({1: '#00aaff'}, 0);
  assert.doesNotThrow(() => mock.flush(10));
  assert.equal(paints, 0);
  assert.equal(mock.queue.size, 0);
  assert.equal(mock.listeners.size, 0);
  animator.pushFrame({1: '#ff0000'}, 20); animator.setPreview(() => ({1: '#00ff00'})); animator.start();
  assert.equal(mock.queue.size, 0);
});

function timedAnimatorEnvironment() {
  const mock = animatorEnvironment(), timers = new Map();
  let handle = 0;
  mock.media.matches = false;
  mock.environment.setTimeout = (callback, delay) => { timers.set(++handle, {callback, delay}); return handle; };
  mock.environment.clearTimeout = id => timers.delete(id);
  mock.timers = timers;
  mock.fireTimers = () => { const pending = [...timers.values()]; timers.clear(); for (const timer of pending) timer.callback(); };
  return mock;
}

test('steady lighting stops repainting until a different frame arrives', async () => {
  const {PAINT_INTERVAL, SETTLE_TIME} = await import('../preview/port-animation.mjs');
  const mock = timedAnimatorEnvironment(), writes = [];
  const element = {isConnected: true, style: {setProperty: (key, value) => { if (key === '--port-color') writes.push(value); }}};
  const animator = createPortAnimator(() => element, mock.environment);
  let time = 0;
  const step = () => { time += PAINT_INTERVAL; mock.fireTimers(); mock.flush(time); };
  animator.pushFrame({1: '#204060'}, time);
  for (let i = 0; i < 400 && (mock.queue.size || mock.timers.size); i++) step();
  assert.equal(mock.queue.size, 0); assert.equal(mock.timers.size, 0);
  assert.ok(time >= SETTLE_TIME, 'the loop keeps painting while the color settles');
  const settled = writes.length;
  for (let i = 0; i < 40; i++) { time += 45; animator.pushFrame({1: '#204060'}, time); }
  assert.equal(mock.queue.size, 0, 'identical frames do not wake a settled display');
  assert.equal(writes.length, settled);
  time += 45; animator.pushFrame({1: '#ff0000'}, time);
  assert.equal(mock.queue.size + mock.timers.size, 1, 'a different frame wakes it');
  for (let i = 0; i < 60; i++) step();
  assert.ok(writes.length > settled);
  animator.dispose();
});

test('moving lighting paints at most thirty times a second on a fast display', async () => {
  const {PAINT_INTERVAL} = await import('../preview/port-animation.mjs');
  const mock = timedAnimatorEnvironment();
  let paints = 0;
  const element = {isConnected: true, style: {setProperty: key => { if (key === '--port-color') paints++; }}};
  const animator = createPortAnimator(() => element, mock.environment);
  // One second of a 240 Hz display with a color that changes on every stream frame.
  for (let frame = 0; frame <= 240; frame++) {
    const time = frame * 1000 / 240;
    if (frame % 10 === 0) animator.pushFrame({1: `#${(frame % 256).toString(16).padStart(2, '0')}4080`}, time);
    for (const [id, timer] of [...mock.timers]) if (timer.due === undefined) timer.due = time + timer.delay;
    for (const [id, timer] of [...mock.timers]) if (timer.due <= time) { mock.timers.delete(id); timer.callback(); }
    mock.flush(time);
  }
  assert.ok(paints > 15, `painted ${paints} times`);
  assert.ok(paints <= Math.ceil(1000 / PAINT_INTERVAL) + 1, `painted ${paints} times`);
  animator.stop();
  assert.equal(mock.timers.size, 0, 'stopping clears the pending wake-up');
  animator.dispose();
});

test('a staged preview keeps animating and stopping clears every pending frame', () => {
  const mock = timedAnimatorEnvironment();
  const element = {isConnected: true, style: {setProperty() {}}};
  const animator = createPortAnimator(() => element, mock.environment);
  animator.pushFrame({1: '#101010'}, 0);
  animator.setPreview(() => ({1: '#303030'}));
  let time = 0;
  for (let i = 0; i < 120; i++) { time += 34; mock.fireTimers(); mock.flush(time); }
  assert.equal(mock.queue.size + mock.timers.size, 1, 'a preview is time-based and never settles');
  animator.stop();
  assert.equal(mock.queue.size + mock.timers.size, 0);
  animator.dispose();
});
