import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import fsPromises,{chmod,mkdir,mkdtemp,readFile,rm,symlink,writeFile} from 'node:fs/promises';
import {syncBuiltinESMExports} from 'node:module';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {createLanAccessManager,lanAddresses,LAN_DEFAULT_PORT,PAIRING_ATTEMPTS,PAIRING_CODE_TTL_MS} from '../preview/lan-access.mjs';
import {deviceName,lanPage,loopbackHost,pairingCode,privateIPv4} from '../preview/lan-address.mjs';

// Private addresses other than the two documentation examples are built from parts, as the repository check requires.
const address=(...parts)=>parts.join('.');
const PC='192.168.1.2',PHONE='10.0.0.1',ORIGIN=`http://${PC}:${LAN_DEFAULT_PORT}`;
const ETHERNET={Ethernet:[{family:'IPv4',internal:false,address:PC}]};
const START=Date.parse('2026-11-16T20:10:12.000Z');

// Makes the named file operations fail as they do in a data folder that cannot be written. The manager imports them by
// name, so the live bindings are updated too.
function failing(t,names){
  const originals=Object.fromEntries(names.map(name=>[name,fsPromises[name]]));
  for(const name of names)fsPromises[name]=async()=>{throw Object.assign(new Error(`${name} failed`),{code:'EPERM'});};
  syncBuiltinESMExports();
  const restore=()=>{Object.assign(fsPromises,originals);syncBuiltinESMExports();};
  t.after(restore);
  return restore;
}

async function fixture(t,{interfaces=ETHERNET,random,startListener}={}){
  const directory=await mkdtemp(path.join(tmpdir(),'ee-lan-access-'));
  t.after(()=>rm(directory,{recursive:true,force:true}));
  const configPath=path.join(directory,'workspace','lan-access.json'),events=[],clock={time:START};
  const create=()=>createLanAccessManager({configPath,interfaces:()=>interfaces,now:()=>clock.time,random,
    startListener:startListener??(async(_gate,port)=>{events.push(['start',port]);}),
    stopListener:async()=>{events.push(['stop']);},recheck:async()=>{events.push(['recheck']);}});
  const lan=create();
  const saved=async()=>JSON.parse(await readFile(configPath,'utf8'));
  return {lan,create,events,clock,configPath,saved,admin:()=>lan.status({admin:true})};
}

function request({method='GET',url='/',headers={},local=PC,port=LAN_DEFAULT_PORT,remote=PHONE,body}={}){
  const all={host:`${local}:${port}`,...headers};
  const rawHeaders=Object.entries(all).filter(([,value])=>value!==undefined).flatMap(([name,value])=>Array.isArray(value)?value.flatMap(item=>[name,item]):[name,value]);
  return {method,url,rawHeaders,headers:Object.fromEntries(Object.entries(all).map(([name,value])=>[name.toLowerCase(),value])),
    socket:{localAddress:local,localPort:port,remoteAddress:remote},async *[Symbol.asyncIterator](){if(body!==undefined)yield Buffer.from(body);}};
}

async function pair(lan,{code,name='Kitchen iPad',client,remote=PHONE,body}={}){
  const response={writeHead(status,headers){this.status=status;this.headers=headers;},end(text){this.body=JSON.parse(text);}};
  const sent=body??JSON.stringify({code,name,...(client?{client}:{})});
  const handled=await lan.gate.answer(request({method:'POST',url:'/api/lan-access/pair',remote,body:sent,
    headers:{origin:ORIGIN,'content-length':String(Buffer.byteLength(sent))}}),response,'/api/lan-access/pair');
  assert.equal(handled,true);
  return response;
}
const cookieOf=response=>response.headers['Set-Cookie']?.split(';')[0];
const wrong=code=>String((Number(code)+1)%1000000).padStart(6,'0');

async function enabledWithCode(t,options){
  const f=await fixture(t,options);
  await f.lan.configure(true);await f.lan.issueCode();
  return {...f,code:f.admin().pairing.code};
}

