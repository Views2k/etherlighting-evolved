import test from 'node:test';
import assert from 'node:assert/strict';
import {chmod,mkdtemp,rm,symlink,unlink,writeFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {canonicalTailnetAddress,createApprovedDeviceVerifier,matchesApprovedNode,MAX_APPROVED_DEVICES,parseApprovedDevices} from '../preview/approved-device.mjs';

const address='100.100.0.20',ipv6='fd7a:115c:a1e0::20';
const record={version:1,hostStableID:'fixture-host',deviceStableID:'fixture-peer',addresses:[address,ipv6]};
const node={StableID:record.deviceStableID,Tags:['tag:fixture-device'],Addresses:[address+'/32',ipv6+'/128'],MachineAuthorized:true};
const who=JSON.stringify({Node:node});
const firstPeer={deviceStableID:record.deviceStableID,addresses:record.addresses};
const secondPeer={deviceStableID:'fixture-second',addresses:['100.100.0.21','fd7a:115c:a1e0::21']};
const bothPeers={version:2,hostStableID:record.hostStableID,devices:[firstPeer,secondPeer]};

function deferred(){
  let resolve;
  const promise=new Promise(done=>{resolve=done;});
  return {promise,resolve};
}

async function fixture(t,lookup=async()=>who){
  const directory=await mkdtemp(path.join(os.tmpdir(),'ee-test-device-'));
  t.after(()=>rm(directory,{recursive:true,force:true}));
  const configPath=path.join(directory,'approved-device.json');
  const save=value=>writeFile(configPath,typeof value==='string'?value:JSON.stringify(value),{mode:0o600});
  await save(record);
  const queries=[];
  const verify=createApprovedDeviceVerifier({configPath,hostStableID:record.hostStableID,runTailscale:args=>{
    queries.push(args);return lookup(args);
  }});
  return {configPath,queries,save,verify};
}

test('whois must identify the exact authorized tagged device and its individual address',()=>{
  assert.equal(canonicalTailnetAddress('FD7A:115C:A1E0:0000:0000:0000:0000:0020'),ipv6);
  assert.equal(matchesApprovedNode(node,record.deviceStableID,address),true);
  assert.equal(matchesApprovedNode({...node,KeyExpiry:'0001-01-01T00:00:00Z'},record.deviceStableID,ipv6),true);
  assert.equal(matchesApprovedNode({...node,KeyExpiry:'2999-01-01T00:00:00Z'},record.deviceStableID,ipv6),true);
  for(const changes of [
    {StableID:'replacement'},{Tags:[]},{Tags:undefined},{Tags:['tag:']},{MachineAuthorized:false},
    {Expired:true},{Expired:'unknown'},{IsJailed:true},{KeyExpiry:'2000-01-01T00:00:00Z'},
    {KeyExpiry:'unknown'},{KeyExpiry:null},{Addresses:[address+'/24']},{Addresses:[address]},
    {Addresses:['100.100.0.21/32'],AllowedIPs:[address+'/32']}
  ])assert.equal(matchesApprovedNode({...node,...changes},record.deviceStableID,address),false);
  assert.equal(matchesApprovedNode({...node,Addresses:[ipv6+'/64']},record.deviceStableID,ipv6),false);
});

test('a local approval binds the workspace host, device and addresses before invoking whois',async t=>{
  const f=await fixture(t);
  assert.equal(await f.verify(address),true);
  assert.equal(await f.verify(ipv6),true);
  assert.deepEqual(f.queries,[['whois','--json',address],['whois','--json',ipv6]]);
  for(const invalid of ['192.0.2.12','100.100.0.21',ipv6+'%eth0',address+'/32','other.example'])
    assert.equal(await f.verify(invalid),false);
  for(const invalid of [
    null,[],{...record,version:2},{...record,hostStableID:'other-host'},{...record,deviceStableID:record.hostStableID},
    {...record,deviceStableID:''},{...record,addresses:[]},{...record,addresses:[address,'']},
    {...record,addresses:[address,null]},{...record,addresses:[address,'FD7A:115C:A1E0::20']},'{','x'.repeat(4097)
  ]){
    await f.save(invalid);
    assert.equal(await f.verify(address),false);
  }
  await unlink(f.configPath);
  assert.equal(await f.verify(address),false);
  assert.equal(f.queries.length,2);
});

test('a multi-device approval requires unique device IDs and unique canonical addresses across all entries',()=>{
  const parse=devices=>parseApprovedDevices({...bothPeers,devices},record.hostStableID);
  assert.deepEqual(parseApprovedDevices(record,record.hostStableID),[firstPeer]);
  assert.deepEqual(parse([]),[]);
  assert.deepEqual(parse(bothPeers.devices),bothPeers.devices);
  assert.equal(parseApprovedDevices(bothPeers,'different-host'),null);
  for(const invalid of [null,{},[firstPeer,null],[firstPeer,firstPeer],
    [firstPeer,{...secondPeer,deviceStableID:firstPeer.deviceStableID}],
    [firstPeer,{...secondPeer,deviceStableID:record.hostStableID}],
    [firstPeer,{...secondPeer,addresses:[secondPeer.addresses[0],address]}],
    [firstPeer,{...secondPeer,addresses:[ipv6]}],[{...firstPeer,addresses:[address,address]}],
    [firstPeer,{...secondPeer,addresses:[]}],[firstPeer,{...secondPeer,addresses:[null]}]])
    assert.equal(parse(invalid),null);
  const many=Array.from({length:MAX_APPROVED_DEVICES+1},(_,i)=>({deviceStableID:'fixture-'+i,addresses:['100.100.1.'+(i+1)]}));
  assert.equal(parse(many),null);
  assert.deepEqual(parse(many.slice(0,-1)),many.slice(0,-1));
});

test('each device must prove its own tagged identity and revoking one leaves the other working',async t=>{
  let secondNode={...node,StableID:secondPeer.deviceStableID,Addresses:secondPeer.addresses.map((ip,i)=>ip+(i?'/128':'/32'))};
  const f=await fixture(t,async args=>secondPeer.addresses.includes(args.at(-1))?JSON.stringify({Node:secondNode}):who);
  await f.save(bothPeers);
  for(const ip of [...firstPeer.addresses,...secondPeer.addresses])assert.equal(await f.verify(ip),true);
  const validNode=secondNode;
  for(const change of [{StableID:firstPeer.deviceStableID},{Addresses:[address+'/32']},{Tags:[]},{Expired:true}]){
    secondNode={...validNode,...change};
    assert.equal(await f.verify(secondPeer.addresses[0]),false);
  }
  secondNode=validNode;
  await f.save({...bothPeers,devices:[secondPeer]});
  const before=f.queries.length;
  assert.equal(await f.verify(address),false);
  assert.equal(f.queries.length,before);
  assert.equal(await f.verify(secondPeer.addresses[1]),true);
  await f.save({...bothPeers,devices:[]});
  assert.equal(await f.verify(secondPeer.addresses[1]),false);
});

test('changing a different approval during whois does not revoke the retained device',async t=>{
  const entered=deferred(),reply=deferred();
  const f=await fixture(t,async()=>{entered.resolve();return reply.promise;});
  await f.save(bothPeers);
  const pending=f.verify(address);
  await entered.promise;
  await f.save({...bothPeers,devices:[firstPeer]});
  reply.resolve(who);
  assert.equal(await pending,true);
});

test('malformed or unavailable whois output is never retained as authorization',async t=>{
  let output=who;
  const f=await fixture(t,async()=>{
    if(output instanceof Error)throw output;
    return output;
  });
  assert.equal(await f.verify(address),true);
  for(output of [new Error('unavailable'),null,'{','{}','x'.repeat(65537),JSON.stringify({Node:{...node,StableID:'reused-address'}})])
    assert.equal(await f.verify(address),false);
  output=who;
  assert.equal(await f.verify(address),true);
});

test('requests share a running whois for one address and recheck removal during lookup',async t=>{
  const first=deferred(),second=deferred();
  const entered=deferred(),both=deferred();
  let active=0,maximum=0;
  const f=await fixture(t,async args=>{
    if(args.at(-1)===address){
      active++;maximum=Math.max(active,maximum);entered.resolve();
      try{return await first.promise;}finally{active--;}
    }
    both.resolve();return second.promise;
  });
  const pending=f.verify(address);
  await entered.promise;
  const duplicates=Array.from({length:8},()=>f.verify(address));
  const v6=f.verify(ipv6);
  await both.promise;
  await unlink(f.configPath);
  first.resolve(who);second.resolve(who);
  assert.deepEqual(await Promise.all([pending,...duplicates,v6]),Array(10).fill(false));
  assert.equal(maximum,1);
  const completed=f.queries.length;
  await f.save(record);
  assert.equal(await f.verify(address),true);
  assert.equal(f.queries.length,completed+1);
});

test('non-private or redirected local approval files are refused on Unix',{skip:process.platform==='win32'},async t=>{
  const f=await fixture(t);
  await chmod(f.configPath,0o644);
  assert.equal(await f.verify(address),false);
  await unlink(f.configPath);
  const target=f.configPath+'.target';
  await writeFile(target,JSON.stringify(record),{mode:0o600});
  await symlink(target,f.configPath);
  assert.equal(await f.verify(address),false);
  assert.equal(f.queries.length,0);
});
