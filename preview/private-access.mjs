import {createHash,randomBytes} from 'node:crypto';
import {lstat,mkdir,open,readFile,rename,unlink} from 'node:fs/promises';
import path from 'node:path';
import {createTailnetAccess} from './tailnet-access.mjs';
import {runInstalledTailscale,tailscaleActionUrl} from './tailscale-cli.mjs';
export {runInstalledTailscale} from './tailscale-cli.mjs';

export const PRIVATE_HTTPS_PORT=8444;
export const PRIVATE_LISTENER_PORT=4184;
const targetFor=port=>`http://127.0.0.1:${port}`;
const validPort=port=>Number.isSafeInteger(port)&&port>=1024&&port<=65535;
const MESSAGES={
  tailscale_not_installed:'Install Tailscale on this computer, sign in, then refresh status.',
  tailscale_permission_required:'Tailscale needs permission to manage private access. Open EE as an administrator and try again.',
  tailscale_approval_required:'Complete the approval in Tailscale, then refresh status and apply the setting.',
  tailscale_not_running:'Connect Tailscale on this computer, then refresh status.',
  tailscale_device_identity_required:'Use a personal Tailscale account on the computer running EE, then refresh status.',
  private_access_port_change_requires_off:'Turn private access off and confirm cleanup before changing its port.',
  tailscale_unavailable:'Open Tailscale on this computer, then check again.',
  tailscale_signin_required:'Connect this computer to Tailscale, then check again.',
  private_access_identity_changed:'The approved Tailscale account or computer identity changed. Turn private access off before setting it up again.',
  private_access_port_in_use:'Another Tailscale service uses this port. Keep that service unchanged and choose a separate port for EE.',
  private_access_route_unavailable:'The saved private address is unavailable. Apply the private access setting to reconnect it.',
  private_access_funnel_enabled:'This port is configured for public access. EE requires a private Tailscale Serve address.',
  private_access_listener_failed:'The private EE connection could not start. Check that its local port is available.',
  private_access_save_failed:'The private access setting could not be saved. Check the local data folder and try again.',
  private_access_setup_unconfirmed:'Private access setup could not be confirmed. Its EE connection remains closed; check again before retrying.',
  private_access_cleanup_unconfirmed:'The private EE connection is closed, but removal of its Tailscale route is unconfirmed. Check again before retrying.',
  private_access_shutdown_unconfirmed:'Private access shutdown could not be confirmed. Review the local connection before retrying.',
  private_access_invalid_configuration:'The private access configuration could not be verified. Review its saved settings before retrying.',
};
const hash=value=>createHash('sha256').update(value).digest('hex');
const object=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
function fail(code){const error=new Error(MESSAGES[code]);error.code=code;throw error;}

function parseJSON(value){
  if(typeof value!=='string'||Buffer.byteLength(value)>65536)fail('private_access_invalid_configuration');
  try{const parsed=JSON.parse(value);if(object(parsed))return parsed;}catch{}
  fail('private_access_invalid_configuration');
}

function identityFrom(status,httpsPort){
  const self=status.Self;
  if(status.BackendState!=='Running'||!object(self)||self.Online!==true||self.Expired===true
      ||(self.Tags!==undefined&&(!Array.isArray(self.Tags)||self.Tags.length)))fail('tailscale_signin_required');
  if(!Number.isSafeInteger(self.UserID)||self.UserID<=0||typeof self.ID!=='string'||!self.ID||!object(status.User))
    fail('tailscale_signin_required');
  const user=status.User[String(self.UserID)];
  if(!object(user)||user.ID!==self.UserID||typeof user.LoginName!=='string'||typeof self.DNSName!=='string')
    fail('tailscale_signin_required');
  const name=self.DNSName.endsWith('.')?self.DNSName.slice(0,-1):self.DNSName;
  const origin=`https://${name}:${httpsPort}`;
  let gate;
  try{gate=createTailnetAccess({origin,ownerLogin:user.LoginName});}catch{fail('tailscale_signin_required');}
  return {origin,ownerHash:hash(user.LoginName),nodeHash:hash(self.ID),gate};
}