test('private IPv4 addresses are the three private ranges only',()=>{
  for(const value of [PHONE,PC,address(172,16,0,1),address(172,31,255,254),address(10,255,255,255)])assert.equal(privateIPv4(value),value,value);
  assert.equal(privateIPv4(`::ffff:${PC}`),PC);assert.equal(privateIPv4(`::FFFF:${PHONE}`),PHONE);
  for(const value of [address(172,15,255,255),address(172,32,0,1),'100.64.0.1','100.100.1.1','127.0.0.1','169.254.1.1','8.8.8.8',
    '::1','fd00::1',address(192,168,1,256),address('010',0,0,1),`${PC}:4280`,' '+PC,'',undefined,null,42])
    assert.equal(privateIPv4(value),null,String(value));
  assert.equal(lanPage({protocol:'http:',hostname:PC,port:'4280'}),true);
  for(const page of [{protocol:'https:',hostname:PC,port:'4280'},{protocol:'http:',hostname:PC,port:''},{protocol:'http:',hostname:'mypc.local',port:'4280'},
    {protocol:'http:',hostname:'127.0.0.1',port:'4180'},{protocol:'http:',hostname:'[fd00::1]',port:'4280'}])assert.equal(lanPage(page),false,JSON.stringify(page));
  for(const host of ['127.0.0.1','localhost','[::1]'])assert.equal(loopbackHost(host),true);
  for(const host of [PC,'example.test','127.0.0.2'])assert.equal(loopbackHost(host),false);
});

test('LAN addresses list each private interface address with its port and interface name, in the fixed order',()=>{
  const interfaces={
    'vEthernet (WSL)':[{family:'IPv4',internal:false,address:address(172,20,0,1)}],
    'Wi-Fi':[{family:'IPv4',internal:false,address:PHONE},{family:'IPv6',internal:false,address:'fd00::2'}],
    Ethernet:[{family:'IPv4',internal:false,address:address(192,168,1,20)},{family:'IPv4',internal:false,address:PC}],
    Tailscale:[{family:'IPv4',internal:false,address:'100.100.1.1'}],
    'Loopback Pseudo-Interface 1':[{family:'IPv4',internal:true,address:'127.0.0.1'}],
  };
  assert.deepEqual(lanAddresses(4280,interfaces),[
    {url:`http://${PC}:4280`,interface:'Ethernet'},{url:`http://${address(192,168,1,20)}:4280`,interface:'Ethernet'},
    {url:`http://${PHONE}:4280`,interface:'Wi-Fi'},{url:`http://${address(172,20,0,1)}:4280`,interface:'vEthernet (WSL)'}]);
  assert.deepEqual(lanAddresses(4280,{}),[]);
});

test('with no saved file, LAN access is off and opens nothing',async t=>{
  const f=await fixture(t);
  await f.lan.initialize();
  assert.deepEqual(f.events,[]);
  assert.deepEqual(f.admin(),{available:true,enabled:false,ready:false,port:4280,addresses:[],error:null,pairing:null,devices:[],
    max_devices:16,can_configure:true,can_change_port:true,admin_required:false});
  await assert.rejects(readFile(f.configPath),{code:'ENOENT'});
});

test('turning LAN access on opens the listener on the saved port and saves a version 1 record',async t=>{
  const f=await fixture(t);
  await f.lan.configure(true);
  assert.deepEqual(f.events,[['start',4280]]);
  assert.deepEqual(await f.saved(),{version:1,enabled:true,port:4280,devices:[]});
  const status=f.admin();
  assert.equal(status.enabled,true);assert.equal(status.ready,true);assert.equal(status.can_change_port,false);
  assert.deepEqual(status.addresses,[{url:ORIGIN,interface:'Ethernet'}]);
  const restarted=f.create();await restarted.initialize();
  assert.equal(restarted.status().ready,true);assert.deepEqual(f.events.at(-1),['start',4280]);
});

test('a failed save on turn-on closes the listener again',async t=>{
  const f=await fixture(t);
  failing(t,['rename']);
  await f.lan.configure(true);
  assert.deepEqual(f.events,[['start',4280],['stop']]);
  const status=f.admin();
  assert.equal(status.enabled,false);assert.equal(status.ready,false);assert.equal(status.error.code,'lan_access_save_failed');
});

