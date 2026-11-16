import {canonicalTailnetAddress} from './approved-device.mjs';

const SAFE_METHODS=new Set(['GET','HEAD','OPTIONS']);
const LOOPBACK=new Set(['127.0.0.1','::1','::ffff:127.0.0.1']);
const IDENTITY_HEADERS=new Set(['host','origin','x-forwarded-host','x-forwarded-proto','x-forwarded-for',
  'tailscale-user-login','tailscale-funnel-request','authorization','forwarded','sec-fetch-site','sec-fetch-mode']);
const SAME_SITE_FETCH=new Set(['same-origin','none']);
export const ACCESS_AUDIENCES=Object.freeze(['owner','accounts']);
const DENIED=Object.freeze({ok:false,status:403,code:'private_access_denied'});
const ALLOWED=Object.freeze({ok:true});
const DEVICE_ALLOWED=Object.freeze({ok:true,device:true});

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

// An accountless request can use only a separately approved device identity.
export function createTailnetAccess({origin,ownerLogin,audience='owner',verifyDevice}={}){
  let url;
  try{url=new URL(origin);}catch{throw new Error('Private access requires an exact Tailscale HTTPS origin.');}
  if(typeof origin!=='string'||url.protocol!=='https:'||url.origin!==origin
      ||!/^([a-z\d](?:[a-z\d-]{0,61}[a-z\d])?)\.([a-z\d](?:[a-z\d-]{0,61}[a-z\d])?)\.ts\.net$/.test(url.hostname))
    throw new Error('Private access requires an exact Tailscale HTTPS origin.');
  if(typeof ownerLogin!=='string'||!ownerLogin||ownerLogin.length>512||/[\s\x00-\x1f\x7f]/u.test(ownerLogin))
    throw new Error('Private access requires an approved Tailscale owner.');
  if(!ACCESS_AUDIENCES.includes(audience))throw new Error('Private access requires a known audience.');
  return Object.freeze({origin,authorize(request,{websocket=false}={}){
    if(!LOOPBACK.has(request?.socket?.remoteAddress))return DENIED;
    const headers=singleHeaders(request);
    if(!headers||headers.has('tailscale-funnel-request')||headers.has('authorization')||headers.has('forwarded'))return DENIED;
    const sourceIp=canonicalTailnetAddress(headers.get('x-forwarded-for'));
    if(headers.get('host')!==url.host||headers.get('x-forwarded-host')!==url.host
        ||headers.get('x-forwarded-proto')!=='https'||!sourceIp)return DENIED;
    const suppliedOrigin=headers.get('origin');
    if(suppliedOrigin!==undefined&&suppliedOrigin!==origin)return DENIED;
    if(typeof request.method!=='string'||(!SAFE_METHODS.has(request.method)||websocket)&&suppliedOrigin!==origin)return DENIED;
    if(typeof request.url!=='string'||!request.url.startsWith('/')||request.url.startsWith('//'))return DENIED;
    // A simple cross-site GET, such as an <img> source on another page, carries no Origin but still names its site.
    // Only a top-level navigation to a workspace page may come from elsewhere, so links from other apps still open.
    const site=headers.get('sec-fetch-site');
    if(site!==undefined&&!SAME_SITE_FETCH.has(site)&&(websocket||request.method!=='GET'
        ||headers.get('sec-fetch-mode')!=='navigate'||request.url.startsWith('/api/')))return DENIED;
    if(headers.has('tailscale-user-login')){
      const login=decodeLogin(headers.get('tailscale-user-login'));
      if(!login||/[\s\x00-\x1f\x7f]/u.test(login)||audience==='owner'&&login!==ownerLogin)return DENIED;
      return ALLOWED;
    }
    if(typeof verifyDevice!=='function')return DENIED;
    return Promise.resolve().then(()=>verifyDevice(sourceIp)).then(ok=>ok===true?DEVICE_ALLOWED:DENIED,()=>DENIED);
  }});
}
