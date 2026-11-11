import {appendFileSync, existsSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync} from 'node:fs';
import path from 'node:path';

const events=new Set(['engine_started','engine_exit','engine_failure','engine_log','workspace_close_failed','workspace_closed','parent_cleanup_exhausted','private_access_restored']);
const reasons=new Set(['release_unconfirmed','release_timeout','shutdown_unconfirmed','shutdown_timeout','private_failed']);
const identifier=value=>typeof value==='string'&&/^[A-Za-z_][\w.<>-]{0,79}$/.test(value)?value:undefined;

const stages=new Set(['engine','shutdown','parent_cleanup','desktop_exit']);
const outcomes=new Set(['closed','failed','cleanup_unconfirmed']);
const buildNumber=value=>typeof value==='string'&&/^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]{1,40})?$/.test(value)?value:null;

export function sessionSummary(value){
  if(!value||!stages.has(value.stage)||!outcomes.has(value.outcome)
    ||typeof value.time!=='string'||!/^\d{4}-\d\d-\d\dT[\d:.+-]+Z?$/.test(value.time)||!Number.isFinite(Date.parse(value.time)))return null;
  const result={time:value.time,stage:value.stage,outcome:value.outcome,build:buildNumber(value.build)};
  if(Number.isSafeInteger(value.attempts)&&value.attempts>0&&value.attempts<=10)result.attempts=value.attempts;
  return result;
}

export function readLastSession(directory){
  const file=path.join(directory,'last-session.json');
  try{
    if(statSync(file).size>8192)return {available:false,last_session:null};
    const value=sessionSummary(JSON.parse(readFileSync(file,'utf8')));
    return {available:Boolean(value),last_session:value};
  }catch(error){return {available:error.code==='ENOENT',last_session:null};}
}

export function createServiceDiagnostics(directory,{maxBytes=65536,build=null}={}){
  const file=path.join(directory,'service-diagnostics.log');
  let pending='',discarding=false,currentSummary=null;
  const statusFile=path.join(directory,'last-session.json');
  function saveSummary(entry){
    const stage=entry.event==='workspace_closed'||entry.event==='workspace_close_failed'?'shutdown'
      :entry.event==='parent_cleanup_exhausted'?'parent_cleanup':'engine';
    const value=sessionSummary({time:entry.time,stage,build,
      outcome:entry.event==='workspace_closed'?'closed':entry.event==='engine_failure'?'failed':'cleanup_unconfirmed',
      attempts:entry.attempts});
    currentSummary=value;
    const temporary=statusFile+'.'+process.pid+'.tmp';
    try{writeFileSync(temporary,JSON.stringify(value)+'\n',{encoding:'utf8',mode:0o600});renameSync(temporary,statusFile);}
    catch{try{unlinkSync(temporary);}catch{}}
  }
  function record(event,details={}){
    if(!events.has(event))return false;
    const entry={time:new Date().toISOString(),event,build:buildNumber(build)};
    if(event==='parent_cleanup_exhausted'&&Number.isSafeInteger(details.attempts))entry.attempts=Math.min(10,Math.max(1,details.attempts));
    if(event==='private_access_restored'&&Number.isSafeInteger(details.attempts))entry.attempts=Math.min(100000,Math.max(1,details.attempts));
    if(event==='engine_exit'){
      entry.exit_code=Number.isInteger(details.code)?details.code:null;
      if(typeof details.signal==='string'&&/^SIG[A-Z]{1,16}$/.test(details.signal))entry.signal=details.signal;
    }
    if(event==='workspace_close_failed')entry.reason=reasons.has(details.reason)?details.reason:'unexpected_error';
    if(event==='engine_failure'||event==='engine_log'){
      if(event==='engine_failure')entry.error_type=identifier(details.error_type)||'UnknownError';
      else{
        entry.level=['WARNING','ERROR','CRITICAL'].includes(details.level)?details.level:'ERROR';
        entry.logger=identifier(details.logger)||'other';
      }
      entry.frames=Array.isArray(details.frames)?details.frames.slice(-8).map(frame=>({
        module:identifier(frame?.module)||'unknown',function:identifier(frame?.function)||'unknown',
        line:Number.isSafeInteger(frame?.line)&&frame.line>0?frame.line:0,
      })):[];
    }
    if(['workspace_closed','workspace_close_failed','parent_cleanup_exhausted','engine_failure'].includes(event)
      ||event==='engine_exit'&&entry.exit_code!==0)saveSummary(entry);
    const line=JSON.stringify(entry)+'\n';
    try{
      if(existsSync(file)&&statSync(file).size+Buffer.byteLength(line)>maxBytes)renameSync(file,file+'.1');
      appendFileSync(file,line,{encoding:'utf8',mode:0o600});return true;
    }catch{return false;}
  }
  function stderr(chunk){
    for(const fragment of String(chunk).split(/(?<=\n)/)){
      if(!discarding){pending+=fragment;if(pending.length>4096){pending='';discarding=true;}}
      if(fragment.endsWith('\n')){
        if(!discarding){try{const entry=JSON.parse(pending);if(['engine_failure','engine_log'].includes(entry?.event))record(entry.event,entry);}catch{}}
        pending='';discarding=false;
      }
    }
  }
  return Object.freeze({record,stderr,setBuild:value=>{build=buildNumber(value);},summary:()=>currentSummary?{available:true,last_session:currentSummary}:readLastSession(directory)});
}