test('a port in use leaves LAN access off and unsaved',async t=>{
  for(const [code,error] of [['EADDRINUSE','lan_access_port_in_use'],['EACCES','lan_access_listener_failed']]){
    const f=await fixture(t,{startListener:async()=>{throw Object.assign(new Error('listen'),{code});}});
    await f.lan.configure(true);
    const status=f.admin();
    assert.equal(status.enabled,false);assert.equal(status.ready,false);assert.equal(status.error.code,error);
    assert.equal(status.error.message,error==='lan_access_port_in_use'?'Another program uses this port. Choose another LAN port in Connection options.'
      :'EE could not open its LAN port. Choose another LAN port in Connection options, then try again.');
    await assert.rejects(readFile(f.configPath),{code:'ENOENT'});
  }
});

test('a saved setting that cannot open its port reports the failure and opens on Retry',async t=>{
  let busy=true;
  const f=await fixture(t,{startListener:async()=>{if(busy)throw Object.assign(new Error('listen'),{code:'EADDRINUSE'});}});
  await mkdir(path.dirname(f.configPath),{recursive:true});
  await writeFile(f.configPath,JSON.stringify({version:1,enabled:true,port:4281,devices:[]}),{mode:0o600});
  await f.lan.initialize();
  assert.deepEqual([f.admin().enabled,f.admin().ready,f.admin().error.code],[true,false,'lan_access_port_in_use']);
  busy=false;await f.lan.configure(true);
  assert.deepEqual([f.admin().enabled,f.admin().ready,f.admin().error],[true,true,null]);
});

test('the port changes only while LAN access is off',async t=>{
  const f=await fixture(t);
  await f.lan.configure(false,{port:4281});
  assert.deepEqual(await f.saved(),{version:1,enabled:false,port:4281,devices:[]});
  await f.lan.configure(true);assert.deepEqual(f.events,[['stop'],['start',4281]]);
  await f.lan.configure(true,{port:4282});
  assert.equal(f.admin().error.code,'lan_access_port_change_requires_off');assert.equal(f.admin().port,4281);
  await f.lan.configure(false,{port:4282});
  assert.equal(f.admin().error.code,'lan_access_port_change_requires_off');assert.equal(f.admin().ready,true);
  assert.deepEqual(await f.saved(),{version:1,enabled:true,port:4281,devices:[]});
});

test('turning LAN access off closes the listener, removes every device and the code, and saves',async t=>{
  const f=await enabledWithCode(t);
  assert.equal((await pair(f.lan,{code:f.code})).status,200);
  await f.lan.issueCode();
  await f.lan.configure(false);
  assert.deepEqual(f.events.at(-1),['stop']);
  assert.deepEqual(await f.saved(),{version:1,enabled:false,port:4280,devices:[]});
  assert.deepEqual([f.admin().pairing,f.admin().devices,f.admin().ready],[null,[],false]);
  await f.lan.configure(false);
  assert.deepEqual(await f.saved(),{version:1,enabled:false,port:4280,devices:[]});
});

test('a failed save on turn-off or removal ends with LAN access off and no devices, also after a restart',async t=>{
  for(const action of ['off','remove']){
    const f=await enabledWithCode(t);
    const paired=await pair(f.lan,{code:f.code});
    const restore=failing(t,['rename']);
    if(action==='off')await f.lan.configure(false);
    else await f.lan.revoke(paired.body.device.id);
    restore();
    assert.equal(f.admin().error.code,'lan_access_turned_off',action);
    await assert.rejects(readFile(f.configPath),{code:'ENOENT'});
    assert.deepEqual([f.admin().enabled,f.admin().ready,f.admin().devices],[false,false,[]]);
    assert.equal(f.lan.gate.authorize(request({headers:{cookie:cookieOf(paired)}})).ok,false);
    const restarted=f.create();await restarted.initialize();
    assert.deepEqual([restarted.status({admin:true}).enabled,restarted.status({admin:true}).devices],[false,[]]);
  }
});

