import {createHash,randomBytes,randomInt,timingSafeEqual} from 'node:crypto';
import {mkdir,open,rename,unlink} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {MAX_APPROVED_DEVICES,readOwnedFile} from './approved-device.mjs';
import {deviceName,LAN_DEFAULT_PORT,pairingCode,privateIPv4} from './lan-address.mjs';
import {sameOriginRequest,singleHeaders} from './tailnet-access.mjs';

// LAN access is plain HTTP to this PC's private IPv4 addresses. Each device pairs with a code shown on this PC and
// then sends its own key in a cookie. The codes live in memory only; the settings file keeps a hash of each key.
export {LAN_DEFAULT_PORT};
export const PAIRING_CODE_TTL_MS=300000;
export const PAIRING_ATTEMPTS=5;
export const MAX_LAN_DEVICES=MAX_APPROVED_DEVICES;
const MAX_RECORD_BYTES=16384;
export const MAX_SETTING_BYTES=1024;
const KEY_MAX_AGE_SECONDS=31536000;
const CLIENTS=['browser','app'];
const DEVICE_KEYS=['id','name','client','key_sha256','paired_at','paired_from'];

const MESSAGES={
  lan_access_port_in_use:'Another program uses this port. Choose another LAN port in Connection options.',
  lan_access_listener_failed:'EE could not open its LAN port. Choose another LAN port in Connection options, then try again.',
  lan_access_save_failed:'The LAN access setting could not be saved. Check the local data folder and try again.',
  lan_access_turned_off:'The change could not be saved, so EE turned off LAN access and removed every paired device. Check the local data folder, then turn on LAN access again.',
  lan_access_revoke_unsaved:'EE turned off LAN access and removed every paired device, but could not save this. Paired devices can connect again after EE restarts. Check the local data folder, then apply the setting again with LAN access off.',
  lan_access_invalid_configuration:'The saved LAN access settings could not be read, so LAN access is off and no devices are paired. Turn it on to start again.',
  lan_access_port_change_requires_off:'Turn off LAN access before changing its port.',
};
// Requests the local gateway refuses with their own status instead of reporting them in the status.
const REFUSALS={
  lan_access_off:[409,'Turn on LAN access before pairing a device.'],
  lan_access_device_limit:[409,`${MAX_LAN_DEVICES} devices are paired, the most EE allows. Remove one before pairing another.`],
  not_found:[404,'That device is no longer paired.'],
};
const DENIED=Object.freeze({ok:false,code:'lan_access_denied',message:'Open EE at the address shown in LAN access on the computer running EE.'});
const NOT_PAIRED=Object.freeze({ok:false,pairing:true});
const PAIRING_REQUIRED=Object.freeze({detail:{code:'lan_pairing_required',message:'Pair this device first. Enter the pairing code shown on the computer running EE.'}});
const PATH_DENIED=Object.freeze({detail:{code:'lan_access_denied',message:'This request cannot be opened through LAN access.'}});
const PAIRING_FAILED={detail:{code:'lan_pairing_failed',message:'The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one.'}};
const PAIRING_INVALID={detail:{code:'invalid',message:'Enter the 6-digit pairing code and a name of up to 40 characters for this device.'}};
const PAIRING_SAVE_FAILED={detail:{code:'lan_pairing_save_failed',message:'The device could not be saved on the computer running EE. Ask for a new pairing code there and try again.'}};
const TOO_LARGE={detail:{code:'too_large',message:'The request is too large.'}};
// Nothing proxies to the LAN listener, so a proxy or Authorization header means the request is not what it seems.
const proxyHeader=name=>name==='authorization'||name==='forwarded'||name.startsWith('x-forwarded-');
const HEADER_LIMITS=new Map([...['host','origin','sec-fetch-site','sec-fetch-mode'].map(name=>[name,1024]),['cookie',4096]]);

const sha256=value=>createHash('sha256').update(value).digest('hex');
const iso=time=>new Date(time).toISOString();
const validPort=port=>Number.isSafeInteger(port)&&port>=1024&&port<=65535;
const exactKeys=(value,keys)=>value!==null&&typeof value==='object'&&!Array.isArray(value)
  &&Object.keys(value).length===keys.length&&keys.every(key=>Object.hasOwn(value,key));
const isoTime=value=>typeof value==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value)
  &&!Number.isNaN(Date.parse(value))&&new Date(value).toISOString()===value;
function fail(code){throw Object.assign(new Error(MESSAGES[code]),{code});}
function refusal(code){const [status,message]=REFUSALS[code];return Object.assign(new Error(message),{code,status});}

