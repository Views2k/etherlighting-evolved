import http from 'node:http';

const LOCAL_ONLY=new Set(['/api/private-access','/api/private-access/refresh','/api/private-access/login','/api/workspace/close','/api/desktop/quit','/api/desktop/show']);
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

export function createPrivateGateway({targetPort,listenPort=4184,status=()=>({})}) {
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
    const next=http.createServer((request,response)=>{
      if(!access.authorize(request).ok){json(response,403,{detail:{code:'private_access_denied',message:'Use your approved Tailscale account to open EE.'}});return;}
      const pathname=requestPath(request.url);
      if(pathname===null){json(response,403,{detail:{code:'private_access_denied',message:'This request cannot be opened through private access.'}});return;}
      if(pathname==='/api/private-access'&&request.method==='GET'){json(response,200,{...status(),can_configure:false,can_change_port:false,setup_action:null,action_url:null});return;}
      if(LOCAL_ONLY.has(pathname)){json(response,403,{detail:{code:'local_access_required',message:'Manage this setting on the computer running EE.'}});return;}
      const upstream=http.request({host:'127.0.0.1',port:targetPort,path:request.url,method:request.method,headers:headers(request)},remote=>{
        response.writeHead(remote.statusCode,remote.headers);remote.pipe(response);
      });
      upstream.setTimeout(30000,()=>upstream.destroy());
      upstream.on('error',()=>{if(!response.headersSent)json(response,503,{detail:'EE is reconnecting. Try again shortly.'});else response.destroy();});
      request.on('aborted',()=>upstream.destroy());
      request.pipe(upstream);
    });
    next.requestTimeout=30000;next.headersTimeout=10000;
    next.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
    next.on('upgrade',(request,socket,head)=>{
      if(request.url!=='/api/ws'||!access.authorize(request,{websocket:true}).ok){socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');return;}
      const upstream=http.request({host:'127.0.0.1',port:targetPort,path:'/api/ws',headers:headers(request,true)});
      upstream.setTimeout(10000,()=>upstream.destroy());
      upstream.on('upgrade',(response,remote,remoteHead)=>{
        upstream.setTimeout(0);sockets.add(remote);remote.on('close',()=>sockets.delete(remote));
        socket.write(`HTTP/1.1 101 Switching Protocols\r\n${Object.entries(response.headers).map(([key,value])=>`${key}: ${value}`).join('\r\n')}\r\n\r\n`);
        if(head.length)remote.write(head);if(remoteHead.length)socket.write(remoteHead);
        remote.pipe(socket);socket.pipe(remote);
        remote.on('error',()=>socket.destroy());socket.on('error',()=>remote.destroy());
        socket.on('close',()=>remote.destroy());remote.on('close',()=>socket.destroy());
      });
      upstream.on('error',()=>socket.destroy());upstream.on('response',()=>socket.destroy());socket.on('close',()=>upstream.destroy());upstream.end();
    });
    await new Promise((resolve,reject)=>{next.once('error',reject);next.listen(requestedPort,'127.0.0.1',resolve);});
    server=next;
    return next.address();
  }
  return {start,stop};
}