test('when the record can be neither saved nor deleted, the status says devices can connect after a restart',async t=>{
  const f=await enabledWithCode(t);
  assert.equal((await pair(f.lan,{code:f.code})).status,200);
  const restore=failing(t,['rename','unlink']);
  await f.lan.configure(false);
  restore();
  assert.equal(f.admin().error.code,'lan_access_revoke_unsaved');
  assert.match(f.admin().error.message,/can connect again after EE restarts/);
  assert.deepEqual([f.admin().enabled,f.admin().ready,f.admin().devices],[false,false,[]]);
  assert.equal((await f.saved()).devices.length,1);
  await f.lan.configure(false);
  assert.equal(f.admin().error,null);assert.deepEqual(await f.saved(),{version:1,enabled:false,port:4280,devices:[]});
});

test('an unreadable, oversized, linked, wrongly owned, unknown-version or extra-key record keeps LAN access closed',async t=>{
  const valid={version:1,enabled:true,port:4280,devices:[{id:'3f9c0a1b2c3d4e5f',name:'Kitchen iPad',client:'browser',key_sha256:'a'.repeat(64),
    paired_at:'2026-11-16T20:10:12.000Z',paired_from:PHONE}]};
  const device=changes=>({...valid,devices:[{...valid.devices[0],...changes}]});
  const records=['not json','',JSON.stringify({...valid,version:0}),JSON.stringify({...valid,version:'2'}),JSON.stringify({...valid,extra:true}),JSON.stringify({...valid,port:80}),
    JSON.stringify(device({extra:1})),JSON.stringify(device({name:' Kitchen'})),JSON.stringify(device({name:'a'.repeat(41)})),
    JSON.stringify(device({client:'tv'})),JSON.stringify(device({key_sha256:'A'.repeat(64)})),JSON.stringify(device({paired_at:'2026-11-16'})),
    JSON.stringify(device({paired_from:'8.8.8.8'})),JSON.stringify(device({id:'3F9C0A1B2C3D4E5F'})),
    JSON.stringify({...valid,devices:[valid.devices[0],{...valid.devices[0],key_sha256:'b'.repeat(64)}]}),' '.repeat(16385)];
  for(const text of records){
    const f=await fixture(t);
    await mkdir(path.dirname(f.configPath),{recursive:true});await writeFile(f.configPath,text,{mode:0o600});
    await f.lan.initialize();
    assert.deepEqual(f.events,[],text.slice(0,40));
    assert.equal(f.admin().error.code,'lan_access_invalid_configuration');assert.equal(f.admin().enabled,false);
    await f.lan.configure(true);
    assert.deepEqual(await f.saved(),{version:1,enabled:true,port:4280,devices:[]});
  }
  const f=await fixture(t);
  await mkdir(path.dirname(f.configPath),{recursive:true});await writeFile(f.configPath,JSON.stringify(valid),{mode:0o600});
  await f.lan.initialize();assert.equal(f.admin().ready,true);assert.equal(f.admin().devices.length,1);
  const linked=await fixture(t),target=path.join(path.dirname(path.dirname(linked.configPath)),'elsewhere.json');
  await writeFile(target,JSON.stringify(valid),{mode:0o600});await mkdir(path.dirname(linked.configPath),{recursive:true});
  try{await symlink(target,linked.configPath);}catch(error){if(error.code==='EPERM'){t.diagnostic('symbolic links need extra rights here');return;}throw error;}
  await linked.lan.initialize();assert.equal(linked.admin().error.code,'lan_access_invalid_configuration');assert.deepEqual(linked.events,[]);
  if(process.platform!=='win32'){
    const shared=await fixture(t);
    await mkdir(path.dirname(shared.configPath),{recursive:true});await writeFile(shared.configPath,JSON.stringify(valid));await chmod(shared.configPath,0o644);
    await shared.lan.initialize();assert.equal(shared.admin().error.code,'lan_access_invalid_configuration');
  }
});

test('a record from a newer release keeps LAN access off and is never written',async t=>{
  const f=await fixture(t),text=JSON.stringify({version:2,enabled:true,port:4280,devices:[],future:true});
  await mkdir(path.dirname(f.configPath),{recursive:true});await writeFile(f.configPath,text,{mode:0o600});
  await f.lan.initialize();
  const newer={code:'lan_access_newer_configuration',message:'The LAN access settings were saved by a newer version of EE, so LAN access is off and its settings stay as they are. Update EE to use them.'};
  assert.deepEqual(f.admin().error,newer);assert.equal(f.admin().enabled,false);
  assert.equal(f.admin().can_configure,false);assert.equal(f.admin().can_change_port,false);
  await f.lan.configure(true);await f.lan.configure(false,{port:4281});
  await assert.rejects(f.lan.issueCode(),{code:'lan_access_off'});
  assert.deepEqual(f.admin().error,newer);assert.deepEqual(f.events,[]);
  assert.equal(await readFile(f.configPath,'utf8'),text);
});

