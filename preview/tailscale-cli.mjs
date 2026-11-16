import {execFile} from 'node:child_process';
import path from 'node:path';

export function tailscaleActionUrl(value) {
  if(typeof value!=='string'||value.length>2048||/[\s\x00-\x1f\x7f]/.test(value))return null;
  try {
    const url=new URL(value);
    if(url.protocol!=='https:'||url.username||url.password||url.port||url.hash)return null;
    if(url.hostname==='login.tailscale.com'&&(
      /^\/a\/[a-z\d_-]+$/i.test(url.pathname)&&!url.search||url.pathname==='/f/serve'||url.pathname==='/admin/machines'&&!url.search))return url.href;
    if(url.href==='https://tailscale.com/download')return url.href;
  }catch{}
  return null;
}

function outputAction(value) {
  for(const candidate of String(value).match(/https:\/\/[^\s<>"']+/g)||[]) {
    const url=tailscaleActionUrl(candidate);
    if(url&&new URL(url).hostname==='login.tailscale.com')return url;
  }
  return null;
}

export function createTailscaleRunner({platform=process.platform,env=process.env,execute=execFile,maxBuffer=65536}={}) {
  const executable=platform==='win32'?path.join(env.ProgramFiles||'C:/Program Files','Tailscale','tailscale.exe')
    :platform==='darwin'?'/Applications/Tailscale.app/Contents/MacOS/Tailscale':'/usr/bin/tailscale';
  // An aborted signal ends the CLI process, so a caller with a deadline is not held by a call that does not answer.
  return (args,{signal}={})=>new Promise((resolve,reject)=>{
    if(signal?.aborted){reject(Object.assign(new Error('Tailscale could not complete this action.'),{code:'tailscale_unavailable',actionUrl:null}));return;}
    execute(executable,args,{windowsHide:true,timeout:10000,maxBuffer,encoding:'utf8',...(signal?{signal}:{})},(error,stdout='',stderr='')=>{
      // Status exits nonzero while signed out, but still returns the state JSON.
      if(args[0]==='status') {
        try {const status=JSON.parse(stdout);if(status&&typeof status.BackendState==='string'){resolve(stdout);return;}}catch{}
      }
      if(!error){resolve(stdout);return;}
      const failure=new Error('Tailscale could not complete this action.');
      failure.code=error.code==='ENOENT'?'tailscale_not_installed'
        :error.code==='ERR_CHILD_PROCESS_STDIO_MAXBUFFER'?'tailscale_output_too_large'
        :error.code==='EACCES'||/access denied|permission denied/i.test(stderr)?'tailscale_permission_required':'tailscale_unavailable';
      failure.actionUrl=outputAction(`${stdout}\n${stderr}`);
      reject(failure);
    });
  });
}

export const runInstalledTailscale=createTailscaleRunner();