function validateSaved(value){
  if(!object(value)||![1,2].includes(value.version)||typeof value.enabled!=='boolean'
      ||!/^https:\/\/[a-z\d-]+\.[a-z\d-]+\.ts\.net:\d+$/.test(value.origin)
      ||!/^[a-f\d]{64}$/.test(value.ownerHash||'')||!/^[a-f\d]{64}$/.test(value.nodeHash||''))
    fail('private_access_invalid_configuration');
  const httpsPort=value.version===1?PRIVATE_HTTPS_PORT:value.httpsPort;
  const listenerPort=value.version===1?PRIVATE_LISTENER_PORT:value.listenerPort;
  if(!validPort(httpsPort)||!validPort(listenerPort)||Number(new URL(value.origin).port)!==httpsPort)fail('private_access_invalid_configuration');
  return {version:2,enabled:value.enabled,origin:value.origin,ownerHash:value.ownerHash,nodeHash:value.nodeHash,httpsPort,listenerPort};
}

function portKey(key,port){return key===String(port)||key.endsWith(`:${port}`);}
function containsPort(value,port,depth=0){
  if(!object(value)||depth>8)return false;
  return Object.entries(value).some(([key,item])=>portKey(key,port)||containsPort(item,port,depth+1));
}
function routeState(serve,origin,listenerPort){
  const port=Number(new URL(origin).port);
  for(const key of ['TCP','Web','AllowFunnel','Foreground','Services'])
    if(serve[key]!==undefined&&!object(serve[key]))fail('private_access_invalid_configuration');
  if(Object.entries(serve.AllowFunnel||{}).some(([key,value])=>portKey(key,port)&&value))return 'public';
  if(containsPort(serve.Foreground,port)||containsPort(serve.Services,port))return 'occupied';
  const web=serve.Web||{},tcp=serve.TCP||{},authority=new URL(origin).host;
  const authorities=Object.keys(web).filter(key=>portKey(key,port));
  if(!Object.hasOwn(tcp,String(port))&&!authorities.length)return 'absent';
  const listener=tcp[String(port)],site=web[authority],handlers=site?.Handlers;
  if(object(listener)&&Object.keys(listener).length===1&&listener.HTTPS===true
      &&authorities.length===1&&authorities[0]===authority&&object(site)&&Object.keys(site).length===1
      &&object(handlers)&&Object.keys(handlers).length===1&&object(handlers['/'])
      &&Object.keys(handlers['/']).length===1&&handlers['/'].Proxy===targetFor(listenerPort))return 'exact';
  return 'occupied';
}

function unrelatedRoutes(serve,port){
  const result=structuredClone(serve);
  for(const group of ['TCP','Web','AllowFunnel']){
    if(!object(result[group]))continue;
    for(const key of Object.keys(result[group]))if(portKey(key,port))delete result[group][key];
    if(!Object.keys(result[group]).length)delete result[group];
  }
  const stable=value=>Array.isArray(value)?value.map(stable):object(value)
    ?Object.fromEntries(Object.keys(value).sort().map(key=>[key,stable(value[key])])):value;
  return JSON.stringify(stable(result));
}

