import http from 'node:http';
import {ROUTE_ACCESS} from './remote-routes.mjs';

const LOCAL_ACCESS={detail:{code:'local_access_required',message:'This action is available only on the computer running EE.'}};
// Private access runs only in live mode: server.mjs starts it only when live is true.
const REMOTE_WORKSPACE={live:true,remote:true};
const FORWARD_HEADERS=new Set(['accept','accept-language','content-type','content-length','x-ee-workspace','if-none-match','if-modified-since','range','sec-websocket-key','sec-websocket-version','sec-websocket-protocol','sec-websocket-extensions']);

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

export function createRouteMatcher(access){
  const routes=access.routes.filter(route=>route.method!=='WS').map(route=>({method:route.method,remote:route.remote===true,
    pattern:new RegExp(`^${route.path.split('/').map(segment).join('/')}$`)}));
  const sockets=new Set(access.routes.filter(route=>route.method==='WS'&&route.remote===true).map(route=>route.path));
  const staticMethods=new Set(access.static.methods);
  return {
    // pathname comes from requestPath(): decoded, without query or trailing slash. A request is remote only when at
    // least one entry matches and every matching entry is remote, so a placeholder never hides a local entry.
    allowed(method,pathname){
      if(pathname!=='/api'&&!pathname.startsWith('/api/'))return staticMethods.has(method);
      const hits=routes.filter(route=>route.method===method&&route.pattern.test(pathname));
      return hits.length>0&&hits.every(route=>route.remote);
    },
    // The raw request URL must equal a remote WS path, so a query, trailing slash or encoding is refused.
    socket(url){return sockets.has(url);},
  };
}
export const remoteRoutes=createRouteMatcher(ROUTE_ACCESS);

export function createPrivateGateway({targetPort,listenPort=4184,status=()=>({}),deviceRecheckMs=15000}) {
  let server=null,closing=null;
  const sockets=new Set();
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
  async function start(access,requestedPort=listenPort) {
    await stop();
    async function authorize(request,websocket=false){
      try{return await access.authorize(request,{websocket});}
      catch{return null;}
    }
    // Revocation also closes an already subscribed device. Only one recheck runs per socket at a time.
    function watchDevice(request,socket){
      let timer;
      async function check(){
        const result=await authorize(request,true);
        if(server!==next||socket.destroyed)return;
        if(result?.ok!==true||result.device!==true){socket.destroy();return;}
        timer=setTimeout(check,deviceRecheckMs).unref();
      }
      timer=setTimeout(check,deviceRecheckMs).unref();
      socket.once('close',()=>clearTimeout(timer));
    }
    const next=http.createServer(async(request,response)=>{
      const result=await authorize(request);
      if(server!==next||response.destroyed||request.socket.destroyed)return;
      if(result?.ok!==true){json(response,403,{detail:{code:'private_access_denied',message:'Private access requires an approved Tailscale account or device.'}});return;}
      const pathname=requestPath(request.url);
      if(pathname===null){json(response,403,{detail:{code:'private_access_denied',message:'This request cannot be opened through private access.'}});return;}
      if(pathname==='/api/private-access'&&request.method==='GET'){json(response,200,{...status(),can_configure:false,can_close_workspace:false,can_change_port:false,setup_action:null,action_url:null});return;}
      if(pathname==='/api/workspace'&&request.method==='GET'){json(response,200,REMOTE_WORKSPACE);return;}
      if(!remoteRoutes.allowed(request.method,pathname)){json(response,403,LOCAL_ACCESS);return;}
      const upstream=http.request({host:'127.0.0.1',port:targetPort,path:request.url,method:request.method,headers:headers(request)},remote=>{
        response.writeHead(remote.statusCode,remote.headers);remote.pipe(response);
      });
      upstream.setTimeout(30000,()=>upstream.destroy());
      upstream.on('error',()=>{if(!response.headersSent)json(response,503,{detail:'EE is reconnecting. Try again shortly.'});else response.destroy();});
      response.once('close',()=>upstream.destroy());
      request.pipe(upstream);
    });
    next.requestTimeout=30000;next.headersTimeout=10000;
    next.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
    next.on('upgrade',async(request,socket,head)=>{
      socket.on('error',()=>socket.destroy());
      socket.once('end',()=>socket.destroy());
      const result=remoteRoutes.socket(request.url)&&await authorize(request,true);
      if(server!==next||socket.destroyed)return;
      if(result?.ok!==true){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
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
        if(result.device===true)watchDevice(request,socket);
      });
      upstream.on('error',()=>socket.destroy());upstream.on('response',()=>socket.destroy());socket.on('close',()=>upstream.destroy());upstream.end();
    });
    await new Promise((resolve,reject)=>{next.once('error',reject);next.listen(requestedPort,'127.0.0.1',resolve);});
    server=next;
    return next.address();
  }
  return {start,stop};
}