test('a code has six digits, lasts five minutes, works once, and a new code replaces it',async t=>{
  const f=await fixture(t,{random:()=>42});
  await f.lan.configure(true);await f.lan.issueCode();
  assert.deepEqual(f.admin().pairing,{code:'000042',expires_at:new Date(START+PAIRING_CODE_TTL_MS).toISOString(),attempts_left:5,ended:null});
  f.clock.time+=PAIRING_CODE_TTL_MS;
  assert.deepEqual(f.admin().pairing,{code:null,expires_at:new Date(START+PAIRING_CODE_TTL_MS).toISOString(),attempts_left:5,ended:'expired'});
  assert.equal((await pair(f.lan,{code:'000042'})).status,403);
  await f.lan.issueCode();
  assert.equal((await pair(f.lan,{code:'000 042'})).status,200);
  assert.equal(f.admin().pairing.ended,'used');assert.equal(f.admin().pairing.code,null);
  const again=await pair(f.lan,{code:'000-042'});
  assert.equal(again.status,403);assert.equal(again.body.detail.code,'lan_pairing_failed');
  assert.equal(f.admin().devices.length,1);
  let next=0;
  const replaced=await fixture(t,{random:()=>[111111,222222][next++]});
  await replaced.lan.configure(true);await replaced.lan.issueCode();await replaced.lan.issueCode();
  assert.equal((await pair(replaced.lan,{code:'111111'})).status,403);
  assert.equal(replaced.admin().pairing.attempts_left,4);
  assert.equal((await pair(replaced.lan,{code:'222222'})).status,200);
});

test('five wrong codes end the code, and concurrent guesses are counted one at a time',async t=>{
  const f=await enabledWithCode(t);
  const guesses=await Promise.all(Array.from({length:PAIRING_ATTEMPTS+2},()=>pair(f.lan,{code:wrong(f.code)})));
  assert.ok(guesses.every(response=>response.status===403&&response.body.detail.message===
    'The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one.'));
  assert.deepEqual([f.admin().pairing.code,f.admin().pairing.attempts_left,f.admin().pairing.ended],[null,0,'attempts']);
  assert.equal((await pair(f.lan,{code:f.code})).status,403);
  assert.deepEqual(f.admin().devices,[]);
});

test('an invalid body is refused without counting an attempt or touching the code',async t=>{
  const f=await enabledWithCode(t);
  for(const body of ['x','[]','null',JSON.stringify({code:f.code}),JSON.stringify({name:'Kitchen iPad'}),JSON.stringify({code:f.code,name:'Kitchen iPad',extra:1}),
    JSON.stringify({code:f.code,name:'Kitchen iPad',client:'tv'}),JSON.stringify({code:f.code,name:'Kitchen iPad',client:null}),
    JSON.stringify({code:'12345',name:'Kitchen iPad'}),JSON.stringify({code:123456,name:'Kitchen iPad'})]){
    const refused=await pair(f.lan,{body});
    assert.equal(refused.status,400,body);
    assert.deepEqual(refused.body.detail,{code:'invalid',message:'Enter the 6-digit pairing code and a name of up to 40 characters for this device.'});
  }
  const large=await pair(f.lan,{body:JSON.stringify({code:f.code,name:'Kitchen iPad',padding:'a'.repeat(1024)})});
  assert.equal(large.status,413);assert.equal(large.body.detail.code,'too_large');
  assert.deepEqual([f.admin().pairing.code,f.admin().pairing.attempts_left],[f.code,5]);
  assert.equal((await pair(f.lan,{code:f.code})).status,200);
});