const group=address=>address.startsWith('192.168.')?0:address.startsWith('10.')?1:2;
const numeric=address=>address.split('.').reduce((value,part)=>value*256+Number(part),0);
function privateInterfaces(interfaces){
  const found=[];
  for(const [name,entries] of Object.entries(interfaces??{}))
    for(const entry of Array.isArray(entries)?entries:[])
      if(entry?.family==='IPv4'&&entry.internal===false&&privateIPv4(entry.address)===entry.address)found.push({address:entry.address,name});
  return found.sort((a,b)=>group(a.address)-group(b.address)||numeric(a.address)-numeric(b.address));
}

// The address other devices open for each private IPv4 address of this PC, 192.168/16 first, then 10/8, then
// 172.16/12. Hyper-V, WSL, Docker and VPN adapters are listed with their names so the owner can pick the right one.
export function lanAddresses(port,interfaces){
  return privateInterfaces(interfaces).map(({address,name})=>({url:`http://${address}:${port}`,interface:name}));
}

// A request body of at most limit bytes, or null for a longer one.
export async function readLimited(request,limit){
  const chunks=[];let size=0;
  for await(const chunk of request){
    const bytes=Buffer.from(chunk);size+=bytes.length;
    if(size>limit)return null;
    chunks.push(bytes);
  }
  return Buffer.concat(chunks).toString('utf8');
}

function parseRecord(text){
  let value;
  try{value=JSON.parse(text);}catch{return null;}
  if(!exactKeys(value,['version','enabled','port','devices'])||value.version!==1||typeof value.enabled!=='boolean'
      ||!validPort(value.port)||!Array.isArray(value.devices)||value.devices.length>MAX_LAN_DEVICES)return null;
  const ids=new Set(),hashes=new Set();
  for(const device of value.devices){
    if(!exactKeys(device,DEVICE_KEYS)||typeof device.id!=='string'||!/^[0-9a-f]{16}$/.test(device.id)||ids.has(device.id)
        ||typeof device.key_sha256!=='string'||!/^[0-9a-f]{64}$/.test(device.key_sha256)||hashes.has(device.key_sha256)
        ||typeof device.name!=='string'||deviceName(device.name)!==device.name||!CLIENTS.includes(device.client)
        ||!isoTime(device.paired_at)||typeof device.paired_from!=='string'||privateIPv4(device.paired_from)!==device.paired_from)return null;
    ids.add(device.id);hashes.add(device.key_sha256);
  }
  return {enabled:value.enabled,port:value.port,devices:value.devices.map(device=>({...device}))};
}

function pairingBody(text){
  let value;
  try{value=JSON.parse(text);}catch{return null;}
  if(value===null||typeof value!=='object'||Array.isArray(value)||!Object.hasOwn(value,'code')||!Object.hasOwn(value,'name')
      ||Object.keys(value).some(key=>!['code','name','client'].includes(key)))return null;
  const code=pairingCode(value.code),name=deviceName(value.name),client=Object.hasOwn(value,'client')?value.client:'browser';
  return code&&name&&CLIENTS.includes(client)?{code,name,client}:null;
}

/**
 * LAN access settings, pairing codes and paired devices. startListener(gate, port) opens the LAN listener with the gate
 * returned here, stopListener() closes it and every connection, and recheck() checks every device WebSocket again,
 * including one still waiting for EE to accept it.
 * Every change runs one at a time; a removal or turning LAN access off takes effect in memory before it is saved.
 */
