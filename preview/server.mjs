import http from 'node:http';
import {readFile, mkdir} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {randomBytes} from 'node:crypto';
import path from 'node:path';
import {createPrivateGateway} from './private-gateway.mjs';
import {createPrivateAccessManager} from './private-access.mjs';
import {keepPrivateAccessAvailable} from './private-access-retry.mjs';
import {resolveRuntimeConfig} from './runtime-config.mjs';
import {createWorkspaceCloser} from './workspace-close.mjs';
import {watchDesktopParent, whenWorkspaceGone} from './parent-lifetime.mjs';
import {createServiceDiagnostics} from './service-diagnostics.mjs';

const runtime=resolveRuntimeConfig();
const {root,port,live,directory}=runtime;
await mkdir(directory, {recursive: true});
const diagnostics=createServiceDiagnostics(directory);
const token = randomBytes(32).toString('hex');
const child = spawn(runtime.command,runtime.args,{cwd:runtime.cwd,env:runtime.env,windowsHide:true,stdio:['pipe','pipe','pipe']});
child.stderr.on('data',diagnostics.stderr);
child.on('exit',(code,signal)=>diagnostics.record('engine_exit',{code,signal}));
child.stdin.write(JSON.stringify({token,...runtime.serviceOptions})+'\n');
const lines = createInterface({input:child.stdout});
const ready = await new Promise((resolve,reject)=>{
  const timer=setTimeout(()=>reject(new Error('Preview service startup timed out.')),30000);
  child.once('error',()=>{clearTimeout(timer);reject(new Error('Preview service could not start.'));});
  child.once('exit',()=>{clearTimeout(timer);reject(new Error('Preview service stopped.'));});
  lines.once('line',line=>{clearTimeout(timer);try{resolve(JSON.parse(line));}catch{reject(new Error('Invalid startup response.'));}});
});
if (!ready.ready) throw new Error(ready.code === 'instance_running' ? 'Close the other EE app before starting the live web workspace.' : 'Preview service could not start.');
const backendPort = ready.port;
diagnostics.setBuild(ready.version);
diagnostics.record('engine_started');
const allowed = new Set([`127.0.0.1:${port}`,`localhost:${port}`]);
const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.webmanifest':'application/manifest+json','.png':'image/png','.ico':'image/x-icon','.svg':'image/svg+xml'};
function permitted(req) {return allowed.has(req.headers.host) && (!req.headers.origin || [...allowed].some(host=>req.headers.origin===`http://${host}`));}
const privateGateway=createPrivateGateway({targetPort:port,status:()=>privateAccess.status()});
const privateAccess=createPrivateAccessManager({configPath:path.join(directory,'private-access.json'),startListener:(gate,listenPort)=>privateGateway.start(gate,listenPort),stopListener:()=>privateGateway.stop()});
let closing,workspaceClosing=false;
const privateRetry=keepPrivateAccessAvailable({manager:privateAccess,active:()=>!workspaceClosing,
  restored:({attempts})=>diagnostics.record('private_access_restored',{attempts})});
