import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, i, all) =>
  i % 2 === 0 ? [...pairs, [value.replace(/^--/, ''), all[i + 1]]] : pairs, []));
if (!args.browser || !args.output) throw new Error('Provide --browser and --output absolute paths.');
const root = path.resolve(args.root ?? fileURLToPath(new URL('../', import.meta.url)));
const output = path.resolve(args.output);
const port = Number(args.port ?? 4198), url = `http://127.0.0.1:${port}/`;
await mkdir(output, {recursive: true});
const env = {...process.env};
for (const key of Object.keys(env)) if (key.startsWith('EE_') || key.startsWith('PYTHON') || key === 'NODE_OPTIONS') delete env[key];
Object.assign(env, {EE_PREVIEW_LIVE: '0', EE_DESKTOP_HOST: '1', EE_PREVIEW_PORT: String(port),
  EE_WORKSPACE_DATA_DIR: path.join(output, 'demo')});
if (args.python) env.EE_PREVIEW_PYTHON = path.resolve(args.python);
let gatewayText = '', browser, socket, session, sequence = 0;
const pending = new Map(), report = {passed: false, pages: [], sizes: [], errors: []};
const gateway = spawn(process.execPath, [path.join(root, 'preview/server.mjs')], {
  cwd: root, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
gateway.stdout.on('data', data => gatewayText += data);
gateway.stderr.on('data', () => {});
const gatewayExit = new Promise(resolve => gateway.once('exit', code => resolve(code)));
async function until(check, label, milliseconds = 20000) {
  const deadline = Date.now() + milliseconds;
  do {
    const value = await check();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  } while (Date.now() < deadline);
  throw new Error(`Timed out: ${label}`);
}
function send(method, params = {}, id = session) {
  return new Promise((resolve, reject) => {
    const message = ++sequence;
    const timer = setTimeout(() => {pending.delete(message); reject(new Error(`CDP timeout: ${method}`));}, 10000);
    pending.set(message, {resolve: result => {clearTimeout(timer); resolve(result);}, reject: error => {clearTimeout(timer); reject(error);}});
    socket.send(JSON.stringify({id: message, method, params, ...(id ? {sessionId: id} : {})}));
  });
}
async function evaluate(expression) {
  const result = await send('Runtime.evaluate', {expression, returnByValue: true, awaitPromise: true});
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
  return result.result.value;
}
const frame = () => evaluate('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
async function screenshot(name) {
  await evaluate('Promise.all(document.getAnimations().filter(a=>a.effect?.getComputedTiming().iterations!==Infinity).map(a=>a.finished.catch(()=>{})))');
  const result = await send('Page.captureScreenshot', {format: 'png', captureBeyondViewport: false});
  await writeFile(path.join(output, name + '.png'), Buffer.from(result.data, 'base64'));
}
async function bounds(selector) {
  return evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,bottom:r.bottom};})()`);
}
async function tap(selector) {
  await evaluate(`new Promise((resolve,reject)=>{const start=performance.now();let last='',since=start;function check(time){const r=document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect();if(!r)return reject(new Error('Touch target disappeared'));const value=[r.x,r.y,r.width,r.height,document.querySelector('#inspector')?.scrollTop].join(',');if(value!==last){last=value;since=time;}if(time-since>=180)return resolve();if(time-start>4000)return reject(new Error('Touch target kept moving'));requestAnimationFrame(check);}requestAnimationFrame(check);})`);
  const r = await bounds(selector);
  assert.ok(r.y >= 0 && r.bottom <= await evaluate('innerHeight'), `Touch target is clipped: ${selector}`);
  await send('Input.dispatchTouchEvent', {type: 'touchStart', touchPoints: [{x: r.x + r.width / 2, y: r.y + r.height / 2, id: 1, radiusX: 7, radiusY: 7,force:1}]});
  await send('Input.dispatchTouchEvent', {type: 'touchEnd', touchPoints: []});
  await frame();
}
try {
  await until(() => gatewayText.includes(`Browser preview: ${url.slice(0, -1)}`), 'gateway', 40000);
  assert.equal((await (await fetch(url + 'api/workspace')).json()).live, false);
  assert.equal((await (await fetch(url + 'api/session')).json()).simulated, true);
  const profile = path.join(output, 'browser-profile');
  browser = spawn(path.resolve(args.browser), ['--headless=new', '--no-first-run', '--disable-background-networking',
    '--disable-component-update', '--disable-sync', '--disable-extensions', '--remote-debugging-port=0',
    '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1',
    '--user-data-dir=' + profile, 'about:blank'], {windowsHide: true, stdio: 'ignore'});
  const debug = await until(async () => {try {return (await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).trim().split('\n');} catch {return null;}}, 'browser');
  socket = new WebSocket(`ws://127.0.0.1:${debug[0]}${debug[1]}`);
  await new Promise((resolve, reject) => {socket.addEventListener('open', resolve, {once: true}); socket.addEventListener('error', reject, {once: true});});
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const entry = pending.get(message.id);
      if (entry) {pending.delete(message.id); message.error ? entry.reject(new Error(message.error.message)) : entry.resolve(message.result);}
    } else if (message.method === 'Runtime.exceptionThrown') report.errors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
  });
  const target = await send('Target.createTarget', {url: 'about:blank'}, null);
  session = (await send('Target.attachToTarget', {targetId: target.targetId, flatten: true}, null)).sessionId;
  await send('Runtime.enable'); await send('Page.enable');
  await send('Page.navigate', {url});
  await until(() => evaluate('document.querySelectorAll(".port-socket-rim").length === 10'), 'port map');
  const firstSample = await until(async () => {
    const value = (await (await fetch(url + 'api/state')).json()).uplink_bandwidth;
    return value?.status === 'ready' ? value : null;
  }, 'first bandwidth sample');
  const firstObserved = performance.now();
  const secondSample = await until(async () => {
    const value = (await (await fetch(url + 'api/state')).json()).uplink_bandwidth;
    return value?.status === 'ready' && value.sequence > firstSample.sequence ? value : null;
  }, 'next bandwidth sample');
  const observedInterval = performance.now() - firstObserved;
  assert.ok(observedInterval > 4500 && observedInterval < 5500, 'Bandwidth publication must follow five-second deadlines');
  assert.ok(secondSample.sample_period_ms > 0 && secondSample.sample_period_ms < 6500);
  report.bandwidth = {first: firstSample, second: secondSample, observed_interval_ms: observedInterval};
  report.bandwidth.display = await until(async () => {
    const readings = await evaluate(`(async () => {
    const {formatBandwidth}=await import('/preview/uplink-bandwidth.mjs');
    const sample=(await (await fetch('/api/state')).json()).uplink_bandwidth;
    return [...document.querySelectorAll('.bandwidth-reading')].map((node,index)=>{
      const rate=formatBandwidth(index?sample.download_bps:sample.upload_bps);
      return {display:node.querySelector('dd').textContent, expected:rate.value+rate.unit};
    });
    })()`);
    return readings.length === 2 && readings.every(reading => reading.display === reading.expected) ? readings : null;
  }, 'bandwidth display settles at confirmed values', 3000);
  assert.equal(report.bandwidth.display.length, 2);
  for (const reading of report.bandwidth.display) assert.equal(reading.display, reading.expected);
  report.bandwidth.animation = await evaluate(`(async () => {
    const {mountUplinkBandwidth}=await import('/preview/uplink-bandwidth.mjs');
    const host=document.createElement('div');host.style.cssText='position:fixed;left:-10000px';document.body.append(host);
    const panel=mountUplinkBandwidth(host), samples=[];
    const sample={status:'ready',ports:[5],upload_bps:1000000,download_bps:1000000,sample_period_ms:5000,sample_age_ms:0,sequence:1};
    panel.update({connected:true,uplink_bandwidth:sample});
    panel.update({connected:true,uplink_bandwidth:{...sample,upload_bps:100000000,sequence:2}});
    await new Promise(resolve=>{const start=performance.now();function step(time){samples.push(host.querySelector('.bandwidth-value').textContent);if(time-start<750)requestAnimationFrame(step);else resolve();}requestAnimationFrame(step);});
    panel.dispose();host.remove();
    return {unique:new Set(samples).size,last:samples.at(-1),reduced:matchMedia('(prefers-reduced-motion: reduce)').matches};
  })()`);
  assert.equal(report.bandwidth.animation.last, '100.00');
  assert.ok(report.bandwidth.animation.reduced || report.bandwidth.animation.unique > 8);
  for (const page of ['Effects', 'Automation', 'Profiles', 'Help', 'Settings', 'Lighting']) {
    await evaluate(`document.querySelector('[data-page="${page}"]').click()`);
    await until(() => evaluate(`document.querySelector('[data-page="${page}"]').getAttribute('aria-current') === 'page'`), page);
    report.pages.push(page);
  }
  for (const [width, height] of [[1280, 800], [1024, 600], [390, 844], [1280, 400], [1024, 400]]) {
    await send('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor: 1, mobile: false});
    await frame();
    const layout = await evaluate(`({width: innerWidth, height: innerHeight, scrollWidth: document.documentElement.scrollWidth})`);
    assert.ok(layout.scrollWidth <= width + 1, 'Page overflows viewport');
    report.sizes.push(layout);
    await screenshot(`lighting-${width}-${height}`);
  }
  await send('Emulation.setTouchEmulationEnabled', {enabled: true, maxTouchPoints: 5});
  await evaluate(`window.touchTrace=[];for(const type of ['pointerdown','pointerup','click','focusout'])document.addEventListener(type,event=>{window.touchTrace.push({type,tag:event.target.tagName,cls:event.target.className,color:event.target.dataset?.color,open:!!document.querySelector('.color-popover')});if(touchTrace.length>30)touchTrace.shift();},true)`);
  report.rack = [];
  for (const width of [1280, 1024]) {
    await send('Emulation.setDeviceMetricsOverride', {width, height: 400, deviceScaleFactor: 1, mobile: false});
    await evaluate('document.querySelector("[data-page=Lighting]").click()');
    await frame();
    const inspector = await bounds('#inspector');
    const before = await evaluate(`({canvas:document.querySelector('.canvas-area').scrollTop,inspector:document.querySelector('#inspector').scrollTop,port:document.querySelector('[data-port="1"]').getBoundingClientRect().top})`);
    const scrollX = inspector.x + inspector.width - 24, scrollY = inspector.y + inspector.height * .8;
    await send('Input.dispatchTouchEvent', {type:'touchStart',touchPoints:[{x:scrollX,y:scrollY,id:1}]});
    for (let step=1;step<=10;step++) {
      await send('Input.dispatchTouchEvent', {type:'touchMove',touchPoints:[{x:scrollX,y:scrollY-step*12,id:1}]});
      await frame();
    }
    await send('Input.dispatchTouchEvent', {type:'touchEnd',touchPoints:[]});
    const after = await until(async () => {
      const value = await evaluate(`({canvas:document.querySelector('.canvas-area').scrollTop,inspector:document.querySelector('#inspector').scrollTop,port:document.querySelector('[data-port="1"]').getBoundingClientRect().top})`);
      return value.inspector > before.inspector ? value : null;
    }, 'independent inspector touch scroll');
    assert.equal(after.canvas, before.canvas);
    assert.ok(Math.abs(after.port-before.port)<1, 'Editing controls must not move the map');
    await screenshot(`rack-scroll-${width}`);
    await evaluate('document.querySelector("[data-port=\\"3\\"]").click()');
    await until(() => evaluate('document.querySelector("#inspector").dataset.scope === "3"'), 'port selection');
    await evaluate('document.querySelector("[data-editor=color]").click()');
    await until(() => evaluate('!!document.querySelector("[data-color=manual]")'), 'fixed color controls');
    await evaluate('document.querySelector("[data-color=manual]").scrollIntoView({block:"center"})');
    await frame();
    const beforeScene = (await (await fetch(url + 'api/scene')).json()).scene;
    await tap('[data-color="manual"]');
    await until(() => evaluate('!!document.querySelector(".color-popover")'), 'color picker');
    await tap('.color-sv');
    const firstColor = await evaluate('document.querySelector("[data-color=manual]").value');
    await evaluate('document.querySelector("#master").focus({preventScroll:true})');
    await frame();
    assert.equal(await evaluate('!!document.querySelector(".color-popover")'), true, 'Picker survives touch focus restoration');
    const hue = await bounds('.color-hue');
    await send('Input.dispatchTouchEvent', {type:'touchStart', touchPoints:[{x:hue.x+hue.width*.2,y:hue.y+hue.height/2,id:1}]});
    const hueEnd=width===1280?.8:.4;
    for (let step=1;step<=8;step++) await send('Input.dispatchTouchEvent', {type:'touchMove', touchPoints:[{x:hue.x+hue.width*(.2+step*(hueEnd-.2)/8),y:hue.y+hue.height/2,id:1}]});
    await send('Input.dispatchTouchEvent', {type:'touchEnd',touchPoints:[]});
    const chosen = await evaluate('document.querySelector("[data-color=manual]").value');
    assert.notEqual(chosen, firstColor);
    assert.ok(await evaluate(`(()=>{const led=document.querySelector('[data-port="3"] .port-led').getBoundingClientRect(),canvas=document.querySelector('.canvas-area').getBoundingClientRect();return led.top>=canvas.top&&led.bottom<=canvas.bottom;})()`), 'The selected port stays fully visible while editing its color');
    assert.deepEqual((await (await fetch(url + 'api/scene')).json()).scene, beforeScene, 'Touch edits need Apply');
    await screenshot(`rack-picker-${width}`);
    await evaluate('document.querySelector(".color-popup-footer button").scrollIntoView({block:"nearest"})');
    await frame();
    await tap('.color-popup-footer button');
    const apply = await bounds('#apply-draft');
    assert.ok(apply.height >= 44 && apply.bottom <= 400, 'Apply remains visible and touch sized');
    await tap('#apply-draft');
    await until(() => evaluate('document.querySelector("#draft-actions").hidden'), 'applied draft');
    assert.equal((await (await fetch(url + 'api/scene')).json()).scene.overrides['3'].color, chosen.toLowerCase());
    report.rack.push({width,height:400,before,after,apply,touch_color_applied:true});
    for (const page of ['Effects','Automation','Profiles','Help','Settings','Lighting']) {
      await evaluate(`document.querySelector('[data-page="${page}"]').click()`);
      await frame();
      assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth + 1'), `${page} overflows rack viewport`);
      await screenshot(`rack-${width}-${page.toLowerCase()}`);
    }
  }
  assert.deepEqual(report.errors, []);
  report.passed = true;
} catch (error) {
  report.failure = error.message;
  try {report.touch_trace=await evaluate('window.touchTrace');} catch {}
  try {report.failure_layout=await evaluate(`[...document.querySelectorAll('.studio-content,.canvas-area,.canvas-area > *,#inspector')].map(e=>({name:e.id||e.className,top:e.getBoundingClientRect().top,height:e.clientHeight,scroll:e.scrollHeight,scrollTop:e.scrollTop,overflow:getComputedStyle(e).overflowY}))`);} catch {}
  try {await screenshot('failure');} catch {}
  throw error;
} finally {
  if (socket?.readyState === WebSocket.OPEN) {
    try {await send('Browser.close', {}, null);} catch {}
    socket.close();
  } else if (browser) browser.kill();
  if (gateway.exitCode === null) gateway.stdin.end();
  const code = await Promise.race([gatewayExit, new Promise(resolve => setTimeout(() => resolve('timeout'), 15000))]);
  report.gateway_exit = code;
  if (code !== 0) {report.passed = false; if (code === 'timeout') gateway.kill();}
  await writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2) + '\n');
  if (!report.passed) process.exitCode = 1;
}
console.log(JSON.stringify(report));
