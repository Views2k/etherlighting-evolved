import {checkProductWorkflows} from './product-workspace-checks.mjs';
import {checkThemeWorkflows} from './theme-workspace-checks.mjs';
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
async function checkFaceplate(width, height) {
  const rack = height <= 500;
  const geometry = await evaluate(`(() => {
    const canvas = document.querySelector('.canvas-area');
    const tiles = [...canvas.querySelectorAll('.port-tile')];
    const bounds = node => {
      const {top, right, bottom, left, height, width} = node.getBoundingClientRect();
      return {top, right, bottom, left, height, width};
    };
    // Exercise the height of a host and a guest sharing one port.
    const names = tiles[2].querySelector('.port-label');
    const original = [...names.childNodes];
    const title = canvas.querySelector('#switch-title');
    const originalTitle = title.textContent;
    try {
      title.textContent = 'USW Pro XG 8 PoE';
      names.replaceChildren(...['Home server', 'Home Assistant'].map(text => {
        const label = document.createElement('span'); label.textContent = text; return label;
      }));
      return {
        header: bounds(document.querySelector('.titlebar')), canvas: bounds(canvas),
        overflowY: canvas.scrollHeight - canvas.clientHeight, overflowX: canvas.scrollWidth - canvas.clientWidth,
        ports: tiles.map(bounds), sockets: tiles.map(tile => bounds(tile.querySelector('.port-led'))),
        bandwidth: bounds(canvas.querySelector('.port-map .uplink-bandwidth')),
        controls: bounds(canvas.querySelector('.selection-toolbar')), namesHeight: names.clientHeight, namesScroll: names.scrollHeight,
        brightness: bounds(document.querySelector('#master')), apply: bounds(document.querySelector('#apply-draft'))
      };
    } finally { names.replaceChildren(...original); title.textContent = originalTitle; }
  })()`);
  const {header, canvas, ports, sockets, bandwidth, brightness, apply} = geometry;
  assert.equal(ports.length, 10);
  assert.ok(header.height <= 46, `Title bar is ${header.height}px`);
  assert.ok(geometry.overflowX <= 1 && geometry.overflowY <= 1,
    `Faceplate overflows by ${geometry.overflowX} × ${geometry.overflowY} at ${width} × ${height}: ${JSON.stringify(geometry)}`);
  for (const [index, port] of ports.entries()) {
    assert.ok(port.top >= canvas.top && port.bottom <= canvas.bottom + 1);
    assert.ok(port.left >= canvas.left && port.right <= canvas.right + 1);
    assert.ok(port.width >= 44 && port.height >= 44);
    if (index) assert.ok(port.left >= ports[index - 1].right - 1, 'Ports must share one row in physical order');
    assert.ok(Math.abs(sockets[index].top - sockets[0].top) <= 4, 'Socket rows must align');
  }
  assert.ok(bandwidth.width > 0 && bandwidth.top >= canvas.top && bandwidth.bottom <= canvas.bottom + 1);
  if (rack && height > 360) {
    assert.ok(bandwidth.top >= Math.max(...ports.map(port=>port.bottom))-1, 'Rack traffic history sits below the ports');
    assert.ok(bandwidth.bottom <= brightness.top + 1, 'Traffic and brightness stay visible together');
  } else assert.ok(bandwidth.bottom <= ports[0].top);
  assert.ok(brightness.width >= 70 && brightness.bottom <= height && brightness.top >= header.bottom);
  assert.ok(apply.width > 0 && apply.bottom <= height && apply.left >= brightness.right, 'Draft and brightness stay accessible together');
  if (rack) {
    assert.ok(brightness.height >= 44 && apply.height >= 44);
    assert.ok(bandwidth.right <= geometry.controls.left + 1 || bandwidth.bottom <= geometry.controls.top + 1 || bandwidth.top >= geometry.controls.bottom - 1,
      'Uplink readings must clear selection controls');
    assert.ok(geometry.namesScroll <= geometry.namesHeight + 1, 'Both client names must fit');
  }
  return {headerHeight: header.height, canvasHeight: canvas.height, overflowY: geometry.overflowY, portsInRow: ports.length};
}
async function checkPhoneFooter(width, height, bottomInset) {
  await send('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor: 1, mobile: true});
  await send('Emulation.setSafeAreaInsetsOverride', {insets: {top: bottomInset ? 59 : 0, bottom: bottomInset}});
  await send('Emulation.setEmulatedMedia', {features: [{name: 'prefers-reduced-motion', value: 'reduce'}]});
  await evaluate("document.body.style.height = 'calc(100dvh - 59px)'");
  const workspace = '.rack-workspace-dialog';
  const brightness = async () => (await (await fetch(url + 'api/scene')).json()).scene.brightness;
  const original = await brightness();
  const key = original > 0 ? 'ArrowLeft' : 'ArrowRight';
  const target = original + (original > 0 ? -1 : 1);
  async function openWorkspace() {
    await evaluate(`document.querySelector('#rack-workspace-toggle').scrollIntoView({block:'nearest'})`);
    await tap('#rack-workspace-toggle');
    await until(() => evaluate(`!!document.querySelector('${workspace}:modal')`), 'mobile Workspace');
  }
  async function stageBrightness() {
    await evaluate(`document.querySelector('#master').scrollIntoView({block:'nearest'});document.querySelector('#master').focus()`);
    await send('Input.dispatchKeyEvent', {type: 'keyDown', key, code: key, windowsVirtualKeyCode: key === 'ArrowLeft' ? 37 : 39});
    await send('Input.dispatchKeyEvent', {type: 'keyUp', key, code: key});
    assert.equal(await evaluate(`Number(document.querySelector('#master').value)`), target);
    const done = workspace + ' .dialog-header button';
    await evaluate(`document.querySelector('${done}').scrollIntoView({block:'nearest'})`);
    await tap(done);
    assert.equal(await evaluate('document.activeElement.id'), 'rack-workspace-toggle');
  }

  try {
    await frame();
    const idle = await evaluate(`(() => {
      const status = document.querySelector('.statusbar'), message = document.querySelector('#status');
      const connection = document.querySelector('#connection'), controller = document.querySelector('#controller-connection');
      const previous = {connection:connection.className, controller:controller.className, hidden:controller.hidden, message:message.className};
      try {
        controller.hidden = false; controller.className = 'connection-state is-disconnected';
        connection.className = 'connection-state online';
        const normal = status.getBoundingClientRect().height;
        const safeBottom = visualViewport.height - document.querySelector('.studio-content').getBoundingClientRect().bottom;
        connection.className = 'connection-state waiting';
        const interrupted = connection.getBoundingClientRect().height;
        connection.className = 'connection-state online'; message.classList.add('error');
        const error = message.getBoundingClientRect().height;
        const errorInset = visualViewport.height - status.getBoundingClientRect().bottom;
        return {dock:document.querySelector('.output-dock').getBoundingClientRect().height, normal, safeBottom, interrupted, error, errorInset};
      } finally {
        controller.hidden = previous.hidden; controller.className = previous.controller;
        connection.className = previous.connection; message.className = previous.message;
      }
    })()`);
    assert.equal(idle.dock, 0, 'The idle phone dock takes no space');
    assert.equal(idle.normal, 0, 'An optional disconnected controller does not add a status row');
    assert.ok(Math.abs(idle.safeBottom) <= 1, `Idle port map leaves ${idle.safeBottom}px below its scroller`);
    assert.ok(Math.abs(idle.errorInset - bottomInset) <= 1, 'Reported errors clear the home indicator');
    const edge = await evaluate(`(() => {
      const scroller = document.querySelector('.studio-content');
      document.querySelector('#select-none').click();
      const maxScroll = scroller.scrollHeight - scroller.clientHeight;
      scroller.scrollTop = Math.round(maxScroll / 2);
      const y = Math.floor(visualViewport.height) - 1;
      const hit = document.elementFromPoint(Math.floor(visualViewport.width / 2), y);
      const midScroll = scroller.scrollTop;
      scroller.scrollTop = scroller.scrollHeight;
      const last = document.querySelectorAll('.port-tile.sfp');
      const port = last[last.length - 1];
      return {midScroll, maxScroll, edgeHitsMap: Boolean(hit?.closest('.canvas-area')),
        lastPort: port.dataset.port, clearance: visualViewport.height - port.getBoundingClientRect().bottom};
    })()`);
    assert.ok(edge.midScroll > 0 && edge.midScroll < edge.maxScroll, 'Exercise the edge while the port list is between scroll limits');
    assert.ok(edge.edgeHitsMap, 'The last pixel of the visible viewport belongs to the scrolling port map');
    assert.ok(edge.clearance >= bottomInset - 1, 'The last SFP port scrolls completely above the home indicator');
    const opticalPort = `.port-tile.sfp[data-port="${edge.lastPort}"]`;
    await tap(opticalPort, true);
    assert.equal(await evaluate(`document.querySelector('${opticalPort}').getAttribute('aria-pressed')`), 'true');
    await evaluate("document.querySelector('#open-port-controls').scrollIntoView({block:'nearest'})");
    await tap('#open-port-controls', true);
    const inspectorInset = await evaluate("visualViewport.height - document.querySelector('.studio-content.is-editing-ports').getBoundingClientRect().bottom");
    assert.ok(Math.abs(inspectorInset - bottomInset) <= 1, 'Port controls clear the home indicator');
    await tap('#back-to-ports', true);
    assert.ok(idle.interrupted > 0 && idle.error > 0, 'Switch interruptions and status errors stay visible');
    await openWorkspace();
    assert.ok(await evaluate(`['master','open-map-key','connection','controller-connection'].every(id=>
      document.querySelectorAll('#'+id).length===1 && document.querySelector('${workspace}').contains(document.getElementById(id)))`));
    for (const selector of ['#master', '#open-map-key']) {
      const control = await bounds(selector);
      assert.ok(control.height >= 44 && control.width >= 44 && control.x >= 0 && control.x + control.width <= width + 1,
        `${selector} fits the mobile Workspace with a full touch target`);
    }
    await evaluate("document.querySelector('#open-map-key').scrollIntoView({block:'nearest'})");
    await tap('#open-map-key');
    assert.ok(await evaluate(`document.querySelector('.map-key:modal')?.textContent.includes('25 GbE')`));
    await tap('.map-key .dialog-header button');
    assert.equal(await evaluate('document.activeElement.id'), 'open-map-key');
    assert.ok(await evaluate(`!!document.querySelector('${workspace}:modal')`));
    await stageBrightness();
    const dock = await bounds('.output-dock');
    const buttons = [];
    for (const selector of ['#compare-draft', '#cancel-draft', '#apply-draft']) buttons.push(await bounds(selector));
    assert.ok(dock.height >= 44 && dock.height <= 48, 'A pending change uses only one row');
    assert.ok(Math.abs(height - dock.bottom - bottomInset) <= 1, 'Draft actions sit above the phone safe area');
    for (const [i, control] of buttons.entries()) {
      assert.ok(control.height >= 44 && control.width >= 44 && control.x >= 0 && control.x + control.width <= width + 1);
      assert.ok(Math.abs(control.y - buttons[0].y) <= 1 && (!i || control.x >= buttons[i - 1].x + buttons[i - 1].width - 1));
    }
    const toastBlockers = await evaluate(`(() => {
      const toast = document.querySelector('#toast'), message = document.querySelector('#toast-message');
      const dismiss = document.querySelector('#toast-dismiss');
      const prior = {hidden: toast.hidden, error: toast.classList.contains('error'), message: message.textContent, dismiss: dismiss.hidden};
      try {
        toast.hidden = false; toast.classList.remove('error'); message.textContent = 'Changes applied'; dismiss.hidden = true;
        return ['#compare-draft', '#cancel-draft', '#apply-draft'].map(${blockedTouchTarget}).filter(Boolean);
      } finally {
        toast.hidden = prior.hidden; toast.classList.toggle('error', prior.error); message.textContent = prior.message; dismiss.hidden = prior.dismiss;
      }
    })()`);
    assert.deepEqual(toastBlockers, [], 'A normal notification must leave every draft action tappable');
    await tap('#compare-draft', true);
    assert.equal(await evaluate("document.querySelector('#compare-draft').getAttribute('aria-pressed')"), 'true');
    await tap('#cancel-draft', true);
    assert.equal((await bounds('.output-dock')).height, 0);
    assert.equal(await brightness(), original, 'Discard preserves applied brightness');
    await openWorkspace();
    await stageBrightness();
    await tap('#apply-draft', true);
    await until(() => evaluate("document.querySelector('#draft-actions').hidden"), 'applied phone brightness');
    assert.equal(await brightness(), target, 'Applying the phone draft sets the simulator brightness');
    assert.equal((await bounds('.output-dock')).height, 0);
    return {width, height, bottomInset, idle, edge, inspectorInset, pendingHeight: dock.height};
  } finally {
    await evaluate("document.querySelector('.map-key[open]')?.close(); document.querySelector('.rack-workspace-dialog[open]')?.close(); if (!document.querySelector('#draft-actions').hidden) document.querySelector('#cancel-draft').click(); document.body.style.removeProperty('height')");
    await send('Emulation.setSafeAreaInsetsOverride', {insets: {}});
    await send('Emulation.setEmulatedMedia', {features: []});
  }
}
function blockedTouchTarget(selector) {
  const control = document.querySelector(selector);
  const box = control.getBoundingClientRect();
  const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
  if (control.contains(hit)) return null;
  return {selector, hit: hit ? [hit.tagName, hit.id, hit.className].filter(Boolean).join(' ') : 'outside viewport'};
}
async function tap(selector, verifyHit = false) {
  await evaluate(`new Promise((resolve,reject)=>{const start=performance.now();let last='',since=start;function check(time){const r=document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect();if(!r)return reject(new Error('Touch target disappeared'));const value=[r.x,r.y,r.width,r.height,document.querySelector('#inspector')?.scrollTop].join(',');if(value!==last){last=value;since=time;}if(time-since>=180)return resolve();if(time-start>4000)return reject(new Error('Touch target kept moving'));requestAnimationFrame(check);}requestAnimationFrame(check);})`);
  const r = await bounds(selector);
  assert.ok(r.y >= 0 && r.bottom <= await evaluate('innerHeight'), `Touch target is clipped: ${selector}`);
  if (verifyHit) assert.equal(await evaluate(`(${blockedTouchTarget})(${JSON.stringify(selector)})`), null, `Tap was intercepted: ${selector}`);
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
      const rate=formatBandwidth(index?sample.upload_bps:sample.download_bps);
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
    await new Promise(resolve=>{const start=performance.now();function step(time){samples.push(host.querySelector('.upload .bandwidth-value').textContent);if(time-start<750)requestAnimationFrame(step);else resolve();}requestAnimationFrame(step);});
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
  await evaluate('document.querySelector("#browse-effects").click()');
  await until(() => evaluate('!!document.querySelector("[data-effect=traffic] .effect-card-open")'), 'Live Traffic preview');
  await evaluate('document.querySelector("[data-effect=traffic] .effect-card-open").click()');
  await until(() => evaluate('!!document.querySelector("#inspector")'), 'long port controls');
  report.inspector_scroll = [];
  for (const [width, height] of [[1920, 1080], [1366, 768], [1280, 800], [1100, 700], [1099, 700], [1024, 600], [390, 844], [1280, 400], [1272, 394], [1024, 320], [1024, 400]]) {
    await send('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor: 1, mobile: false});
    await frame();
    const layout = await evaluate(`({width: innerWidth, height: innerHeight, scrollWidth: document.documentElement.scrollWidth})`);
    assert.ok(layout.scrollWidth <= width + 1, 'Page overflows viewport');
    if (width >= 1100 || width >= 1024 && height <= 500) layout.faceplate = await checkFaceplate(width, height);
    report.sizes.push(layout);
    if (width >= 1100 || width >= 900 && height <= 500) {
      const initial = await bounds('.canvas-area');
      const draftOpen=await evaluate('!document.querySelector("#draft-actions").hidden');
      await evaluate('document.querySelector("#collapse-inspector").click()');
      await frame();
      const expanded=await bounds('.canvas-area');
      assert.equal((await bounds('#inspector')).width,0);
      assert.ok(expanded.width >= initial.width + 200 && expanded.width >= width-2,'Canvas uses the released inspector column');
      assert.equal(await evaluate('document.activeElement.id'),'expand-inspector');
      assert.equal(await evaluate('!document.querySelector("#draft-actions").hidden'),draftOpen);
      await evaluate('document.querySelector("#expand-inspector").click()');
      await frame();
      assert.equal((await bounds('.canvas-area')).width,initial.width);
      const scrollLayout = await evaluate(`(() => {
        const outer=document.querySelector('.studio-content'), inspector=document.querySelector('#inspector');
        return {outerHeight:outer.clientHeight,outerScroll:outer.scrollHeight,inspectorHeight:inspector.clientHeight,
          inspectorScroll:inspector.scrollHeight,scrollbar:getComputedStyle(inspector).scrollbarWidth};
      })()`);
      assert.equal(scrollLayout.outerHeight,scrollLayout.outerScroll,'The split workspace must not add an outer scrollbar');
      assert.equal(scrollLayout.inspectorHeight,scrollLayout.outerHeight,'Port controls fit their own pane');
      assert.equal(scrollLayout.scrollbar,'none','Port controls scroll without a visible scrollbar');
      if (scrollLayout.inspectorScroll > scrollLayout.inspectorHeight) {
        await evaluate('document.querySelector("#inspector").scrollTop=0;document.querySelector("#master").focus({preventScroll:true})');
        const before=await evaluate('({map:document.querySelector(".canvas-area").scrollTop,outer:document.querySelector(".studio-content").scrollTop,focus:document.activeElement.id})');
        const box=await bounds('#inspector');
        await send('Input.dispatchMouseEvent',{type:'mouseMoved',x:box.x+8,y:box.y+30});
        await send('Input.dispatchMouseEvent',{type:'mouseWheel',x:box.x+8,y:box.y+30,deltaY:180,deltaX:0});
        await until(()=>evaluate('document.querySelector("#inspector").scrollTop>0'),'wheel scroll without focus');
        const after=await evaluate('({map:document.querySelector(".canvas-area").scrollTop,outer:document.querySelector(".studio-content").scrollTop,focus:document.activeElement.id})');
        assert.deepEqual(after,before,'Hover wheel must not move the map or require editor focus');
        await evaluate('document.querySelector("#inspector").scrollTop=0');
        report.inspector_scroll.push({width,height,hover_wheel:true,...scrollLayout});
      }
    }
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
    await tap('#open-map-key');
    assert.ok(await evaluate('document.querySelector(".map-key").open && document.querySelector(".map-key").textContent.includes("25 GbE")'));
    await tap('.map-key .dialog-header button');
    assert.equal(await evaluate('document.activeElement.id'), 'open-map-key');
    if(width===1280){
      const swatch=await evaluate('document.querySelector("[data-color]")?.dataset.color');
      assert.ok(swatch && swatch!=='manual','Test an effect parameter before testing fixed color');
      const selector='[data-color="'+swatch+'"]';
      await evaluate(`document.querySelector(${JSON.stringify(selector)}).scrollIntoView({block:'center'})`);
      await tap(selector);
      await until(()=>evaluate('!!document.querySelector("dialog.color-popover:modal")'),'touch color editor');
      const outsideX=40,outsideY=100;
      await send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:outsideX,y:outsideY,id:1}]});
      await send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
      await frame();
      assert.ok(await evaluate('!!document.querySelector("dialog.color-popover:modal")'),'A missed touch leaves the editor open');
      const original=await evaluate(`document.querySelector(${JSON.stringify(selector)}).value`);
      await tap('.color-sv');
      await tap('.color-popup-footer button');
      assert.notEqual(await evaluate(`document.querySelector(${JSON.stringify(selector)}).value.toUpperCase()`),original.toUpperCase());
      await evaluate('document.querySelector("#inspector").scrollTop=0');
    }
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
  report.phone_footer = [];
  for (const [width, height, bottomInset] of [[320, 640, 34], [390, 844, 34], [390, 844, 0], [430, 932, 34]]) {
    report.phone_footer.push(await checkPhoneFooter(width, height, bottomInset));
  }
  await send('Emulation.setDeviceMetricsOverride', {width: 390, height: 844, deviceScaleFactor: 1, mobile: false});
  await evaluate(`document.querySelector('[data-select="ethernet"]').click()`);
  await evaluate(`document.querySelector('[data-page="Effects"]').click()`);
  const includeSfp = '.gallery-target input[type="checkbox"]';
  await until(() => evaluate(`!!document.querySelector('${includeSfp}')`), 'SFP effect option');
  assert.equal(await evaluate(`document.querySelector('${includeSfp}').checked`), false);
  await evaluate(`document.querySelector('${includeSfp}').click(); document.querySelector('[data-category=Activity]').click()`);
  const activityCard = '[data-effect="activity_glow"] .effect-card-open';
  await until(() => evaluate(`document.querySelector('${activityCard}')?.getClientRects().length`), 'Activity Glow');
  assert.ok(await evaluate('document.documentElement.scrollWidth <= innerWidth + 1'));
  await screenshot('phone-sfp-activity');
  await send('Emulation.setDeviceMetricsOverride', {width: 1366, height: 768, deviceScaleFactor: 1, mobile: false});
  const readScene = async () => (await (await fetch(url + 'api/scene')).json()).scene;
  const original = await readScene();
  await evaluate(`document.querySelector('${activityCard}').click()`);
  await until(() => evaluate('!!document.querySelector("[data-param=include_uplinks]")'), 'uplink traffic option');
  await evaluate('document.querySelector("[data-param=include_uplinks]").click()');
  assert.deepEqual(await readScene(), original, 'Effect selection and uplink edits stay in preview until Apply');
  await evaluate('document.querySelector("#apply-draft").click()');
  const assigned = await until(async () => (await readScene()).zones.find(zone =>
    zone.effect.id === 'activity_glow' && zone.effect.params.include_uplinks && zone.ports.length === 10), 'mixed activity group');
  assert.deepEqual(assigned.ports, [1,2,3,4,5,6,7,8,9,10]);
  await until(() => evaluate('document.querySelector("#draft-actions").hidden && !document.querySelector("#undo-last").disabled'), 'Apply confirmed in workspace');
  const previousDocument = await evaluate('performance.timeOrigin');
  await send('Page.reload', {ignoreCache: true});
  await until(async () => {
    try {
      return await evaluate(`performance.timeOrigin !== ${previousDocument} &&
        document.querySelectorAll(".port-socket-rim").length === 10 && !!document.querySelector('[data-port="9"]')`);
    } catch (error) {
      if (/execution context was destroyed|cannot find (?:default )?(?:execution )?context/i.test(error.message)) return false;
      throw error;
    }
  }, 'port 9 in the reloaded lighting workspace');
  await evaluate(`document.querySelector('[data-port="9"]').click()`);
  const restore = `[...document.querySelectorAll('#inspector button')].find(button => button.textContent === 'Return to SFP lighting')`;
  await until(() => evaluate(`!!(${restore})`), 'restore SFP control');
  await evaluate(`(${restore}).click(); document.querySelector('#apply-draft').click()`);
  const restored = await until(async () => {
    const scene = await readScene();
    return scene.zones.every(zone => !zone.ports.includes(9)) && scene;
  }, 'individual SFP removal');
  assert.deepEqual(restored.zones.find(zone => zone.id === assigned.id).ports,[1,2,3,4,5,6,7,8,10]);
  assert.deepEqual(restored.sfp, original.sfp);
  assert.deepEqual(restored.sfp_sync, original.sfp_sync);
  report.sfp_activity = {included: assigned.ports, restored: 9};
  report.product_workflows=await checkProductWorkflows({evaluate,until,send,screenshot,url});
  report.themes=await checkThemeWorkflows({evaluate,until,send,screenshot,url});
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