test('a correct code with an invalid name is refused before the code is checked; a correct code whose save fails cannot be used again',async t=>{
  const f=await enabledWithCode(t);
  for(const name of ['','   ','a'.repeat(41),'Kitchen\u0000iPad','Kitchen​iPad','Kitchen iPad','Tab\tname',42]){
    assert.equal((await pair(f.lan,{code:f.code,name})).status,400,JSON.stringify(name));
  }
  assert.equal(f.admin().pairing.code,f.code);
  assert.equal(deviceName('  Kitchen iPad  '),'Kitchen iPad');assert.equal(deviceName('a'.repeat(40)),'a'.repeat(40));
  assert.equal(deviceName('😀'.repeat(40)),'😀'.repeat(40));
  assert.equal(pairingCode('482 913'),'482913');assert.equal(pairingCode('482-913'),'482913');assert.equal(pairingCode('4829134'),null);
  const restore=failing(t,['rename']);
  const failed=await pair(f.lan,{code:f.code});
  restore();
  assert.equal(failed.status,500);
  assert.deepEqual(failed.body.detail,{code:'lan_pairing_save_failed',message:'The device could not be saved on the computer running EE. Ask for a new pairing code there and try again.'});
  assert.equal(failed.headers['Set-Cookie'],undefined);
  assert.deepEqual([f.admin().pairing.code,f.admin().pairing.ended,f.admin().devices],[null,'failed',[]]);
  assert.equal((await pair(f.lan,{code:f.code})).status,403);
});

test('the code never reaches the saved record or a status without administration access',async t=>{
  const f=await enabledWithCode(t);
  for(const status of [f.lan.status(),f.lan.status({admin:false})]){
    assert.equal(status.pairing,null);assert.equal(status.devices,null);assert.equal(status.admin_required,true);
    assert.equal(status.can_configure,false);assert.doesNotMatch(JSON.stringify(status),new RegExp(f.code));
  }
  await pair(f.lan,{code:f.code});
  assert.doesNotMatch(await readFile(f.configPath,'utf8'),new RegExp(f.code));
  assert.equal(f.lan.status({admin:true,closing:true}).can_configure,false);
});

test('every pairing answer sets the HttpOnly SameSite=Strict cookie on /api and has no key in the body',async t=>{
  for(const client of [undefined,'browser','app']){
    const f=await enabledWithCode(t);
    const paired=await pair(f.lan,{code:f.code,client,name:'  Kitchen iPad '});
    assert.equal(paired.status,200);
    assert.match(paired.headers['Set-Cookie'],/^ee_lan_4280=[0-9a-f]{64}; Path=\/api; Max-Age=31536000; HttpOnly; SameSite=Strict$/);
    assert.doesNotMatch(paired.headers['Set-Cookie'],/Secure/);
    assert.deepEqual(Object.keys(paired.body),['paired','device']);
    assert.deepEqual(paired.body.device,{id:paired.body.device.id,name:'Kitchen iPad',client:client??'browser',paired_at:'2026-11-16T20:10:12.000Z'});
    assert.match(paired.body.device.id,/^[0-9a-f]{16}$/);
    assert.doesNotMatch(JSON.stringify(paired.body),new RegExp(cookieOf(paired).split('=')[1]));
    assert.equal(paired.headers['Cache-Control'],'no-store');assert.equal(paired.headers['Referrer-Policy'],'no-referrer');
  }
});

test('the saved record holds only a hash of each key and the paired-from address',async t=>{
  const f=await enabledWithCode(t);
  const paired=await pair(f.lan,{code:f.code,remote:`::ffff:${PHONE}`}),key=cookieOf(paired).split('=')[1];
  const [device]=(await f.saved()).devices;
  assert.deepEqual(device,{id:paired.body.device.id,name:'Kitchen iPad',client:'browser',key_sha256:createHash('sha256').update(key).digest('hex'),
    paired_at:'2026-11-16T20:10:12.000Z',paired_from:PHONE});
  assert.doesNotMatch(JSON.stringify(await f.saved()),new RegExp(key));
  const [listed]=f.admin().devices;
  assert.deepEqual(listed,{id:device.id,name:'Kitchen iPad',client:'browser',paired_at:device.paired_at,paired_from:PHONE,last_used_at:null,last_address:null});
  f.clock.time+=60000;
  const other=address(10,0,0,9);
  assert.equal(f.lan.gate.authorize(request({remote:other,headers:{cookie:cookieOf(paired)}})).ok,true);
  assert.deepEqual([f.admin().devices[0].last_used_at,f.admin().devices[0].last_address],[new Date(START+60000).toISOString(),other]);
  assert.equal((await f.saved()).devices[0].last_used_at,undefined);
});

