import test from 'node:test';
import assert from 'node:assert/strict';
import { MutationRecovery,SfpEdits,createState } from '../src/state.mjs';
import { LatestQueue } from '../src/api.mjs';

function deferred() {let resolve,reject;const promise = new Promise((done,fail) => {resolve = done;reject = fail;});return {promise,resolve,reject};}
function fixture() {
  const response = deferred(),context = {backend:1,sfp:1},applied = [],failures = [],changes = [];
  let reads = 0;
  const recovery = new MutationRecovery({
    generation:() => `${context.backend}:${context.sfp}`,
    read:() => {reads++;return response.promise;},
    apply:data => applied.push(data),onFailure:error => failures.push(error),onPending:value => changes.push(value)
  });
  return {recovery,response,context,applied,failures,changes,reads:() => reads};
}

test('shared recovery stays pending until authoritative state arrives and coalesces callers',async () => {
  const f = fixture();
  const first = f.recovery.run(),second = f.recovery.run();
  assert.equal(f.recovery.pending,true);assert.equal(f.reads(),1);
  assert.deepEqual(f.changes,[true]);assert.deepEqual(f.applied,[]);
  f.response.resolve({state:{brightness:35},ports:[]});await Promise.all([first,second]);
  assert.equal(f.recovery.pending,false);assert.deepEqual(f.changes,[true,false]);
  assert.deepEqual(f.applied,[{state:{brightness:35},ports:[]}]);assert.deepEqual(f.failures,[]);
});

test('a backend or SFP workspace generation change discards the old resync',async () => {
  for (const key of ['backend','sfp']) {
    const f = fixture(),waiting = f.recovery.run();
    f.context[key]++;
    assert.equal(f.recovery.pending,true,'changes stay blocked while the old queue is recovering');
    f.response.resolve({state:{brightness:1}});await waiting;
    assert.deepEqual(f.applied,[],key);assert.deepEqual(f.failures,[],key);
    assert.equal(f.recovery.pending,false);assert.deepEqual(f.changes,[true,false]);
  }
});

test('an old resync failure cannot mark a newer connection as disconnected',async () => {
  const f = fixture(),waiting = f.recovery.run();
  f.context.backend++;f.context.sfp++;f.response.reject(new Error('old connection closed'));
  await waiting;
  assert.deepEqual(f.failures,[]);assert.deepEqual(f.applied,[]);assert.equal(f.recovery.pending,false);
});

test('a current recovery failure reports once, unlocks controls and permits a later retry',async () => {
  const f = fixture(),waiting = f.recovery.run(),failure = new Error('unavailable');
  f.response.reject(failure);await waiting;
  assert.deepEqual(f.failures,[failure]);assert.equal(f.recovery.pending,false);
  f.recovery.read = async () => ({state:{brightness:75}});
  await f.recovery.run();assert.deepEqual(f.applied,[{state:{brightness:75}}]);
  assert.deepEqual(f.changes,[true,false,true,false]);
});

test('a failed SFP write keeps shared recovery active until the ordinary state and SFP state are resynced',async () => {
  const response = deferred(),write = deferred(),started = deferred(),edits = new SfpEdits(createState().sfp),sent = [];
  let brightness = 80;
  const recovery = new MutationRecovery({
    generation:() => edits.generation,
    read:() => response.promise,
    apply:data => {brightness = data.brightness;edits.receive(data.sfp);},
    onFailure:assert.fail,
    onPending:value => {if (value) {edits.rollback();started.resolve();}}
  });
  const queue = new LatestQueue(async (path,request) => {
    sent.push(path);
    if (path === '/sfp/9') {await write.promise;throw new Error('rejected');}
    brightness = request.value;
  },() => recovery.run());
  queue.push('/sfp/9',edits.edit(9,{mode:'color',color:'#2468AC'}));
  queue.push('/brightness',{value:20});brightness = 20;
  write.resolve();await started.promise;
  assert.equal(recovery.pending,true);assert.equal(edits.values[0].mode,'unmanaged');
  assert.equal(queue.pending.size,0);assert.deepEqual(sent,['/sfp/9']);
  response.resolve({brightness:80,sfp:createState().sfp});await queue.idle();
  assert.equal(recovery.pending,false);assert.equal(brightness,80);
  queue.push('/brightness',{value:40});await queue.idle();
  assert.equal(brightness,40);assert.deepEqual(sent,['/sfp/9','/brightness']);
});
