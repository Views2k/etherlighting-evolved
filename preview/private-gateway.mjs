import http from 'node:http';
import {ROUTE_ACCESS} from './remote-routes.mjs';

const LOCAL_ACCESS={detail:{code:'local_access_required',message:'This action is available only on the computer running EE.'}};
const PRIVATE_DENIED={detail:{code:'private_access_denied',message:'Private access requires an approved Tailscale account or device.'}};
const PRIVATE_PATH_DENIED={detail:{code:'private_access_denied',message:'This request cannot be opened through private access.'}};
const TOO_LARGE={detail:{code:'too_large',message:'The request is too large.'}};
// Remote access runs only in live mode: server.mjs starts both gateways only when live is true.
const REMOTE_WORKSPACE={live:true,remote:true};
const FORWARD_HEADERS=new Set(['accept','accept-language','content-type','content-length','x-ee-workspace','if-none-match','if-modified-since','range','sec-websocket-key','sec-websocket-version','sec-websocket-protocol','sec-websocket-extensions']);
// The rule server.mjs applies to engine requests, so a request that passes a remote gateway fits the engine too.
const MAX_BODY_BYTES=1048576;

function requestPath(value) {
  if(typeof value!=='string')return null;
  const raw=value.split('?',1)[0];
  if(!raw.startsWith('/')||/%(?:2f|5c)/i.test(raw))return null;
  let decoded;
  try{decoded=decodeURIComponent(raw);}catch{return null;}
  // Compare the same decoded route the backend sees; reject paths that could
  // change meaning after another decoding or separator-normalization pass.
  if(/[\\%?#\x00-\x1f\x7f]/.test(decoded)||decoded.includes('//')
      ||decoded.split('/').some(part=>part==='.'||part==='..'))return null;
  return decoded.length>1&&decoded.endsWith('/')?decoded.slice(0,-1):decoded;
}

const segment=part=>/^\{[a-z_]+\}$/.test(part)?'[^/]+':part.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
const isApi=pathname=>pathname==='/api'||pathname.startsWith('/api/');

export function createRouteMatcher(access){
  const routes=access.routes.filter(route=>route.method!=='WS').map(route=>({method:route.method,remote:route.remote===true,
    pattern:new RegExp(`^${route.path.split('/').map(segment).join('/')}$`)}));
  const sockets=new Set(access.routes.filter(route=>route.method==='WS'&&route.remote===true).map(route=>route.path));
  const staticMethods=new Set(access.static.methods);
  return {
    // pathname comes from requestPath(): decoded, without query or trailing slash. A request is remote only when at
    // least one entry matches and every matching entry is remote, so a placeholder never hides a local entry.
    allowed(method,pathname){
      if(!isApi(pathname))return staticMethods.has(method);
      const hits=routes.filter(route=>route.method===method&&route.pattern.test(pathname));
      return hits.length>0&&hits.every(route=>route.remote);
    },
    // The raw request URL must equal a remote WS path, so a query, trailing slash or encoding is refused.
    socket(url){return sockets.has(url);},
  };
}
export const remoteRoutes=createRouteMatcher(ROUTE_ACCESS);

/**
 * A listener for remote devices that forwards allowed requests to the local gateway. The Tailscale listener uses the
 * defaults. The LAN listener binds every IPv4 interface and passes a gate with these optional hooks:
 * - acceptConnection(socket): false closes a new connection before any HTTP is read.
 * - answer(request,response,pathname,result): answers a request itself and resolves true, before any route check.
 * - workspace(result): the GET /api/workspace answer.
 * - pairingRequired: the refusal for an /api request or change from a device that authorize() reports as not paired
 *   ({ok:false,pairing:true}); without it such a device is refused like any other.
 * - pathDenied: the refusal for a path that cannot be classified.
 * authorize() may also refuse with its own {code,message}.
 */
export function createPrivateGateway({targetPort,listenPort=4184,listenHost='127.0.0.1',status=()=>({}),deviceRecheckMs=15000,
  serverOptions={},maxConnections=Infinity,maxConnectionsPerAddress=Infinity}) {
  let server=null,closing=null;
  const sockets=new Set(),watched=new Set();
  function headers(request,upgrade=false) {
    const selected=Object.fromEntries(Object.entries(request.headers).filter(([key])=>FORWARD_HEADERS.has(key)));
    selected.host=`127.0.0.1:${targetPort}`;
    selected.origin=`http://127.0.0.1:${targetPort}`;
    if(upgrade){selected.connection='Upgrade';selected.upgrade='websocket';}
    return selected;
  }
  function json(response,code,body){response.writeHead(code,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});response.end(JSON.stringify(body));}
  async function stop() {
    if(closing)return closing;
    if(!server)return;
    const previous=server;server=null;
    closing=new Promise(resolve=>previous.close(resolve));
    for(const socket of sockets)socket.destroy();
    sockets.clear();
    try{await closing;}finally{closing=null;}
  }
  // Checks every open device WebSocket now, so a removed device is disconnected before its removal is confirmed.
  async function recheck(){await Promise.all([...watched].map(check=>check()));}
  async function start(access,requestedPort=listenPort) {
    await stop();
    async function authorize(request,websocket=false){
      try{return await access.authorize(request,{websocket});}
      catch{return null;}
    }
    // Revocation also closes a device's open or still opening WebSocket. Only one check runs per socket at a time.
    function watchDevice(request,socket){
      let timer,running=null;
      const check=()=>running??=(async()=>{
        clearTimeout(timer);
        const result=await authorize(request,true);
        running=null;
        if(server!==next||socket.destroyed)return;
        if(result?.ok!==true||result.device!==true){socket.destroy();return;}
        timer=setTimeout(check,deviceRecheckMs).unref();
      })();
      timer=setTimeout(check,deviceRecheckMs).unref();
      watched.add(check);
      socket.once('close',()=>{clearTimeout(timer);watched.delete(check);});
    }
    function forward(request,response){
      if(Number(request.headers['content-length'])>MAX_BODY_BYTES){json(response,413,TOO_LARGE);return;}
      const upstream=http.request({host:'127.0.0.1',port:targetPort,path:request.url,method:request.method,headers:headers(request)},remote=>{
        // Administration cookies belong to the computer running EE and never reach a remote device.
        const forwarded={...remote.headers};delete forwarded['set-cookie'];
        response.writeHead(remote.statusCode,forwarded);remote.pipe(response);
      });
      upstream.setTimeout(30000,()=>upstream.destroy());
      upstream.on('error',()=>{if(!response.headersSent)json(response,503,{detail:'EE is reconnecting. Try again shortly.'});else response.destroy();});
      response.once('close',()=>upstream.destroy());
      let received=0;
      request.on('data',chunk=>{received+=chunk.length;if(received>MAX_BODY_BYTES){response.destroy();upstream.destroy();}});
      request.pipe(upstream);
    }
    const next=http.createServer({requestTimeout:30000,headersTimeout:10000,...serverOptions},async(request,response)=>{
      const result=await authorize(request);
      if(server!==next||response.destroyed||request.socket.destroyed)return;
      const paired=result?.ok===true;
      if(!paired&&!(result?.pairing===true&&access.pairingRequired)){
        json(response,403,result?.code&&result.message?{detail:{code:result.code,message:result.message}}:PRIVATE_DENIED);return;
      }
      const pathname=requestPath(request.url);
      if(pathname===null){json(response,403,access.pathDenied??PRIVATE_PATH_DENIED);return;}
      if(access.answer){
        // A body that ends early or stalls past the request timeout rejects here; it ends this request only.
        try{if(await access.answer(request,response,pathname,result))return;}
        catch{response.destroy();return;}
      }
      if(pathname==='/api/workspace'&&request.method==='GET'){json(response,200,access.workspace?.(result)??REMOTE_WORKSPACE);return;}
      if(!paired){
        if(isApi(pathname)||!remoteRoutes.allowed(request.method,pathname))json(response,403,access.pairingRequired);
        else forward(request,response);
        return;
      }
      if(pathname==='/api/private-access'&&request.method==='GET'){json(response,200,{...status(),can_configure:false,can_close_workspace:false,can_change_port:false,setup_action:null,action_url:null});return;}
      if(!remoteRoutes.allowed(request.method,pathname)){json(response,403,LOCAL_ACCESS);return;}
      forward(request,response);
    });
    next.maxConnections=maxConnections;
    const perAddress=new Map();
    next.on('connection',socket=>{
      if(access.acceptConnection&&!access.acceptConnection(socket)){socket.destroy();return;}
      // Read once: a destroyed socket no longer reports its address when it closes.
      const address=socket.remoteAddress,count=perAddress.get(address)??0;
      if(count>=maxConnectionsPerAddress){socket.destroy();return;}
      perAddress.set(address,count+1);sockets.add(socket);
      socket.on('close',()=>{
        sockets.delete(socket);
        const left=perAddress.get(address)-1;
        if(left)perAddress.set(address,left);else perAddress.delete(address);
      });
    });
    next.on('upgrade',async(request,socket,head)=>{
      socket.on('error',()=>socket.destroy());
      socket.once('end',()=>socket.destroy());
      const result=remoteRoutes.socket(request.url)&&await authorize(request,true);
      if(server!==next||socket.destroyed)return;
      if(result?.ok!==true){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
      // Watched before EE answers the upgrade, so a device removed while the upgrade waits never gets the socket.
      if(result.device===true)watchDevice(request,socket);
      const upstream=http.request({host:'127.0.0.1',port:targetPort,path:request.url,headers:headers(request,true)});
      upstream.setTimeout(10000,()=>upstream.destroy());
      upstream.on('upgrade',(response,remote,remoteHead)=>{
        if(server!==next||socket.destroyed){remote.destroy();return;}
        upstream.setTimeout(0);sockets.add(remote);remote.on('close',()=>sockets.delete(remote));
        socket.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([key,value])=>`${key}: ${value}`).join('\r\n')}\r\n\r\n`);
        if(head.length)remote.write(head);if(remoteHead.length)socket.write(remoteHead);
        remote.pipe(socket);socket.pipe(remote);
        remote.on('error',()=>socket.destroy());
        socket.on('close',()=>remote.destroy());remote.on('close',()=>socket.destroy());
      });
      upstream.on('error',()=>socket.destroy());upstream.on('response',()=>socket.destroy());socket.on('close',()=>upstream.destroy());upstream.end();
    });
    await new Promise((resolve,reject)=>{next.once('error',reject);next.listen(requestedPort,listenHost,resolve);});
    server=next;
    return next.address();
  }
  return {start,stop,recheck};
}
