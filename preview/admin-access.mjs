import {randomBytes,timingSafeEqual} from 'node:crypto';

// Tailscale and LAN access settings and closing the workspace need a per-launch secret. The desktop app that started the gateway
// sends the key in a header. A browser gets the key's stand-in, a cookie, by redeeming a single-use code that the
// desktop app requested or that a source run printed.
export const ADMIN_HEADER='x-ee-admin-key';
export const ADMIN_CODE_PARAM='admin_code';
export const LINK_TTL_MS=120000,MAX_ADMIN_CODES=8;
export const ADMIN_MESSAGES=Object.freeze({
  privateAccess:'To change private access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started.',
  lanAccess:'To change LAN access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started.',
  close:'To close the web session, choose Open in browser in the EE tray menu, or quit EE. If EE runs from source, use the administration link it printed when it started.',
  link:'This request needs the administration key of the EE app that started this workspace.',
  closing:'EE is closing. Open EE again to use the browser workspace.',
});
// The cookie is sent only with requests under the three administration paths. Browsers do not separate cookies by
// port, so another server on 127.0.0.1 still receives it for requests under those paths.
const COOKIE_PATHS=['/api/private-access','/api/lan-access','/api/workspace/close'];
const SECRET=/^[0-9a-f]{64}$/;

function equal(value,expected){
  if(typeof value!=='string')return false;
  const actual=Buffer.from(value),wanted=Buffer.from(expected);
  return actual.length===wanted.length&&timingSafeEqual(actual,wanted);
}

export function createAdminAccess({key,port,now=Date.now,random=()=>randomBytes(32).toString('hex')}){
  if(typeof key!=='string'||!SECRET.test(key))throw new TypeError('The administration key must be 64 lowercase hexadecimal characters.');
  if(!Number.isInteger(port)||port<1||port>65535)throw new TypeError('Choose a local port between 1 and 65535.');
  const cookieName=`ee_admin_${port}`,cookieValue=random(),codes=new Map();
  const allowsHeader=req=>equal(req.headers[ADMIN_HEADER],key);
  // Every entry with this name is compared, so a decoy cookie with a narrower path cannot hide the real one.
  const allowsCookie=req=>String(req.headers.cookie??'').split(';').some(part=>{
    const entry=part.trim(),split=entry.indexOf('=');
    return split>0&&entry.slice(0,split)===cookieName&&equal(entry.slice(split+1),cookieValue);
  });
  function issueCode(ttlMs){
    if(ttlMs!==Infinity&&!(Number.isSafeInteger(ttlMs)&&ttlMs>0))throw new RangeError('Choose a positive code lifetime in milliseconds.');
    const time=now();
    for(const [code,expires] of codes)if(expires<=time)codes.delete(code);
    while(codes.size>=MAX_ADMIN_CODES)codes.delete(codes.keys().next().value);
    const code=random();
    codes.set(code,time+ttlMs);
    return code;
  }
  function redeem(code){
    if(typeof code!=='string'||!SECRET.test(code))return null;
    const expires=codes.get(code);
    if(expires===undefined)return null;
    codes.delete(code);
    if(expires<=now())return null;
    return COOKIE_PATHS.map(path=>`${cookieName}=${cookieValue}; Path=${path}; HttpOnly; SameSite=Strict`);
  }
  return Object.freeze({cookieName,allows:req=>allowsHeader(req)||allowsCookie(req),allowsHeader,issueCode,redeem});
}
