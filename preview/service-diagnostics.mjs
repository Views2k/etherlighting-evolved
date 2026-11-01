import {appendFileSync, existsSync, renameSync, statSync} from 'node:fs';
import path from 'node:path';

const events=new Set(['engine_started','engine_exit','engine_failure','engine_log','workspace_close_failed','workspace_closed']);
const reasons=new Set(['release_unconfirmed','release_timeout','shutdown_unconfirmed','shutdown_timeout','private_failed']);
const identifier=value=>typeof value==='string'&&/^[A-Za-z_][\w.<>-]{0,79}$/.test(value)?value:undefined;

export function createServiceDiagnostics(directory,{maxBytes=65536}={}){
  const file=path.join(directory,'service-diagnostics.log');
  let pending='',discarding=false;
  function record(event,details={}){
    if(!events.has(event))return false;
    const entry={time:new Date().toISOString(),event};
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
  return Object.freeze({record,stderr});
}
