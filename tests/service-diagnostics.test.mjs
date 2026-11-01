import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,readdirSync,statSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {createServiceDiagnostics} from '../preview/service-diagnostics.mjs';

function fixture(context,maxBytes){
  const directory=mkdtempSync(path.join(tmpdir(),'ee-service-diagnostics-'));
  context.after(()=>rmSync(directory,{recursive:true,force:true}));
  const log=createServiceDiagnostics(directory,{maxBytes});
  const entries=()=>readFileSync(path.join(directory,'service-diagnostics.log'),'utf8').trim().split('\n').map(line=>JSON.parse(line));
  return {directory,log,entries};
}

test('structured failures retain useful code locations without exception values or paths',context=>{
  const f=fixture(context),secret='private-token-that-must-not-be-logged';
  const diagnostic=JSON.stringify({event:'engine_failure',error_type:'InvalidState',message:secret,
    frames:[{module:'protocol.py',function:'send_close',line:383,source:secret},{module:'C:\\private\\account.py',function:'read secret',line:12}]})+'\n';
  f.log.stderr(diagnostic.slice(0,40));f.log.stderr(diagnostic.slice(40));
  const entries=f.entries();
  assert.equal(entries[0].error_type,'InvalidState');
  assert.deepEqual(entries[0].frames,[{module:'protocol.py',function:'send_close',line:383},{module:'unknown',function:'unknown',line:12}]);
  assert.equal(JSON.stringify(entries).includes(secret),false);
  assert.equal(JSON.stringify(entries).includes('private'),false);
  f.log.stderr(JSON.stringify({event:'engine_log',level:'ERROR',logger:'uvicorn.error',message:secret,args:[secret],frames:[]})+'\n');
  assert.equal(f.entries()[1].event,'engine_log');
  assert.equal(f.entries()[1].logger,'uvicorn.error');
  assert.equal(JSON.stringify(f.entries()).includes(secret),false);
});

test('raw and overlong pipe output cannot leak into diagnostics or grow the input buffer',context=>{
  const f=fixture(context);
  f.log.stderr('private raw exception\n');
  f.log.stderr('x'.repeat(100000));f.log.stderr('\n');
  f.log.stderr('{"event":"engine_failure","error_type":"ValueError"}\n');
  f.log.record('workspace_close_failed',{reason:'private raw exception'});
  f.log.record('engine_exit',{code:1,signal:null,secret:'private value'});
  assert.deepEqual(f.entries().map(item=>item.event),['engine_failure','workspace_close_failed','engine_exit']);
  assert.equal(f.entries()[1].reason,'unexpected_error');
  assert.equal(JSON.stringify(f.entries()).includes('private'),false);
});

test('diagnostics rotate to one bounded backup and unsupported events are ignored',context=>{
  const f=fixture(context,1024);
  assert.equal(f.log.record('secret event'),false);
  for(let i=0;i<100;i++)assert.equal(f.log.record('engine_exit',{code:i}),true);
  assert.deepEqual(readdirSync(f.directory).sort(),['service-diagnostics.log','service-diagnostics.log.1']);
  for(const name of readdirSync(f.directory))assert.ok(statSync(path.join(f.directory,name)).size<=1024);
  assert.equal(f.entries().at(-1).exit_code,99);
});

test('an unavailable log destination does not break lifecycle cleanup',()=>{
  const log=createServiceDiagnostics(path.join(tmpdir(),'missing-ee-diagnostics',String(Date.now())));
  assert.equal(log.record('workspace_closed'),false);
});