export function createLanAccessManager({configPath,startListener,stopListener,recheck,interfaces=os.networkInterfaces,now=Date.now,
  random=()=>randomInt(0,1000000)}={}){
  if(typeof configPath!=='string'||!path.isAbsolute(configPath)||![startListener,stopListener,recheck].every(item=>typeof item==='function'))
    throw new Error('LAN access requires local configuration and listener callbacks.');
  let record={enabled:false,port:LAN_DEFAULT_PORT,devices:[]},loaded=false,ready=false,error=null,pairing=null;
  // A turn-off or removal that EE could neither save nor undo by deleting the file: the file still lists devices.
  let revocationUnsaved=false;
  let operation=Promise.resolve();
  const lastUse=new Map();
  const serial=action=>{const result=operation.catch(()=>{}).then(action);operation=result;return result;};
  const setError=code=>{error={code,message:MESSAGES[code]};};
  const thisComputer=()=>new Set(privateInterfaces(interfaces()).map(entry=>entry.address));
  const cookieName=()=>`ee_lan_${record.port}`;

  async function load(){
    if(loaded)return;
    loaded=true;
    let text;
    try{text=await readOwnedFile(configPath,MAX_RECORD_BYTES);}
    catch(failure){
      if(failure.code==='ENOENT')return;
      text=null;
    }
    const saved=text===null?null:parseRecord(text);
    if(saved)record=saved;else setError('lan_access_invalid_configuration');
  }
  async function save(next){
    const temporary=`${configPath}.${randomBytes(8).toString('hex')}.tmp`;
    try{
      await mkdir(path.dirname(configPath),{recursive:true});
      const file=await open(temporary,'wx',0o600);
      try{await file.writeFile(JSON.stringify({version:1,...next})+'\n');await file.sync();}finally{await file.close();}
      await rename(temporary,configPath);
    }finally{await unlink(temporary).catch(()=>{});}
    record=next;revocationUnsaved=false;
  }
  async function openListener(port){
    try{await startListener(gate,port);}
    catch(failure){fail(failure?.code==='EADDRINUSE'?'lan_access_port_in_use':'lan_access_listener_failed');}
    ready=true;
  }
  async function closeListener(){
    ready=false;pairing=null;
    await stopListener();
  }
  // Without a saved record a restart could bring the removed devices back, so EE turns LAN access off and deletes the
  // file, which reads as off with no devices.
  async function turnOffUnsaved(){
    record={...record,enabled:false,devices:[]};lastUse.clear();
    await closeListener();
    try{await unlink(configPath);}
    catch(failure){
      if(failure.code!=='ENOENT'){revocationUnsaved=true;setError('lan_access_revoke_unsaved');return;}
    }
    revocationUnsaved=false;setError('lan_access_turned_off');
  }
  async function enable(port){
    if(ready)return;
    try{await openListener(port);}catch(failure){setError(failure.code);return;}
    try{await save({enabled:true,port,devices:record.devices});}
    catch{await closeListener();setError('lan_access_save_failed');}
  }
  // The off record is written even when LAN access is already off, so a save that failed earlier can be retried.
  async function disable(port){
    const revoking=record.enabled||ready||record.devices.length>0||revocationUnsaved;
    record={...record,devices:[]};lastUse.clear();
    await closeListener();
    try{await save({enabled:false,port,devices:[]});}
    catch{if(revoking)await turnOffUnsaved();else setError('lan_access_save_failed');}
  }
  // An expired code is reported as ended when it is next read.
  function currentPairing(){
    if(pairing?.code&&now()>=pairing.expires)pairing={...pairing,code:null,ended:'expired'};
    return pairing;
  }
  function pairedDevice(header){
    if(header===undefined)return null;
    const name=cookieName(),hashes=[];
    // Every entry with this name is compared, so a decoy cookie cannot hide the device's own.
    for(const part of header.split(';')){
      const entry=part.trim(),split=entry.indexOf('=');
      if(split>0&&entry.slice(0,split)===name)hashes.push(Buffer.from(sha256(entry.slice(split+1)),'hex'));
    }
    let found=null;
    for(const device of record.devices){
      const saved=Buffer.from(device.key_sha256,'hex');
      for(const hash of hashes)if(timingSafeEqual(hash,saved))found??=device;
    }
    return found;
  }
  async function pair({code,name,client},remoteAddress){
    const active=currentPairing();
    if(!active?.code)return {status:403,body:PAIRING_FAILED};
    if(!timingSafeEqual(Buffer.from(code),Buffer.from(active.code))){
      const attempts=active.attempts-1;
      pairing=attempts>0?{...active,attempts}:{...active,attempts,code:null,ended:'attempts'};
      return {status:403,body:PAIRING_FAILED};
    }
    // A matching code is used up whatever happens next.
    pairing={...active,code:null};
    const key=randomBytes(32).toString('hex');
    const device={id:randomBytes(8).toString('hex'),name,client,key_sha256:sha256(key),paired_at:iso(now()),paired_from:privateIPv4(remoteAddress)};
    try{await save({...record,devices:[...record.devices,device]});}
    catch{pairing={...pairing,ended:'failed'};return {status:500,body:PAIRING_SAVE_FAILED};}
    pairing={...pairing,ended:'used'};
    return {status:200,body:{paired:true,device:{id:device.id,name,client,paired_at:device.paired_at}},
      cookie:`${cookieName()}=${key}; Path=/api; Max-Age=${KEY_MAX_AGE_SECONDS}; HttpOnly; SameSite=Strict`};
  }

  const gate=Object.freeze({
    pairingRequired:PAIRING_REQUIRED,
    pathDenied:PATH_DENIED,
    // Only connections between a private address of this PC and a private address elsewhere; Tailscale's 100.64/10
    // addresses are not private here, so devices on Tailscale use its encrypted address.
    acceptConnection(socket){
      const local=privateIPv4(socket.localAddress);
      return Boolean(local&&thisComputer().has(local)&&privateIPv4(socket.remoteAddress));
    },
    authorize(request,{websocket=false}={}){
      const socket=request.socket,local=privateIPv4(socket?.localAddress);
      const headers=singleHeaders(request,HEADER_LIMITS);
      if(!headers||!local||!thisComputer().has(local)
          ||request.rawHeaders.some((name,index)=>index%2===0&&proxyHeader(name.toLowerCase())))return DENIED;
      // The Host must name the address the connection arrived on, so a page on another name that resolves to this PC
      // (DNS rebinding) can neither use a device's cookie nor guess codes.
      const host=`${local}:${socket.localPort}`;
      if(headers.get('host')!==host||!sameOriginRequest({headers,method:request.method,url:request.url,origin:`http://${host}`,websocket}))return DENIED;
      const device=pairedDevice(headers.get('cookie'));
      if(!device)return NOT_PAIRED;
      lastUse.set(device.id,{last_used_at:iso(now()),last_address:privateIPv4(socket.remoteAddress)});
      return {ok:true,device:true,id:device.id};
    },
    workspace:result=>({live:true,remote:true,lan:true,paired:result?.ok===true}),
    async answer(request,response,pathname){
      if(pathname!=='/api/lan-access/pair'||request.method!=='POST')return false;
      const reply=(status,body,cookie)=>{
        response.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store','X-Content-Type-Options':'nosniff',
          'Referrer-Policy':'no-referrer',...(cookie?{'Set-Cookie':cookie}:{})});
        response.end(JSON.stringify(body));
      };
      if(Number(request.headers['content-length'])>MAX_SETTING_BYTES){reply(413,TOO_LARGE);return true;}
      const text=await readLimited(request,MAX_SETTING_BYTES);
      if(text===null){reply(413,TOO_LARGE);return true;}
      // The body is checked before the code is looked at, so a malformed request costs no attempt.
      const body=pairingBody(text);
      if(!body){reply(400,PAIRING_INVALID);return true;}
      const result=await serial(()=>pair(body,request.socket.remoteAddress));
      reply(result.status,result.body,result.cookie);
      return true;
    },
  });

  function status({admin=false,closing=false}={}){
    const active=currentPairing(),configurable=admin&&!closing;
    return {available:true,enabled:record.enabled,ready,port:record.port,addresses:ready?lanAddresses(record.port,interfaces()):[],
      error:error&&{...error},
      pairing:admin&&active?{code:active.code,expires_at:iso(active.expires),attempts_left:active.attempts,ended:active.ended}:null,
      devices:admin?record.devices.map(({id,name,client,paired_at,paired_from})=>({id,name,client,paired_at,paired_from,
        ...(lastUse.get(id)??{last_used_at:null,last_address:null})})):null,
      max_devices:MAX_LAN_DEVICES,can_configure:configurable,can_change_port:configurable&&!record.enabled&&!ready,admin_required:!admin};
  }

  return Object.freeze({
    status,
    gate,
    initialize:()=>serial(async()=>{
      await load();
      if(!record.enabled||ready)return;
      try{await openListener(record.port);}catch(failure){setError(failure.code);}
    }),
    configure:(enabled,{port}={})=>serial(async()=>{
      await load();
      if(port!==undefined&&port!==record.port&&(record.enabled||ready)){setError('lan_access_port_change_requires_off');return;}
      error=null;
      await (enabled?enable(port??record.port):disable(port??record.port));
    }),
    issueCode:()=>serial(async()=>{
      await load();
      if(!ready)throw refusal('lan_access_off');
      if(record.devices.length>=MAX_LAN_DEVICES)throw refusal('lan_access_device_limit');
      pairing={code:String(random()).padStart(6,'0'),expires:now()+PAIRING_CODE_TTL_MS,attempts:PAIRING_ATTEMPTS,ended:null};
    }),
    revoke:id=>serial(async()=>{
      await load();
      const device=record.devices.find(item=>item.id===id);
      if(!device)throw refusal('not_found');
      record={...record,devices:record.devices.filter(item=>item!==device)};lastUse.delete(id);
      await recheck();
      try{await save(record);}catch{await turnOffUnsaved();}
    }),
    // EE is closing: the listener stops and the saved setting stays, so the next start opens it again.
    close:()=>serial(closeListener),
  });
}
