import http from 'node:http';
import {readFile, mkdir} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {randomBytes} from 'node:crypto';
import path from 'node:path';
import {createAdminAccess,ADMIN_CODE_PARAM,ADMIN_HEADER,ADMIN_MESSAGES,LINK_TTL_MS} from './admin-access.mjs';
import {isEntryScript} from './entry-script.mjs';
import {createPrivateGateway} from './private-gateway.mjs';
import {createPrivateAccessManager} from './private-access.mjs';
import {createLanAccessManager,LAN_DEFAULT_PORT,MAX_SETTING_BYTES,readLimited} from './lan-access.mjs';
import {keepPrivateAccessAvailable} from './private-access-retry.mjs';
import {resolveRuntimeConfig} from './runtime-config.mjs';
import {createWorkspaceCloser} from './workspace-close.mjs';
import {finishAfter, watchDesktopParent, whenWorkspaceGone} from './parent-lifetime.mjs';
import {createServiceDiagnostics} from './service-diagnostics.mjs';

const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.webmanifest':'application/manifest+json','.png':'image/png','.ico':'image/x-icon','.svg':'image/svg+xml'};
// Engine requests are small JSON documents, and the engine reads each body fully into memory.
const MAX_BODY_BYTES=1048576;
const PRIVATE_ACCESS_PATHS=['/api/private-access','/api/private-access/refresh','/api/private-access/login'];
const LAN_DEVICE_PATH=/^\/api\/lan-access\/devices\/([0-9a-f]{16})$/;
const LAN_SETTING_INVALID='Choose a valid LAN access setting and a port between 1024 and 65535.';

// The gateway's own refusals use the engine's error shape, so clients read the same codes whichever one answers.
function refuse(res,status,code,message){
  res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(JSON.stringify({detail:{code,message}}));
}

// The methods each LAN access route accepts on this gateway. The pairing route exists only on the LAN listener.
function lanRoute(url){
  if(url==='/api/lan-access')return ['GET','PUT'];
  if(url==='/api/lan-access/code')return ['POST'];
  return LAN_DEVICE_PATH.test(url)?['DELETE']:null;
}