test('no code is issued with 16 paired devices, or while LAN access is off',async t=>{
  const f=await fixture(t);
  await assert.rejects(f.lan.issueCode(),{status:409,code:'lan_access_off',message:'Turn on LAN access before pairing a device.'});
  await f.lan.configure(true);
  for(let index=0;index<16;index++){
    await f.lan.issueCode();
    assert.equal((await pair(f.lan,{code:f.admin().pairing.code,name:`Device ${index}`})).status,200);
  }
  await assert.rejects(f.lan.issueCode(),{status:409,code:'lan_access_device_limit',message:'16 devices are paired, the most EE allows. Remove one before pairing another.'});
  assert.equal(f.admin().devices.length,16);
});

test('removing a device refuses its cookie at once and calls recheck',async t=>{
  const f=await enabledWithCode(t);
  const paired=await pair(f.lan,{code:f.code});
  await f.lan.issueCode();
  const kept=await pair(f.lan,{code:f.admin().pairing.code,name:'Office PC',client:'app'});
  const allowed=f.lan.gate.authorize(request({headers:{cookie:cookieOf(paired)}}));
  assert.deepEqual(allowed,{ok:true,device:true,id:paired.body.device.id});
  await f.lan.revoke(paired.body.device.id);
  assert.deepEqual(f.events.at(-1),['recheck']);
  assert.deepEqual(f.lan.gate.authorize(request({headers:{cookie:cookieOf(paired)}})),{ok:false,pairing:true});
  assert.equal(f.lan.gate.authorize(request({headers:{cookie:cookieOf(kept)}})).ok,true);
  assert.deepEqual((await f.saved()).devices.map(device=>device.name),['Office PC']);
  await assert.rejects(f.lan.revoke(paired.body.device.id),{status:404,code:'not_found',message:'That device is no longer paired.'});
});

test('the Host must name the address the connection arrived on',async t=>{
  const f=await enabledWithCode(t);
  const cookie=cookieOf(await pair(f.lan,{code:f.code}));
  assert.equal(f.lan.gate.authorize(request({headers:{cookie}})).ok,true);
  const denied={ok:false,code:'lan_access_denied',message:'Open EE at the address shown in LAN access on the computer running EE.'};
  for(const host of ['evil.example:4280','mypc.local:4280',`${PC}:4281`,PC,'8.8.8.8:4280',`[::ffff:${PC}]:4280`,`${PHONE}:4280`])
    assert.deepEqual(f.lan.gate.authorize(request({headers:{host,cookie}})),denied,host);
  // An address that is no longer one of this PC's addresses stops matching.
  assert.deepEqual(f.lan.gate.authorize(request({local:PHONE,headers:{host:`${PHONE}:4280`,cookie}})),denied);
});

test('changes and WebSocket upgrades need the matching Origin; cross-site fetch metadata is refused except for page navigation',async t=>{
  const f=await enabledWithCode(t);
  const cookie=cookieOf(await pair(f.lan,{code:f.code}));
  const allowed=(options,websocket=false)=>f.lan.gate.authorize(request({...options,headers:{cookie,...options.headers}}),{websocket}).ok;
  assert.equal(allowed({method:'PUT',url:'/api/scene',headers:{origin:ORIGIN}}),true);
  assert.equal(allowed({method:'PUT',url:'/api/scene'}),false);
  assert.equal(allowed({method:'PUT',url:'/api/scene',headers:{origin:'http://evil.example:4280'}}),false);
  assert.equal(allowed({method:'GET',url:'/api/state',headers:{origin:`https://${PC}:4280`}}),false);
  assert.equal(allowed({url:'/api/ws',headers:{origin:ORIGIN}},true),true);
  assert.equal(allowed({url:'/api/ws'},true),false);
  assert.equal(allowed({url:'/api/state',headers:{'sec-fetch-site':'cross-site','sec-fetch-mode':'no-cors'}}),false);
  assert.equal(allowed({url:'/',headers:{'sec-fetch-site':'cross-site','sec-fetch-mode':'navigate'}}),true);
  assert.equal(allowed({url:'/api/state',headers:{'sec-fetch-site':'same-origin'}}),true);
  assert.equal(allowed({url:'//evil.example/'}),false);
});

