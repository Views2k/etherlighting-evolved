import {isIP} from 'node:net';

const SAFE_METHODS=new Set(['GET','HEAD','OPTIONS']);
const LOOPBACK=new Set(['127.0.0.1','::1','::ffff:127.0.0.1']);
const IDENTITY_HEADERS=new Set(['host','origin','x-forwarded-host','x-forwarded-proto','x-forwarded-for',
  'tailscale-user-login','tailscale-funnel-request','authorization','forwarded']);
const DENIED=Object.freeze({ok:false,status:403,code:'private_access_denied'});
const ALLOWED=Object.freeze({ok:true});

function singleHeaders(request){
  if(!Array.isArray(request.rawHeaders)||request.rawHeaders.length%2)return null;
  const values=new Map();
  for(let i=0;i<request.rawHeaders.length;i+=2){
    const [rawName,value]=request.rawHeaders.slice(i,i+2);
    if(typeof rawName!=='string'||typeof value!=='string')return null;
    const name=rawName.toLowerCase();
    if(!IDENTITY_HEADERS.has(name))continue;
    if(values.has(name)||value.length>1024||/[\x00-\x1f\x7f]/.test(value))return null;
    values.set(name,value);
  }
  return values;
}

function decodeLogin(value){
  if(typeof value!=='string'||!value||value.length>512)return null;
  if(/^[\x21-\x7e]+$/.test(value)&&!value.startsWith('=?'))return value;
  const encoded=/^=\?utf-8\?q\?([\x21-\x7e]+)\?=$/i.exec(value);
  if(!encoded)return null;
  const bytes=[];
  for(let i=0;i<encoded[1].length;i++){
    const character=encoded[1][i];
    if(character==='='){
      const hex=encoded[1].slice(i+1,i+3);
      if(!/^[a-f\d]{2}$/i.test(hex))return null;
      bytes.push(parseInt(hex,16));i+=2;
    }else bytes.push(character==='_'?32:character.charCodeAt(0));
  }
  try{return new TextDecoder('utf-8',{fatal:true}).decode(new Uint8Array(bytes));}
  catch{return null;}
}

function tailnetAddress(value){
  if(typeof value!=='string'||value.includes('%'))return false;
  if(isIP(value)===4){const octets=value.split('.').map(Number);return octets[0]===100&&octets[1]>=64&&octets[1]<=127;}
  return isIP(value)===6&&new URL(`http://[${value}]/`).hostname.startsWith('[fd7a:115c:a1e0:');
}

export function createTailnetAccess({origin,ownerLogin}={}){
  let url;
  try{url=new URL(origin);}catch{throw new Error('Private access requires an exact Tailscale HTTPS origin.');}
  if(typeof origin!=='string'||url.protocol!=='https:'||url.origin!==origin
      ||!/^([a-z\d](?:[a-z\d-]{0,61}[a-z\d])?)\.([a-z\d](?:[a-z\d-]{0,61}[a-z\d])?)\.ts\.net$/.test(url.hostname))
    throw new Error('Private access requires an exact Tailscale HTTPS origin.');
  if(typeof ownerLogin!=='string'||!ownerLogin||ownerLogin.length>512||/[\s\x00-\x1f\x7f]/u.test(ownerLogin))
    throw new Error('Private access requires an approved Tailscale owner.');
  return Object.freeze({origin,authorize(request,{websocket=false}={}){
    if(!LOOPBACK.has(request?.socket?.remoteAddress))return DENIED;
    const headers=singleHeaders(request);
    if(!headers||headers.has('tailscale-funnel-request')||headers.has('authorization')||headers.has('forwarded'))return DENIED;
    if(headers.get('host')!==url.host||headers.get('x-forwarded-host')!==url.host
        ||headers.get('x-forwarded-proto')!=='https'||!tailnetAddress(headers.get('x-forwarded-for'))
        ||decodeLogin(headers.get('tailscale-user-login'))!==ownerLogin)return DENIED;
    const suppliedOrigin=headers.get('origin');
    if(suppliedOrigin!==undefined&&suppliedOrigin!==origin)return DENIED;
    if(typeof request.method!=='string'||(!SAFE_METHODS.has(request.method)||websocket)&&suppliedOrigin!==origin)return DENIED;
    if(typeof request.url!=='string'||!request.url.startsWith('/')||request.url.startsWith('//'))return DENIED;
    return ALLOWED;
  }});
}