// The returned server is not listening yet. Every way the workspace closes (the close request, the desktop parent
// exiting, a signal or a listen failure) goes through the same state, so private access cannot be reopened while
// the engine is shutting down.
export function createWorkspaceServer({root,port,live,backendPort,token,child,diagnostics,privateAccess,lanAccess,admin,closeWorkspace,keepAvailable=keepPrivateAccessAvailable,exit=code=>process.exit(code)}) {
  const allowed = new Set([`127.0.0.1:${port}`,`localhost:${port}`]);
  // Browsers omit Origin on simple cross-site GETs such as an <img> source, but they still send Sec-Fetch-Site, and
  // some engine GET routes have side effects. API calls are accepted only from the workspace page or from clients
  // that send no fetch metadata (the desktop client and the private gateway). Links from other sites still open
  // the page itself.
  function permitted(req,api) {
    const site=req.headers['sec-fetch-site'];
    return allowed.has(req.headers.host) && (!req.headers.origin || [...allowed].some(host=>req.headers.origin===`http://${host}`))
      && (!api || site===undefined || site==='same-origin' || site==='none');
  }
  let closing,workspaceClosing=false;
  const privateRetry=keepAvailable({manager:privateAccess,active:()=>!workspaceClosing,
    restored:({attempts})=>diagnostics.record('private_access_restored',{attempts})});
  async function shutDown(){
    workspaceClosing=true;
    try{closing??=closeWorkspace();await closing;}
    catch(error){diagnostics.record('workspace_close_failed',{reason:error.message});closing=undefined;throw error;}
    privateRetry.stop();diagnostics.record('workspace_closed');
  }
  function readmit(){if(child.exitCode===null&&!child.stdin.writableEnded)workspaceClosing=false;}
  // In live mode, ending private access also removes EE's Tailscale Serve route, within the manager's own time limit.
  // The LAN listener closes too; both keep their saved settings for the next start.
  async function closePrivate(){
    privateRetry.stop();
    const [result]=await Promise.all([live?privateAccess.shutdown():privateAccess.close(),lanAccess.close()]);
    if(live&&result.route==='unconfirmed')diagnostics.record('private_route_cleanup_unconfirmed');
  }
  function stop(){workspaceClosing=true;void closePrivate().catch(()=>{});server.close();child.stdin.end('quit\n');}
  // The engine never receives the administration key or the browser's cookies.
  function engineHeaders(req){
    const headers={...req.headers,host:`127.0.0.1:${backendPort}`,authorization:`Bearer ${token}`};
    for(const name of ['origin','cookie',ADMIN_HEADER])delete headers[name];
    return headers;
  }
  // Without the engine every API request would fail with 503. Stopping the gateway as well lets the desktop see that
  // the service stopped and offer to start it again. An exit during a workspace close is that close finishing.
  function engineExited(){
    if(workspaceClosing)return;
    workspaceClosing=true;privateRetry.stop();server.close();exit(1);
  }
  const server=http.createServer(async(req,res)=>{
    if (!permitted(req,req.url.startsWith('/api/'))) {refuse(res,403,'forbidden','Requests from other websites are not allowed.');return;}
    // A browser opened by the desktop app or with the printed link trades its code for the cookies, and the redirect
    // takes the code out of the address bar. The Tailscale and LAN gateways always send an Origin header, so a request
    // from a remote device never redeems a code.
    if(req.method==='GET'&&!req.headers.origin&&req.url.startsWith('/?')){
      const codes=new URLSearchParams(req.url.slice(2)).getAll(ADMIN_CODE_PARAM);
      if(codes.length){
        const cookies=codes.length===1?admin.redeem(codes[0]):null;
        res.writeHead(303,{Location:'/','Cache-Control':'no-store','Referrer-Policy':'no-referrer',...(cookies?{'Set-Cookie':cookies}:{})});
        res.end();return;
      }
    }
    if(PRIVATE_ACCESS_PATHS.includes(req.url)){
      const respond=(status,body)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(body));};
      const access=admin.allows(req);
      if(req.method!=='GET'&&!access){respond(403,{detail:{code:'admin_required',message:ADMIN_MESSAGES.privateAccess}});return;}
      // `local` tells clients that this gateway, on the computer running EE, answered, with or without administration
      // access. The private gateway answers GET itself without it, so a page on a remote device does not offer actions
      // that work only here, such as accepting an SSH key.
      if(!live){respond(200,{available:false,enabled:false,ready:false,can_configure:false,can_close_workspace:access&&!workspaceClosing,admin_required:!access,local:true});return;}
      try{
        if(workspaceClosing&&req.method!=='GET'){respond(503,{detail:'EE is closing. Reopen EE to change private access.'});return;}
        if(req.url.endsWith('/refresh')&&req.method==='POST'){await privateAccess.initialize();privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true,admin_required:false});return;}
        if(req.url.endsWith('/login')&&req.method==='POST'){await privateAccess.login();privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true,admin_required:false});return;}
        if(req.url==='/api/private-access'&&req.method==='GET'){
          // Without administration access a tab gets the read-only status that devices on Tailscale get. It leaves
          // out a pending sign-in link, which must not reach other local users.
          respond(200,access?{...privateAccess.status(),can_configure:!workspaceClosing,can_close_workspace:!workspaceClosing,admin_required:false,local:true}
            :{...privateAccess.status(),setup_action:null,action_url:null,can_change_port:false,can_configure:false,can_close_workspace:false,admin_required:true,local:true});
          return;
        }
        if(req.url==='/api/private-access'&&req.method==='PUT'){
          let body='';for await(const chunk of req){body+=chunk;if(body.length>1024){respond(413,{detail:'The setting is too large.'});return;}}
          let value;try{value=JSON.parse(body);}catch{respond(400,{detail:'Choose a valid access setting.'});return;}
          if(!value||typeof value.enabled!=='boolean'||Object.keys(value).some(key=>!['enabled','https_port','audience'].includes(key))
            ||value.audience!==undefined&&!['owner','accounts'].includes(value.audience)
            ||value.https_port!==undefined&&(!Number.isSafeInteger(value.https_port)||value.https_port<1024||value.https_port>65535)){respond(400,{detail:'Choose a valid access setting and a port between 1024 and 65535.'});return;}
          if(workspaceClosing){respond(503,{detail:'EE is closing. Reopen EE to change private access.'});return;}
          await privateAccess.configure(value.enabled,{httpsPort:value.https_port,audience:value.audience});privateRetry.observe();respond(200,{...privateAccess.status(),can_configure:true,admin_required:false});return;
        }
        respond(405,{detail:'This action is not supported.'});
      }catch{privateRetry.observe();respond(409,{detail:{code:'private_access_failed',message:'Private access could not be updated. Refresh status and try again.'},status:privateAccess.status()});}
      return;
    }
    // Every URL under this prefix is answered here, so none reaches the engine, including the pairing route that only
    // the LAN listener answers.
    if(req.url.startsWith('/api/lan-access')){
      const methods=lanRoute(req.url);
      if(!methods){refuse(res,404,'not_found','This LAN access request does not exist.');return;}
      if(!methods.includes(req.method)){res.writeHead(405);res.end();return;}
      const access=admin.allows(req);
      if(req.method!=='GET'&&!access){refuse(res,403,'admin_required',ADMIN_MESSAGES.lanAccess);return;}
      const respond=(status,body)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(JSON.stringify(body));};
      if(!live){respond(200,{available:false,enabled:false,ready:false,can_configure:false,admin_required:!access});return;}
      const current=()=>lanAccess.status({admin:access,closing:workspaceClosing});
      if(req.method==='GET'){respond(200,current());return;}
      const closingRefusal=()=>refuse(res,503,'workspace_closing','EE is closing. Reopen EE to change LAN access.');
      if(workspaceClosing){closingRefusal();return;}
      try{
        if(req.method==='PUT'){
          const text=Number(req.headers['content-length'])>MAX_SETTING_BYTES?null:await readLimited(req,MAX_SETTING_BYTES);
          if(text===null){refuse(res,413,'too_large','The request is too large.');return;}
          let value;
          try{value=JSON.parse(text);}catch{}
          if(!value||typeof value!=='object'||Array.isArray(value)||typeof value.enabled!=='boolean'||Object.keys(value).some(key=>!['enabled','port'].includes(key))
            ||value.port!==undefined&&(!Number.isSafeInteger(value.port)||value.port<1024||value.port>65535)){refuse(res,400,'invalid',LAN_SETTING_INVALID);return;}
          if(workspaceClosing){closingRefusal();return;}
          await lanAccess.configure(value.enabled,{port:value.port});
        }else if(req.method==='POST')await lanAccess.issueCode();
        else await lanAccess.revoke(LAN_DEVICE_PATH.exec(req.url)[1]);
        respond(200,current());
      }catch(error){
        if(Number.isInteger(error?.status))refuse(res,error.status,error.code,error.message);
        else respond(409,{detail:{code:'lan_access_failed',message:'LAN access could not be updated. Refresh status and try again.'},status:current()});
      }
      return;
    }
    if(req.url==='/api/last-session'){
      if(req.method!=='GET'){res.writeHead(405);res.end();return;}
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
      res.end(JSON.stringify(diagnostics.summary()));return;
    }
    if(req.url==='/api/workspace/close'){
      if(req.method!=='POST'){res.writeHead(405);res.end();return;}
      if(!admin.allows(req)){refuse(res,403,'admin_required',ADMIN_MESSAGES.close);return;}
      try{await shutDown();}
      catch{readmit();res.writeHead(409,{'Content-Type':'application/json'});res.end(JSON.stringify({detail:'The service has not confirmed shutdown. Wait and try again.'}));return;}
      res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify({closed:true}));server.close();
      return;
    }
    // Only the desktop app that started the gateway holds the key. It opens the browser with the code it gets here.
    if(req.url==='/api/workspace/admin-link'){
      if(req.method!=='POST'){res.writeHead(405);res.end();return;}
      if(!admin.allowsHeader(req)){refuse(res,403,'admin_required',ADMIN_MESSAGES.link);return;}
      if(workspaceClosing){refuse(res,503,'workspace_closing',ADMIN_MESSAGES.closing);return;}
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify({code:admin.issueCode(LINK_TTL_MS)}));
      return;
    }
    if (req.url === '/api/workspace') {
      if(req.method !== 'GET'){res.writeHead(405);res.end();return;}
      res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});
      res.end(JSON.stringify({live,remote:false}));return;
    }
    if (req.url.startsWith('/api/')) {
      // The answer does not ask to close the connection, so on a kept-alive connection Node reads and discards the rest
      // of the body. Closing a socket that still has unread data makes Windows reset it before the client reads the 413.
      if(Number(req.headers['content-length'])>MAX_BODY_BYTES){refuse(res,413,'too_large','The request is too large.');return;}
      const upstream=http.request({host:'127.0.0.1',port:backendPort,path:req.url,method:req.method,headers:engineHeaders(req)},r=>{res.writeHead(r.statusCode,r.headers);r.pipe(res);});
      upstream.on('error',()=>{if(!res.headersSent)res.writeHead(503,{'Content-Type':'application/json'});res.end(JSON.stringify({detail:'The EE service is unavailable.'}));});
      let received=0;
      req.on('data',chunk=>{received+=chunk.length;if(received>MAX_BODY_BYTES){res.destroy();upstream.destroy();}});
      req.pipe(upstream);return;
    }
    try {
      if(!['GET','HEAD'].includes(req.method)){res.writeHead(405);res.end();return;}
      const url=new URL(req.url,'http://localhost');
      const relative=url.pathname==='/'?'preview/index.html':decodeURIComponent(url.pathname).slice(1);
      const file=path.resolve(root,relative);
      if(!['preview','assets','src'].some(folder=>file.startsWith(path.join(root,folder)+path.sep))||!types[path.extname(file)])throw new Error();
      const bytes=await readFile(file);
      res.writeHead(200,{'Content-Type':types[path.extname(file)],...(relative==='preview/sw.mjs'?{'Service-Worker-Allowed':'/'}:{}),'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Content-Security-Policy':"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'"});
      res.end(req.method==='HEAD'?undefined:bytes);
    } catch {res.writeHead(404);res.end('Not found');}
  });
  // Node removes its own socket error handler before emitting 'upgrade', so a client that resets the connection
  // before the engine answers would otherwise raise an unhandled error and stop the gateway.
  server.on('upgrade',(req,socket,head)=>{
    socket.on('error',()=>socket.destroy());
    if(!permitted(req,true)||req.url!=='/api/ws'){socket.destroy();return;}
    const upstream=http.request({host:'127.0.0.1',port:backendPort,path:'/api/ws',headers:engineHeaders(req)});
    upstream.setTimeout(10000,()=>upstream.destroy());
    upstream.on('upgrade',(response,remote,remoteHead)=>{
      if(socket.destroyed){remote.destroy();return;}
      upstream.setTimeout(0);
      socket.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([key,value])=>`${key}: ${value}`).join('\r\n')}\r\n\r\n`);
      if(head.length)remote.write(head); if(remoteHead.length)socket.write(remoteHead);
      remote.pipe(socket); socket.pipe(remote); remote.on('error',()=>socket.destroy());
      socket.on('close',()=>remote.destroy()); remote.on('close',()=>socket.destroy());
    });
    upstream.on('error',()=>socket.destroy()); upstream.on('response',()=>socket.destroy()); socket.once('close',()=>upstream.destroy()); upstream.end();
  });
  return {server,privateRetry,shutDown,readmit,stop,engineExited,closePrivate};
}

// The desktop app holds the key and requests its own links. A source run has no other way to give a browser access.
export function startupLines({port,desktopHost,admin}){
  const lines=[`Browser preview: http://127.0.0.1:${port}`];
  if(desktopHost!=='1')lines.push(`Administration link (works once): http://127.0.0.1:${port}/?${ADMIN_CODE_PARAM}=${admin.issueCode(Infinity)}`);
  return lines;
}

export async function startWorkspace() {
  const runtime=resolveRuntimeConfig();
  const {root,port,live,directory}=runtime;
  // The Tailscale CLI inherits this process's environment, so the key leaves it before any child process starts.
  const adminKey=runtime.adminKey??randomBytes(32).toString('hex');delete process.env.EE_ADMIN_KEY;
  const admin=createAdminAccess({key:adminKey,port});
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
  const privateGateway=createPrivateGateway({targetPort:port,status:()=>privateAccess.status()});
  const privateAccess=createPrivateAccessManager({configPath:path.join(directory,'private-access.json'),startListener:(gate,listenPort)=>privateGateway.start(gate,listenPort),stopListener:()=>privateGateway.stop()});
  // A browser keeps up to six connections to a host plus one WebSocket per tab, so 16 per address leaves room for
  // several tabs while one device cannot hold every connection.
  const lanGateway=createPrivateGateway({targetPort:port,listenHost:'0.0.0.0',listenPort:LAN_DEFAULT_PORT,status:()=>privateAccess.status(),
    serverOptions:{maxHeaderSize:8192},maxConnections:64,maxConnectionsPerAddress:16});
  const lanAccess=createLanAccessManager({configPath:path.join(directory,'lan-access.json'),startListener:(gate,listenPort)=>lanGateway.start(gate,listenPort),
    stopListener:()=>lanGateway.stop(),recheck:()=>lanGateway.recheck()});
  const closeWorkspace = createWorkspaceCloser({child, closePrivate:()=>workspace.closePrivate(), release:()=>
    new Promise((resolve,reject)=>{
      const upstream=http.request({host:'127.0.0.1',port:backendPort,path:'/api/session/logout',method:'POST',headers:{authorization:`Bearer ${token}`}},response=>{
        response.resume();response.on('end',()=>response.statusCode===200?resolve():reject(new Error('release_unconfirmed')));
      });
      upstream.setTimeout(20000,()=>upstream.destroy(new Error('release_timeout')));upstream.on('error',reject);upstream.end();
    })
  });
  const workspace=createWorkspaceServer({root,port,live,backendPort,token,child,diagnostics,privateAccess,lanAccess,admin,closeWorkspace});
  const {server}=workspace;
  // 'close' follows the end of the engine's output, so its last diagnostics are recorded before the gateway exits.
  child.once('close',workspace.engineExited);
  server.on('error',()=>{workspace.stop();process.exitCode=1;});
  if(process.env.EE_DESKTOP_HOST==='1') watchDesktopParent({input:process.stdin,close:async()=>{
    await workspace.shutDown();server.close();process.stdin.destroy();
  },exhausted:({attempts})=>{
    diagnostics.record('parent_cleanup_exhausted',{attempts});
    workspace.readmit();
    whenWorkspaceGone(process.stdin,child,()=>finishAfter(workspace.closePrivate,10000,()=>process.exit(1)));
  }});
  for(const signal of ['SIGINT','SIGTERM'])process.on(signal,workspace.stop);
  server.listen(port,'127.0.0.1',()=>{
    for(const line of startupLines({port,desktopHost:process.env.EE_DESKTOP_HOST,admin}))console.log(line);
    if(live){
      void privateAccess.initialize().catch(()=>{}).then(()=>workspace.privateRetry.observe());
      // A failure is reported in the LAN access status, where Settings offers Retry LAN access.
      void lanAccess.initialize().catch(()=>{});
    }
  });
}

if(isEntryScript(import.meta.url))await startWorkspace();