test('repeated identity headers and any proxy or Authorization header are refused',async t=>{
  const f=await enabledWithCode(t);
  const cookie=cookieOf(await pair(f.lan,{code:f.code}));
  for(const headers of [{authorization:'Bearer x'},{forwarded:'for=8.8.8.8'},{'x-forwarded-for':'8.8.8.8'},{'x-forwarded-host':'evil.example'},
    {'x-forwarded-proto':'https'},{'x-forwarded-port':'4280'},{'X-Forwarded-Prefix':'/ee'},{Authorization:['Basic x','Basic y']},{origin:[ORIGIN,ORIGIN]},{cookie:[cookie,cookie]},{cookie:`${cookie}; a=${'b'.repeat(4096)}`},{'sec-fetch-site':'none\u0001'}])
    assert.equal(f.lan.gate.authorize(request({headers:{cookie,...headers}})).ok,false,JSON.stringify(headers).slice(0,60));
  const repeatedHost=request({headers:{cookie}});repeatedHost.rawHeaders.push('Host',`${PC}:4280`);
  assert.equal(f.lan.gate.authorize(repeatedHost).ok,false);
});

test('a decoy cookie cannot hide the device cookie',async t=>{
  const f=await enabledWithCode(t);
  const cookie=cookieOf(await pair(f.lan,{code:f.code}));
  assert.equal(f.lan.gate.authorize(request({headers:{cookie:`ee_lan_4280=decoy; theme=dark; ${cookie}`}})).ok,true);
  assert.equal(f.lan.gate.authorize(request({headers:{cookie:cookie.replace('ee_lan_4280','ee_lan_4281')}})).ok,false);
  assert.deepEqual(f.lan.gate.authorize(request({headers:{cookie:'ee_lan_4280=decoy'}})),{ok:false,pairing:true});
  assert.deepEqual(f.lan.gate.authorize(request()),{ok:false,pairing:true});
});

test('connections are accepted only from a private IPv4 address to one of this PC\'s private IPv4 addresses',async t=>{
  const f=await fixture(t);
  const accepts=(localAddress,remoteAddress)=>f.lan.gate.acceptConnection({localAddress,remoteAddress});
  assert.equal(accepts(PC,PHONE),true);assert.equal(accepts(`::ffff:${PC}`,`::ffff:${address(172,16,5,4)}`),true);
  assert.equal(accepts(PC,address(192,168,50,9)),true);
  for(const [local,remote] of [[PC,'100.100.1.1'],[PC,'8.8.8.8'],[PC,'127.0.0.1'],[PC,'169.254.1.1'],[PC,'fd00::1'],[PC,undefined],
    ['127.0.0.1',PHONE],[PHONE,PC],['100.100.1.1',PHONE]])assert.equal(accepts(local,remote),false,`${local} ${remote}`);
});

test('closing EE stops the listener, ends the code and keeps the setting',async t=>{
  const f=await enabledWithCode(t);
  await f.lan.close();
  assert.deepEqual(f.events.at(-1),['stop']);
  assert.deepEqual([f.admin().ready,f.admin().pairing],[false,null]);
  assert.deepEqual(await f.saved(),{version:1,enabled:true,port:4280,devices:[]});
});

test('the gate answers the workspace request with whether this device is paired',async t=>{
  const f=await fixture(t);
  assert.deepEqual(f.lan.gate.workspace({ok:true,device:true}),{live:true,remote:true,lan:true,paired:true});
  assert.deepEqual(f.lan.gate.workspace({ok:false,pairing:true}),{live:true,remote:true,lan:true,paired:false});
  assert.equal(await f.lan.gate.answer(request({method:'GET',url:'/api/lan-access/pair'}),{},'/api/lan-access/pair'),false);
  assert.equal(await f.lan.gate.answer(request({method:'POST',url:'/api/state'}),{},'/api/state'),false);
});
