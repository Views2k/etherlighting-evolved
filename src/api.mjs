export class ApiError extends Error { constructor(code,status) {super(code);this.code = code;this.status = status;} }
export const friendlyError = error => ({
  authentication_failed:'The switch rejected those credentials. Check your SSH username and password.',
  auth_failed:'The switch rejected those credentials. Check your SSH username and password.',
  bad_token:'This backend requires an API token. Open Connection options to enter it.',
  host_key_changed:'The switch identity has changed. Verify its SSH host key before reconnecting.',
  connect_failed:'The switch could not be reached. Check the address and that SSH is enabled.',
  unreachable:'The switch could not be reached. Check the address and that SSH is enabled.',
  ssh_error:'SSH could not establish a connection. Check the switch address and its SSH settings.',
  no_etherlighting:'The switch responded, but per-port lighting is unavailable on this connection.',
  invalid:'One of these values was not accepted. Check the settings and try again.',
  timeout:'The connection timed out. Check the switch and try again.',
  unavailable:'The local connection service is unavailable. Start the EE backend and try again.',
  missing_endpoint:'This backend does not provide that control yet.',
  controller_update_required:'Update the EE controller to apply this effect. Port colors are still active.',
  change_in_progress:'Wait for the effect change to finish.',
  saved_login_failed:'The login could not be saved securely. Turn off Save login and connect again.',
  saved_login_unavailable:'The saved login could not be opened. Enter your SSH credentials to connect again.',
  saved_login_forget_failed:'The saved login could not be removed. It is still saved on this computer.',
  not_logged_in:'The switch session ended. Connect again to continue.'
}[error?.code] || 'That change was not confirmed. Check the connection and try again.');
export class Backend {
  constructor() {this.token = '';this.effects = null;this.socket = null;this.onMessage = () => {};this.onDisconnect = () => {};this.closed = true;this.retry = null;this.attempt = 0;this.generation = 0;}
  async request(path,method = 'GET',body,timeout = 12000) {
    let response;
    try {response = await fetch(`/api${path}`,{method,headers:{...(body !== undefined ? {'Content-Type':'application/json'} : {}),...(this.token ? {Authorization:`Bearer ${this.token}`} : {})},body:body === undefined ? undefined : JSON.stringify(body),signal:AbortSignal.timeout(timeout),cache:'no-store'});}
    catch (e) {throw new ApiError(e.name === 'TimeoutError' ? 'timeout' : 'unavailable',0);}
    const payload = await response.json().catch(() => null);
    if (!response.ok) throw new ApiError(payload?.detail?.code || (response.status === 404 ? 'missing_endpoint' : response.status === 503 ? 'unavailable' : 'request_failed'),response.status);
    return payload;
  }
  async login(host,username,password,remember = false) {return this.request('/session/login','POST',{host,username,password,remember:remember === true},25000);}
  async forgetLogin() {return this.request('/session/forget','POST');}
  async read() {
    const generation = this.generation;
    const [state,ports,effects] = await Promise.all([this.request('/state'),this.request('/ports'),this.effects || this.request('/effects')]);
    if (generation === this.generation) this.effects = effects;
    return {state,ports};
  }
  subscribe() {
    if (!this.closed) return;
    this.closed = false;
    const generation = ++this.generation;
    if (this.token) {this.poll(generation);return;}
    this.connectSocket(generation);
  }
  connectSocket(generation = this.generation) {
    if (this.closed || generation !== this.generation) return;
    const url = new URL('/api/ws',location.href);url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    // Keep API tokens out of URLs and access logs.
    const socket = this.socket = new WebSocket(url);
    socket.onopen = () => {if (this.socket === socket) this.attempt = 0;};
    socket.onmessage = event => {if (this.socket !== socket) return;try {this.onMessage(JSON.parse(event.data));} catch { /* The next valid frame remains authoritative. */ }};
    socket.onclose = () => {if (this.socket !== socket) return;this.socket = null;if (!this.closed) {this.onDisconnect();this.retry = setTimeout(() => this.connectSocket(generation),Math.min(15000,1000 * 2 ** this.attempt++));}};
  }
  async poll(generation = this.generation) {
    if (this.closed || generation !== this.generation) return;
    try {
      const session = await this.request('/session');
      if (this.closed || generation !== this.generation) return;
      this.onMessage({type:'session',session});
      if (this.closed || generation !== this.generation) return;
      if (session.logged_in) {const {state,ports} = await this.read();if (this.closed || generation !== this.generation) return;this.onMessage({type:'state',state});if (this.closed || generation !== this.generation) return;this.onMessage({type:'frame',connected:state.connected,ports});}
    } catch {if (!this.closed && generation === this.generation) this.onDisconnect();}
    if (!this.closed && generation === this.generation) this.retry = setTimeout(() => this.poll(generation),1000);
  }
  close() {this.closed = true;this.generation++;clearTimeout(this.retry);const socket = this.socket;this.socket = null;socket?.close();this.token = '';this.effects = null;}
}
export class SessionRestore {
  constructor(backend,{available,update,enter,onError}) {this.backend = backend;this.available = available;this.update = update;this.enter = enter;this.onError = onError;this.revision = 0;this.pending = null;}
  cancel() {this.revision++;this.pending = null;}
  async check(session) {
    const revision = ++this.revision;
    try {
      session ||= await this.backend.request('/session');
      if (revision !== this.revision) return;
      this.update(session);
      if (!session.logged_in || !this.available()) {this.pending = null;return;}
      const key = JSON.stringify([session.host,session.port,session.username,session.simulated]);
      if (this.pending?.key === key) {
        this.pending.revision = revision;this.pending.session = session;
        return await this.pending.promise;
      }
      const pending = this.pending = {key,revision,session};
      pending.promise = this.load(pending);
      await pending.promise;
    } catch (error) {if (revision === this.revision && this.available()) this.onError(error);}
  }
  async load(pending) {
    try {
      const [data,presets] = await Promise.all([this.backend.read(),this.backend.request('/presets')]);
      if (pending.revision === this.revision && this.available()) this.enter(pending.session,data,presets);
    } catch (error) {if (pending.revision === this.revision && this.available()) this.onError(error);}
    finally {if (this.pending === pending) this.pending = null;}
  }
}
export async function applyZoneEffect(backend,zone,config) {
  const state = await backend.request(`/zones/${encodeURIComponent(zone.id)}/effect`,'PUT',{...config,clear_overrides:true});
  if (zone.ports.some(port => state.overrides?.[port])) throw new ApiError('controller_update_required',409);
  return state;
}
export class EffectTransaction {
  constructor(onPending = () => {}) {this.pending = false;this.onPending = onPending;}
  async run(operation) {
    if (this.pending) throw new ApiError('change_in_progress',409);
    this.pending = true;this.onPending(true);
    try {return await operation();}
    finally {this.pending = false;this.onPending(false);}
  }
}
export class LatestQueue {
  constructor(send,onError) {this.send = send;this.onError = onError;this.pending = new Map();this.running = false;this.waiters = [];}
  push(key,value) {this.pending.set(key,value);this.drain();}
  async drain() {
    if (this.running) return;this.running = true;
    while (this.pending.size) {const [key,value] = this.pending.entries().next().value;this.pending.delete(key);
      try {await this.send(key,value);} catch (error) {this.pending.clear();await this.onError(error);this.pending.clear();break;}}
    this.running = false;this.waiters.splice(0).forEach(resolve => resolve());
  }
  async idle() {if (this.running) await new Promise(resolve => this.waiters.push(resolve));}
}