const closeWorkspace = createWorkspaceCloser({child, closePrivate:()=>privateAccess.close(), release:()=>
  new Promise((resolve,reject)=>{
    const upstream=http.request({host:'127.0.0.1',port:backendPort,path:'/api/session/logout',method:'POST',headers:{authorization:`Bearer ${token}`}},response=>{
      response.resume();response.on('end',()=>response.statusCode===200?resolve():reject(new Error('release_unconfirmed')));
    });
    upstream.setTimeout(20000,()=>upstream.destroy(new Error('release_timeout')));upstream.on('error',reject);upstream.end();
  })
});
const server=http.createServer(async(req,res)=>{
  if (!permitted(req)) {res.writeHead(403);res.end();return;}
  if(['/api/private-access','/api/private-access/refresh','/api/private-access/login'].includes(req.url)){
    const respond=(status,body)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(body));};
    if(!live){respond(200,{available:false,enabled:false,ready:false,can_configure:false,can_close_workspace:!workspaceClosing});return;}
    try{
      if(workspaceClosing&&req.method!=='GET'){respond(503,{detail:'EE is closing. Reopen EE to change private access.'});return;}
      if(req.url.endsWith('/refresh')&&req.method==='POST'){await privateAccess.initialize();privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true});return;}
      if(req.url.endsWith('/login')&&req.method==='POST'){await privateAccess.login();privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true});return;}
      if(req.url==='/api/private-access'&&req.method==='GET'){respond(200,{...privateAccess.status(),can_configure:!workspaceClosing,can_close_workspace:!workspaceClosing});return;}
      if(req.url==='/api/private-access'&&req.method==='PUT'){
        let body='';for await(const chunk of req){body+=chunk;if(body.length>1024){respond(413,{detail:'The setting is too large.'});return;}}
        let value;try{value=JSON.parse(body);}catch{respond(400,{detail:'Choose a valid access setting.'});return;}
        if(!value||typeof value.enabled!=='boolean'||Object.keys(value).some(key=>!['enabled','https_port','audience'].includes(key))
          ||value.audience!==undefined&&!['owner','accounts'].includes(value.audience)
          ||value.https_port!==undefined&&(!Number.isSafeInteger(value.https_port)||value.https_port<1024||value.https_port>65535)){respond(400,{detail:'Choose a valid access setting and a port between 1024 and 65535.'});return;}
        if(workspaceClosing){respond(503,{detail:'EE is closing. Reopen EE to change private access.'});return;}
        await privateAccess.configure(value.enabled,{httpsPort:value.https_port,audience:value.audience});privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true});return;
      }
      respond(405,{detail:'This action is not supported.'});
    }catch{privateRetry.observe();respond(409,{detail:{code:'private_access_failed',message:'Private access could not be updated. Refresh status and try again.'},status:privateAccess.status()});}
    return;
  }
  if(req.url==='/api/last-session'){
    if(req.method!=='GET'){res.writeHead(405);res.end();return;}
    res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
    res.end(JSON.stringify(diagnostics.summary()));return;
  }
  if(req.url==='/api/workspace/close'){
    if(req.method!=='POST'){res.writeHead(405);res.end();return;}
    workspaceClosing=true;
    try{closing??=closeWorkspace();await closing;privateRetry.stop();diagnostics.record('workspace_closed');res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify({closed:true}));server.close();}
    catch(error){diagnostics.record('workspace_close_failed',{reason:error.message});closing=undefined;if(child.exitCode===null&&!child.stdin.writableEnded)workspaceClosing=false;res.writeHead(409,{'Content-Type':'application/json'});res.end(JSON.stringify({detail:'The service has not confirmed shutdown. Wait and try again.'}));}
    return;
  }
  if (req.url === '/api/workspace') {
    if(req.method !== 'GET'){res.writeHead(405);res.end();return;}
    res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});
    res.end(JSON.stringify({live}));return;
  }
  if (req.url.startsWith('/api/')) {
    const headers={...req.headers,host:`127.0.0.1:${backendPort}`,authorization:`Bearer ${token}`}; delete headers.origin;
    const upstream=http.request({host:'127.0.0.1',port:backendPort,path:req.url,method:req.method,headers},r=>{res.writeHead(r.statusCode,r.headers);r.pipe(res);});
    upstream.on('error',()=>{if(!res.headersSent)res.writeHead(503,{'Content-Type':'application/json'});res.end(JSON.stringify({detail:'The preview service is unavailable.'}));});
    req.pipe(upstream);return;
  }
  try {
    if(!['GET','HEAD'].includes(req.method)){res.writeHead(405);res.end();return;}
    const url=new URL(req.url,'http://localhost');
    const relative=url.pathname==='/'?'preview/index.html':decodeURIComponent(url.pathname).slice(1);
    const file=path.resolve(root,relative);
    if(!['preview','assets','src'].some(folder=>file.startsWith(path.join(root,folder)+path.sep))||!types[path.extname(file)])throw new Error();
    const bytes=await readFile(file);
    res.writeHead(200,{'Content-Type':types[path.extname(file)],...(relative==='preview/sw.mjs'?{'Service-Worker-Allowed':'/'}:{}),'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'"});
    res.end(req.method==='HEAD'?undefined:bytes);
  } catch {res.writeHead(404);res.end('Not found');}
});
server.on('upgrade',(req,socket,head)=>{
  if(!permitted(req)||req.url!=='/api/ws'){socket.destroy();return;}
  const headers={...req.headers,host:`127.0.0.1:${backendPort}`,authorization:`Bearer ${token}`}; delete headers.origin;
  const upstream=http.request({host:'127.0.0.1',port:backendPort,path:'/api/ws',headers});
  upstream.on('upgrade',(response,remote,remoteHead)=>{
    socket.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([key,value])=>`${key}: ${value}`).join('\r\n')}\r\n\r\n`);
    if(head.length)remote.write(head); if(remoteHead.length)socket.write(remoteHead);
    remote.pipe(socket); socket.pipe(remote); remote.on('error',()=>socket.destroy()); socket.on('error',()=>remote.destroy());
  });
  upstream.on('error',()=>socket.destroy()); upstream.on('response',()=>socket.destroy()); upstream.end();
});
server.on('error',()=>{workspaceClosing=true;privateRetry.stop();void privateAccess.close();child.stdin.end('quit\n');process.exitCode=1;});
if(process.env.EE_DESKTOP_HOST==='1') watchDesktopParent({input:process.stdin,close:async()=>{
  workspaceClosing=true;closing??=closeWorkspace();
  try{await closing;privateRetry.stop();diagnostics.record('workspace_closed');server.close();process.stdin.destroy();}
  catch(error){diagnostics.record('workspace_close_failed',{reason:error.message});closing=undefined;throw error;}
},exhausted:({attempts})=>{
  diagnostics.record('parent_cleanup_exhausted',{attempts});
  if(child.exitCode===null&&!child.stdin.writableEnded)workspaceClosing=false;
  whenWorkspaceGone(process.stdin,child,()=>process.exit(1));
}});
for(const signal of ['SIGINT','SIGTERM'])process.on(signal,()=>{workspaceClosing=true;privateRetry.stop();void privateAccess.close();server.close();child.stdin.end('quit\n');});
server.listen(port,'127.0.0.1',()=>{console.log(`Browser preview: http://127.0.0.1:${port}`);if(live)void privateAccess.initialize().catch(()=>{}).then(()=>privateRetry.observe());});
