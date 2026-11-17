import {randomBytes} from 'node:crypto';
import {constants} from 'node:fs';
import {lstat,mkdir,open,rename,unlink} from 'node:fs/promises';
import path from 'node:path';

// The settings files that the gateway and its tools keep in the workspace data folder: private-access.json,
// approved-device.json and lan-access.json.

// A port these settings may hold: 1024 to 65535.
export const validPort=port=>Number.isSafeInteger(port)&&port>=1024&&port<=65535;

// The text of a settings file that only this user can have written: a regular file, not a link, from 1 to maxBytes
// bytes, unchanged between the check and the read, and on Unix owned by this user with no group or other permissions.
// Null when it is not; a file that cannot be read throws, so ENOENT tells a missing file apart.
export async function readOwnedFile(configPath,maxBytes){
  const entry=await lstat(configPath);
  if(!entry.isFile()||entry.size===0||entry.size>maxBytes)return null;
  const flags=constants.O_RDONLY|(constants.O_NOFOLLOW??0)|(constants.O_NONBLOCK??0);
  const file=await open(configPath,flags);
  try{
    const info=await file.stat();
    if(!info.isFile()||info.ino!==entry.ino||info.dev!==entry.dev||info.size===0||info.size>maxBytes)return null;
    if(process.platform!=='win32'&&(info.uid!==process.getuid()||(info.mode&0o077)!==0))return null;
    const buffer=Buffer.alloc(maxBytes+1);
    const {bytesRead}=await file.read(buffer,0,buffer.length,0);
    return bytesRead===info.size?buffer.toString('utf8',0,bytesRead):null;
  }finally{await file.close();}
}

// Replaces the file with text through a new file beside it that only this user can read, so a failure at any point
// leaves either the previous file or the new one. A failed write removes its temporary file and rejects.
export async function writeOwnedFile(configPath,text){
  const temporary=`${configPath}.${randomBytes(8).toString('hex')}.tmp`;
  try{
    await mkdir(path.dirname(configPath),{recursive:true});
    const file=await open(temporary,'wx',0o600);
    try{await file.writeFile(text);await file.sync();}finally{await file.close();}
    await rename(temporary,configPath);
  }catch(error){
    // The original failure is the one reported; a temporary file that was never created or cannot be removed adds
    // nothing to it.
    await unlink(temporary).catch(()=>{});
    throw error;
  }
}