export function createPrivateAccessManager({configPath,runTailscale=runInstalledTailscale,startListener,stopListener}={}){
  if(typeof configPath!=='string'||!path.isAbsolute(configPath)||typeof startListener!=='function'||typeof stopListener!=='function')
    throw new Error('Private access requires local configuration and listener callbacks.');
  let saved=null,loaded=false,operation=Promise.resolve(),httpsPort=PRIVATE_HTTPS_PORT,listenerPort=PRIVATE_LISTENER_PORT;
  let state={available:false,enabled:false,ready:false,url:null,error:null,setup_action:null,action_url:null};
  const status=()=>({...state,https_port:httpsPort,can_change_port:!state.enabled&&!state.ready&&!['private_access_cleanup_unconfirmed','private_access_shutdown_unconfirmed','private_access_invalid_configuration','private_access_identity_changed'].includes(state.error?.code),error:state.error?{...state.error}:null});
  const serial=action=>{const result=operation.catch(()=>{}).then(action);operation=result;return result;};
  const setError=error=>{const code=Object.hasOwn(MESSAGES,error?.code)?error.code:'tailscale_unavailable';state.error={code,message:MESSAGES[code]};
    if(code==='tailscale_not_installed'){state.setup_action='install';state.action_url='https://tailscale.com/download';}
    if(code==='tailscale_signin_required'){state.setup_action='signin';state.action_url=null;}
    const action=tailscaleActionUrl(error?.actionUrl);if(action){state.setup_action='approve';state.action_url=action;}
  };
  const reset=()=>{state.error=null;state.setup_action=null;state.action_url=null;state.available=false;state.ready=false;state.url=null;};
  async function load(){
    if(loaded)return;
    try{
      const info=await lstat(configPath);
      if(!info.isFile()||info.isSymbolicLink()||info.size>8192)fail('private_access_invalid_configuration');
      saved=validateSaved(parseJSON(await readFile(configPath,'utf8')));
      state.enabled=saved.enabled;httpsPort=saved.httpsPort;listenerPort=saved.listenerPort;
    }catch(error){
      if(error.code!=='ENOENT'){
        if(Object.hasOwn(MESSAGES,error.code))throw error;
        fail('private_access_invalid_configuration');
      }
    }
    loaded=true;
  }
  async function save(value){
    const temporary=`${configPath}.${randomBytes(8).toString('hex')}.tmp`;
    try{
      await mkdir(path.dirname(configPath),{recursive:true});
      const file=await open(temporary,'wx',0o600);
      try{await file.writeFile(JSON.stringify(value)+'\n');await file.sync();}finally{await file.close();}
      await rename(temporary,configPath);saved=value;state.enabled=value.enabled;
    }catch{fail('private_access_save_failed');}
    finally{await unlink(temporary).catch(()=>{});}
  }
  async function identityStatus(){
    let text;
    try{text=await runTailscale(['status','--json','--peers=false']);}catch(error){if(Object.hasOwn(MESSAGES,error.code))throw error;fail('tailscale_unavailable');}
    return parseJSON(text);
  }
  async function identity(){
    const info=await identityStatus();
    if(info.BackendState==='NeedsMachineAuth'){
      const error=new Error();error.code='tailscale_approval_required';error.actionUrl='https://login.tailscale.com/admin/machines';throw error;
    }
    if(info.BackendState!=='Running'&&info.BackendState!=='NeedsLogin')fail('tailscale_not_running');
    if(info.BackendState==='Running'&&(info.Self?.Expired===true||Array.isArray(info.Self?.Tags)&&info.Self.Tags.length))fail('tailscale_device_identity_required');
    const current=identityFrom(info,httpsPort);state.available=true;return current;
  }
  async function serve(){return parseJSON(await runTailscale(['serve','status','--json']));}
  async function close(){
    state.ready=false;state.url=null;
    try{await stopListener();}catch{fail('private_access_shutdown_unconfirmed');}
  }
  async function stopAfter(error){try{await close();}catch(failure){error=failure;}setError(error);}
  function matches(current){
    return !saved||saved.ownerHash===current.ownerHash&&saved.nodeHash===current.nodeHash&&saved.origin===current.origin;
  }
  async function openListener(current,requested=listenerPort){
    try{const address=await startListener(current.gate,requested);if(address?.port&&validPort(address.port))listenerPort=address.port;}
    catch{fail('private_access_listener_failed');}
  }
  async function initialize(){
    reset();
    try{
      await load();const current=await identity();
      if(!saved?.enabled){
        await close();
        if(saved&&routeState(await serve(),saved.origin,saved.listenerPort)!=='absent')fail('private_access_cleanup_unconfirmed');
        return status();
      }
      if(!matches(current))fail('private_access_identity_changed');
      const route=routeState(await serve(),saved.origin,saved.listenerPort);
      if(route==='public')fail('private_access_funnel_enabled');
      if(route!=='exact')fail(route==='absent'?'private_access_route_unavailable':'private_access_port_in_use');
      await openListener(current);
      state.ready=true;state.url=current.origin;
    }catch(error){await stopAfter(error);}
    return status();
  }
  async function enable(){
    reset();
    try{
      await load();const current=await identity();
      if(saved?.enabled&&!matches(current))fail('private_access_identity_changed');
      const before=await serve(),route=routeState(before,current.origin,listenerPort);
      if(route==='public')fail('private_access_funnel_enabled');
      if(route!=='absent'&&!(route==='exact'&&saved&&matches(current)))fail('private_access_port_in_use');
      await openListener(current,route==='absent'?0:listenerPort);
      await save({version:2,enabled:true,origin:current.origin,ownerHash:current.ownerHash,nodeHash:current.nodeHash,httpsPort,listenerPort});
      if(route==='absent'){
        let setupError;
        try{await runTailscale(['serve','--bg',`--https=${httpsPort}`,targetFor(listenerPort)]);}catch(error){setupError=error;}
        let after;
        try{after=await serve();}catch{fail('private_access_setup_unconfirmed');}
        if(routeState(after,current.origin,listenerPort)!=='exact'||unrelatedRoutes(before,httpsPort)!==unrelatedRoutes(after,httpsPort)){
          if(setupError?.actionUrl){setupError.code='tailscale_approval_required';throw setupError;}
          if(setupError?.code==='tailscale_permission_required')throw setupError;
          fail('private_access_setup_unconfirmed');
        }
      }
      state.ready=true;state.url=current.origin;state.error=null;
    }catch(error){await stopAfter(error);}
    return status();
  }
  async function disable(){
    state.error=null;state.setup_action=null;state.action_url=null;
    try{
      await close();await load();
      if(!saved){state.enabled=false;return status();}
      await save({...saved,enabled:false});
      let before;
      try{before=await serve();}catch{fail('private_access_cleanup_unconfirmed');}
      const route=routeState(before,saved.origin,saved.listenerPort);
      if(route==='absent')return status();
      if(route!=='exact')fail('private_access_cleanup_unconfirmed');
      try{await runTailscale(['serve',`--https=${saved.httpsPort}`,'off']);}catch{}
      let after;
      try{after=await serve();}catch{fail('private_access_cleanup_unconfirmed');}
      if(routeState(after,saved.origin,saved.listenerPort)!=='absent'||unrelatedRoutes(before,saved.httpsPort)!==unrelatedRoutes(after,saved.httpsPort))
        fail('private_access_cleanup_unconfirmed');
    }catch(error){state.ready=false;state.url=null;setError(error);}
    return status();
  }
  async function login(){
    await initialize();
    if(state.error?.code!=='tailscale_signin_required')return status();
    const info=await identityStatus();
    if(info.BackendState!=='NeedsLogin')return status();
    try{await runTailscale(['login','--timeout=5s']);}
    catch(error){setError(error);return status();}
    return initialize();
  }
  return Object.freeze({status,initialize:()=>serial(initialize),login:()=>serial(login),configure:(enabled,{httpsPort:requestedPort}={})=>{
    if(typeof enabled!=='boolean')return Promise.reject(new Error('Choose whether to enable private access.'));
    if(requestedPort!==undefined&&!validPort(requestedPort))return Promise.reject(new Error('Choose a port between 1024 and 65535.'));
    return serial(async()=>{
      try{
      await load();
      if(requestedPort!==undefined&&requestedPort!==httpsPort){
        if(saved?.enabled||state.enabled||state.ready)fail('private_access_port_change_requires_off');
        if(saved&&routeState(await serve(),saved.origin,saved.listenerPort)!=='absent')fail('private_access_cleanup_unconfirmed');
        const previousPort=httpsPort;httpsPort=requestedPort;
        // Persist a new port only with a verified identity; preserve the previous ownership record until then.
        try{
          const current=await identity();
          await save({version:2,enabled:false,origin:current.origin,ownerHash:current.ownerHash,nodeHash:current.nodeHash,httpsPort,listenerPort});
        }catch(error){httpsPort=previousPort;throw error;}
      }
      return enabled?enable():disable();
      }catch(error){setError(error);return status();}
    });
  },close:()=>serial(close)});
}
