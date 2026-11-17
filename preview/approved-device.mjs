import {isIP} from 'node:net';
import {readOwnedFile} from './settings-file.mjs';

const MAX_RECORD_BYTES=4096;
const MAX_WHOIS_BYTES=65536;
export const MAX_APPROVED_DEVICES=16;

export function canonicalTailnetAddress(value){
  if(typeof value!=='string'||value.length>45||value.includes('%'))return null;
  switch(isIP(value)){
    case 4: {
      const [first,second]=value.split('.').map(Number);
      return first===100&&second>=64&&second<=127?value:null;
    }
    case 6: {
      const address=new URL(`http://[${value}]/`).hostname.slice(1,-1);
      return address.startsWith('fd7a:115c:a1e0:')?address:null;
    }
    default:return null;
  }
}

function validId(value){
  return typeof value==='string'&&/^[!-~]{1,256}$/.test(value);
}

export function parseApprovedDevices(record,hostStableID){
  if(!validId(hostStableID)||record?.hostStableID!==hostStableID)return null;
  const entries=record.version===1?[record]:record.version===2?record.devices:null;
  if(!Array.isArray(entries)||entries.length>MAX_APPROVED_DEVICES)return null;
  const ids=new Set([hostStableID]),addresses=new Set(),devices=[];
  for(const entry of entries){
    if(!validId(entry?.deviceStableID)||ids.has(entry.deviceStableID)
      ||!Array.isArray(entry.addresses)||!entry.addresses.length)return null;
    ids.add(entry.deviceStableID);
    for(const address of entry.addresses){
      if(typeof address!=='string'||canonicalTailnetAddress(address)!==address||addresses.has(address))return null;
      addresses.add(address);
    }
    devices.push({deviceStableID:entry.deviceStableID,addresses:[...entry.addresses]});
  }
  return devices;
}

export function matchesApprovedNode(node,expectedStableID,sourceIp){
  const address=canonicalTailnetAddress(sourceIp);
  if(!address||!validId(expectedStableID)||node?.StableID!==expectedStableID)return false;
  if(!Array.isArray(node.Tags)||!node.Tags.length
    ||!node.Tags.every(tag=>typeof tag==='string'&&/^tag:[a-z][a-z0-9-]*$/i.test(tag)))return false;
  if(node.MachineAuthorized!==undefined&&node.MachineAuthorized!==true)return false;
  if((node.IsJailed!==undefined&&node.IsJailed!==false)||(node.Expired!==undefined&&node.Expired!==false))return false;
  if(node.KeyExpiry!==undefined){
    if(typeof node.KeyExpiry!=='string'||!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?(?:Z|[+-]\d\d:\d\d)$/.test(node.KeyExpiry))return false;
    const never=/^0001-01-01T00:00:00(?:\.0+)?Z$/.test(node.KeyExpiry);
    if(!never&&!(Date.parse(node.KeyExpiry)>Date.now()))return false;
  }
  const suffix=isIP(address)===4?'/32':'/128';
  return Array.isArray(node.Addresses)&&node.Addresses.some(prefix=>typeof prefix==='string'
    &&prefix.endsWith(suffix)&&canonicalTailnetAddress(prefix.slice(0,-suffix.length))===address);
}

async function approvedPeer(configPath,hostStableID,sourceIp){
  const text=await readOwnedFile(configPath,MAX_RECORD_BYTES);
  if(text===null)return null;
  const devices=parseApprovedDevices(JSON.parse(text),hostStableID);
  return devices?.find(device=>device.addresses.includes(sourceIp))?.deviceStableID??null;
}

export function createApprovedDeviceVerifier({configPath,hostStableID,runTailscale}){
  const pending=new Map();
  async function lookup(address){
    // The installed CLI runner bounds total execution time and captured output.
    const output=await runTailscale(['whois','--json',address]);
    if(typeof output!=='string'||Buffer.byteLength(output)>MAX_WHOIS_BYTES)return null;
    return JSON.parse(output)?.Node;
  }
  return async function verifyDevice(sourceIp){
    const address=canonicalTailnetAddress(sourceIp);
    if(!address||!validId(hostStableID))return false;
    try{
      const peer=await approvedPeer(configPath,hostStableID,address);
      if(!peer)return false;
      let lookupPromise=pending.get(address);
      if(!lookupPromise){
        if(pending.size>=4)return false;
        lookupPromise=lookup(address).finally(()=>pending.delete(address));
        pending.set(address,lookupPromise);
      }
      if(!matchesApprovedNode(await lookupPromise,peer,address))return false;
      // Removal or replacement while the lookup was running also revokes this request.
      return await approvedPeer(configPath,hostStableID,address)===peer;
    }catch{return false;}
  };
}
