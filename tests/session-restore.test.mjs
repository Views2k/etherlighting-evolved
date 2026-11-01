import test from 'node:test';
import assert from 'node:assert/strict';
import { Backend,SessionRestore } from '../src/api.mjs';

function deferred() {let resolve;const promise = new Promise(done => {resolve = done;});return {promise,resolve};}
function fixture() {
  const updates = [],entries = [],errors = [];
  const backend = {request:async path => path === '/session' ? {logged_in:false,connecting:true,has_saved_login:true} : {Evening:{}},read:async () => ({state:{connected:true},ports:[]})};
  let available = true;
  const restore = new SessionRestore(backend,{available:() => available,update:value => updates.push(value),enter:(...args) => {entries.push(args);available = false;},onError:error => errors.push(error)});
  return {restore,backend,updates,entries,errors,setAvailable:value => {available = value;}};
}

test('a saved connection completing after the first session read opens the workspace',async () => {
  const f = fixture();
  await f.restore.check();
  assert.equal(f.updates[0].connecting,true);assert.equal(f.entries.length,0);
  await f.restore.check({logged_in:true,connected:true,has_saved_login:true});
  assert.equal(f.entries.length,1);assert.equal(f.entries[0][1].state.connected,true);
  assert.deepEqual(f.errors,[]);
});

test('an older startup response cannot overwrite a completed login',async () => {
  const f = fixture(),initial = deferred();
  f.backend.request = path => path === '/session' ? initial.promise : Promise.resolve({});
  const waiting = f.restore.check();
  await f.restore.check({logged_in:true,connected:true});
  initial.resolve({logged_in:false,connecting:true});await waiting;
  assert.equal(f.entries.length,1);assert.equal(f.updates.length,1);
});

test('manual login, demo navigation and logout cancel an in-flight restoration',async () => {
  for (const action of ['manual login','demo','logout']) {
    const f = fixture(),read = deferred();f.backend.read = () => read.promise;
    const waiting = f.restore.check({logged_in:true});
    f.restore.cancel();f.setAvailable(false);
    read.resolve({state:{connected:true},ports:[]});await waiting;
    assert.deepEqual(f.entries,[],action);assert.deepEqual(f.errors,[],action);
  }
});

test('a disconnected session supersedes stale state reads and reports startup failure',async () => {
  const f = fixture(),read = deferred();f.backend.read = () => read.promise;
  const waiting = f.restore.check({logged_in:true});
  const failure = {logged_in:false,connecting:false,error:{code:'auth_failed'},has_saved_login:true};
  await f.restore.check(failure);read.resolve({state:{},ports:[]});await waiting;
  assert.deepEqual(f.entries,[]);assert.deepEqual(f.updates.at(-1),failure);
});

test('restoration reports a state loading failure without erasing saved login',async () => {
  const f = fixture(),error = {code:'unavailable'};f.backend.read = async () => {throw error;};
  await f.restore.check({logged_in:true,has_saved_login:true});
  assert.deepEqual(f.errors,[error]);assert.equal(f.updates[0].has_saved_login,true);
});

test('repeated logged-in notifications share a slow restoration and enter with latest metadata',async () => {
  const f = fixture(),presets = deferred(),calls = [];
  f.backend.read = async () => {calls.push('read');return {state:{connected:true},ports:[]};};
  f.backend.request = path => {calls.push(path);return presets.promise;};
  const first = {logged_in:true,connecting:true,has_saved_login:true,host:'fixture',port:22,username:'fixture',simulated:false};
  const waiting = [f.restore.check(first)];
  for (let i = 0;i < 5;i++) waiting.push(f.restore.check({...first,connecting:false}));
  assert.deepEqual(calls,['read','/presets']);
  presets.resolve({Evening:{}});await Promise.all(waiting);
  assert.equal(f.entries.length,1);assert.equal(f.entries[0][0].connecting,false);
  assert.deepEqual(f.entries[0][2],{Evening:{}});
});

test('a different session replaces restoration without admitting the previous workspace',async () => {
  const f = fixture(),reads = [deferred(),deferred()];let next = 0;
  f.backend.read = () => reads[next++].promise;
  const old = f.restore.check({logged_in:true,host:'fixture-a',simulated:false});
  const current = f.restore.check({logged_in:true,host:'fixture-b',simulated:true});
  reads[0].resolve({state:{connected:true},ports:[{port:1}]});await old;
  assert.equal(f.entries.length,0);
  reads[1].resolve({state:{connected:true},ports:[{port:2}]});await current;
  assert.equal(f.entries.length,1);assert.equal(f.entries[0][0].host,'fixture-b');
  assert.deepEqual(f.entries[0][1].ports,[{port:2}]);
});

