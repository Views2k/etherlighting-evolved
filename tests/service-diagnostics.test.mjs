import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,readdirSync,statSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {createServiceDiagnostics,readLastSession,sessionSummary} from '../preview/service-diagnostics.mjs';

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

test('failure codes are kept only when they are identifiers',context=>{
  const f=fixture(context);
  for(const code of ['output_recovery_required','Bad-Code','x'.repeat(65),'private value',42])
    f.log.stderr(JSON.stringify({event:'engine_failure',error_type:'ValueError',code,frames:[]})+'\n');
  assert.deepEqual(f.entries().map(entry=>entry.code),['output_recovery_required',undefined,undefined,undefined,undefined]);
});

test('log templates are kept without control characters and capped at 120 characters',context=>{
  const f=fixture(context);
  for(const template of ['automatic login to %s failed: %s','line\nbreak\u0007\u009b','x'.repeat(200),['private value']])
    f.log.stderr(JSON.stringify({event:'engine_log',level:'WARNING',logger:'etherlighting_evolved.session',template,frames:[]})+'\n');
  assert.deepEqual(f.entries().map(entry=>entry.template),['automatic login to %s failed: %s','linebreak','x'.repeat(120),undefined]);
  f.log.record('engine_exit',{code:1,template:'private value'});
  assert.equal(f.entries().at(-1).template,undefined);
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
  assert.deepEqual(readdirSync(f.directory).sort(),['last-session.json','service-diagnostics.log','service-diagnostics.log.1']);
  for(const name of readdirSync(f.directory))assert.ok(statSync(path.join(f.directory,name)).size<=1024);
  assert.equal(f.entries().at(-1).exit_code,99);
});

test('an unconfirmed Tailscale route removal is a log line that leaves the last session alone',context=>{
  const f=fixture(context);
  assert.equal(f.log.record('private_route_cleanup_unconfirmed',{https_port:8444,url:'https://private.example.invalid'}),true);
  const [entry]=f.entries();
  assert.deepEqual(Object.keys(entry).sort(),['build','event','time']);assert.equal(entry.event,'private_route_cleanup_unconfirmed');
  assert.deepEqual(readdirSync(f.directory),['service-diagnostics.log']);
  assert.deepEqual(f.log.summary(),{available:true,last_session:null});
});

test('an unavailable log destination does not break lifecycle cleanup',()=>{
  const log=createServiceDiagnostics(path.join(tmpdir(),'missing-ee-diagnostics',String(Date.now())));
  assert.equal(log.record('workspace_closed'),false);
});


test('last session persists safe lifecycle status across restart and log rotation',context=>{
  const f=fixture(context,1024);f.log.setBuild('0.4.19');
  f.log.record('parent_cleanup_exhausted',{attempts:4,message:'private secret'});
  assert.equal(f.log.summary().last_session.stage,'parent_cleanup');
  for(let i=0;i<100;i++)f.log.record('engine_started');
  const restarted=createServiceDiagnostics(f.directory);
  assert.deepEqual(restarted.summary(),f.log.summary());
  assert.equal(restarted.summary().last_session.build,'0.4.19');
  assert.equal(restarted.summary().last_session.attempts,4);
  assert.equal(restarted.summary().last_session.outcome,'cleanup_unconfirmed');
  assert.equal(JSON.stringify(restarted.summary()).includes('private'),false);
  restarted.setBuild('0.4.20');restarted.record('workspace_closed');
  assert.equal(readLastSession(f.directory).last_session.outcome,'closed');
});

test('stored diagnostics are validated again and never expose arbitrary strings',context=>{
  const f=fixture(context);
  writeFileSync(path.join(f.directory,'last-session.json'),JSON.stringify({time:'2026-11-04T12:00:00Z',stage:'desktop_exit',outcome:'cleanup_unconfirmed',build:'private host',secret:'private token'}));
  const read=readLastSession(f.directory);assert.equal(read.available,true);assert.equal(read.last_session.build,null);
  assert.equal(JSON.stringify(read).includes('private'),false);
  writeFileSync(path.join(f.directory,'last-session.json'),'private raw error');
  assert.deepEqual(readLastSession(f.directory),{available:false,last_session:null});
  assert.equal(sessionSummary({time:'invalid',stage:'engine',outcome:'failed'}),null);
});

test('a current diagnostic remains visible when its durable destination is unavailable',()=>{
  const log=createServiceDiagnostics(path.join(tmpdir(),'missing-ee-diagnostics',String(Date.now())));
  log.record('parent_cleanup_exhausted',{attempts:4});
  assert.equal(log.summary().last_session.outcome,'cleanup_unconfirmed');
});
