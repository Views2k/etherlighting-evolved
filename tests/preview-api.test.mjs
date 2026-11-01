import test from 'node:test';
import assert from 'node:assert/strict';
import {request} from '../preview/api.mjs';
import {commitScene} from '../preview/scene-transaction.mjs';

test('API binds the intended workspace and accepts no-content success',async t=>{
  t.mock.method(globalThis,'fetch',async(path,options)=>{
    assert.equal(path,'/api/scene');assert.equal(options.headers['X-EE-Workspace'],'fixture-a');
    assert.deepEqual(JSON.parse(options.body),{scene:{}});return new Response(null,{status:204});
  });
  assert.equal(await request('/api/scene','PUT',{scene:{}},{workspace:'fixture-a'}),null);
});
test('API normalizes network and unreadable responses without exposing raw details',async t=>{
  for(const writing of [false,true]){
    for(const response of [new Error('private transport detail'),new Response('<html>error</html>',{status:500}),new Response('invalid',{status:200})]){
      t.mock.method(globalThis,'fetch',async()=>{if(response instanceof Error)throw response;return response;});
      await assert.rejects(request('/api/scene',writing?'PUT':'GET',writing?{}:undefined),error=>{
        assert.doesNotMatch(error.message,/private transport|Unexpected token/);assert.equal(error.uncertain,writing);return true;
      });t.mock.restoreAll();
    }
  }
});
test('mutating 503 is uncertain and structured errors preserve their code',async t=>{
  t.mock.method(globalThis,'fetch',async()=>Response.json({detail:{code:'unavailable',message:'Service unavailable'}},{status:503}));
  await assert.rejects(request('/api/scene','PUT',{}),error=>error.code==='unavailable'&&error.status===503&&error.uncertain);
});
const scene={brightness:100,zones:[],rules:[],overrides:{},sfp:{}};
test('atomic transaction sends one versioned config and preserves unchanged phases',async()=>{
  const calls=[];
  const after=await commitScene({revision:'before'},scene,async(...args)=>{calls.push(args);return {revision:'after',scene};});
  assert.equal(calls.length,1);assert.deepEqual(calls[0],['/api/scene','PUT',{scene,expected_revision:'before',preserve_unchanged:true,confirm_output:true}]);assert.equal(after.revision,'after');
});
test('accepted write with a lost reply is reconciled, never automatically resent',async()=>{
  for(const code of ['connection_unavailable','output_unconfirmed','scene_changed']){
    const calls=[],failure=Object.assign(new Error('Confirmation unavailable'),{code,uncertain:code==='connection_unavailable'});
    await assert.rejects(commitScene({revision:'before'},scene,async(...args)=>{
      calls.push(args);if(args[1]==='PUT')throw failure;return {revision:'after',scene};
    }),error=>error===failure&&error.acceptedScene?.revision==='after');
    assert.equal(calls.filter(call=>call[1]==='PUT').length,1);assert.equal(calls.length,2);
  }
});
test('reconciliation cannot mistake another configuration for the requested one',async()=>{
  const failure=Object.assign(new Error('Conflict'),{code:'scene_changed'});
  await assert.rejects(commitScene({revision:'before'},scene,async(_path,method)=>{
    if(method==='PUT')throw failure;return {revision:'other',scene:{...scene,brightness:20}};
  }),error=>error===failure&&!error.acceptedScene);
});