test('cancel invalidates every waiter without requiring the availability callback to change',async () => {
  const f = fixture(),read = deferred();f.backend.read = () => read.promise;
  const first = f.restore.check({logged_in:true}),second = f.restore.check({logged_in:true});
  f.restore.cancel();read.resolve({state:{connected:true},ports:[]});
  await Promise.all([first,second]);assert.equal(f.entries.length,0);assert.equal(f.errors.length,0);
});

test('a shared restoration failure is reported once and permits a later retry',async () => {
  const f = fixture(),read = deferred();f.backend.read = async () => {await read.promise;throw {code:'unavailable'};};
  const first = f.restore.check({logged_in:true}),second = f.restore.check({logged_in:true});
  read.resolve();await Promise.all([first,second]);assert.equal(f.errors.length,1);
  f.backend.read = async () => ({state:{connected:true},ports:[]});
  await f.restore.check({logged_in:true});assert.equal(f.entries.length,1);
});

test('login watching and workspace entry share one socket; close ignores stale events',() => {
  const originalSocket = globalThis.WebSocket,originalLocation = globalThis.location;
  const sockets = [],messages = [];
  globalThis.location = {href:'http://localhost/',protocol:'http:'};
  globalThis.WebSocket = class {constructor() {sockets.push(this);}close() {this.onclose?.();}};
  try {
    const backend = new Backend();backend.onMessage = message => messages.push(message);
    backend.subscribe();backend.subscribe();assert.equal(sockets.length,1);
    backend.close();assert.equal(backend.retry,null);
    sockets[0].onmessage({data:JSON.stringify({type:'session',session:{logged_in:true}})});
    assert.deepEqual(messages,[]);
    backend.subscribe();assert.equal(sockets.length,2);backend.close();
  } finally {globalThis.WebSocket = originalSocket;globalThis.location = originalLocation;}
});

test('token sessions report pending login before attempting state reads',async () => {
  const backend = new Backend(),messages = [],calls = [];
  backend.closed = false;backend.token = 'fixture-token';
  backend.request = async path => {calls.push(path);return {logged_in:false,connecting:true};};
  backend.onMessage = message => {messages.push(message);backend.close();};
  await backend.poll();
  assert.deepEqual(calls,['/session']);assert.equal(messages[0].session.connecting,true);
  assert.equal(backend.retry,null);
});

test('an earlier token poll cannot publish or reschedule after close and resubscribe',async () => {
  for (const stage of ['session','state']) {
    const backend = new Backend(),old = deferred(),current = deferred(),reading = deferred(),tasks = [],messages = [];
    const poll = backend.poll.bind(backend);backend.poll = generation => {const task = poll(generation);tasks.push(task);return task;};
    let requests = 0;
    backend.request = async () => ++requests === 1 ? stage === 'session' ? old.promise : {logged_in:true} : current.promise;
    backend.read = () => {reading.resolve();return old.promise;};
    backend.onMessage = message => {messages.push(message);if (message.session?.marker === 'current') backend.close();};
    backend.token = 'fixture-old';backend.subscribe();
    if (stage === 'state') await reading.promise;
    backend.close();backend.token = 'fixture-current';backend.subscribe();
    const count = messages.length;
    old.resolve(stage === 'session' ? {logged_in:false,marker:'old'} : {state:{connected:true},ports:[]});
    await tasks[0];
    assert.equal(messages.length,count,stage);assert.equal(backend.retry,null,stage);
    current.resolve({logged_in:false,marker:'current'});await tasks[1];
    assert.equal(messages.at(-1).session.marker,'current');assert.equal(backend.retry,null);
  }
});

test('a stale state read cannot replace the new subscription catalog',async () => {
  const backend = new Backend(),effects = deferred();
  backend.request = async path => path === '/effects' ? effects.promise : path === '/state' ? {} : [];
  const reading = backend.read();backend.close();backend.effects = [{id:'current'}];
  effects.resolve([{id:'old'}]);await reading;
  assert.deepEqual(backend.effects,[{id:'current'}]);
});
