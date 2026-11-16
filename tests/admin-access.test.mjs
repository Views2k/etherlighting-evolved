import test from 'node:test';
import assert from 'node:assert/strict';
import {createAdminAccess,LINK_TTL_MS,MAX_ADMIN_CODES} from '../preview/admin-access.mjs';

const KEY='a'.repeat(64);
const request=headers=>({headers});

function fixture(){
  let time=1000,count=0;
  const access=createAdminAccess({key:KEY,port:4180,now:()=>time,random:()=>(++count).toString(16).padStart(64,'0')});
  return {access,advance:ms=>{time+=ms;}};
}

test('creation accepts only a 64-character lowercase hex key and a valid port',()=>{
  for(const key of ['a'.repeat(63),'A'.repeat(64),'g'.repeat(64),64,null])
    assert.throws(()=>createAdminAccess({key,port:4180}),TypeError,String(key));
  for(const port of [0,65536,1.5,'4180'])
    assert.throws(()=>createAdminAccess({key:KEY,port}),TypeError,String(port));
});

test('the key header grants access only when it matches exactly',()=>{
  const {access}=fixture();
  assert.equal(access.allows(request({'x-ee-admin-key':KEY})),true);
  assert.equal(access.allowsHeader(request({'x-ee-admin-key':KEY})),true);
  for(const value of [undefined,'b'.repeat(64),KEY.slice(1),KEY+'a',`${KEY}, ${KEY}`])
    assert.equal(access.allows(request({'x-ee-admin-key':value})),false,String(value));
});

test('the cookie grants access under its own name only, and a decoy cannot hide it',()=>{
  const {access}=fixture();
  const [cookie]=access.redeem(access.issueCode(LINK_TTL_MS));
  const value=cookie.split(';')[0].split('=')[1];
  assert.equal(access.allows(request({cookie:`theme=dark; ee_admin_4180=${value}; other=1`})),true);
  assert.equal(access.allows(request({cookie:`ee_admin_4180=bad; ee_admin_4180=${value}`})),true);
  assert.equal(access.allows(request({cookie:`ee_admin_4190=${value}`})),false);
  assert.equal(access.allows(request({cookie:'ee_admin_4180=bad'})),false);
  assert.equal(access.allows(request({cookie:''})),false);
  assert.equal(access.allowsHeader(request({cookie:`ee_admin_4180=${value}`})),false);
  assert.notEqual(value,KEY);
});

test('a code redeems once for two path-limited session cookies',()=>{
  const {access}=fixture(),code=access.issueCode(LINK_TTL_MS);
  assert.match(code,/^[0-9a-f]{64}$/);
  const cookies=access.redeem(code);
  const value=cookies[0].split(';')[0].slice('ee_admin_4180='.length);
  assert.match(value,/^[0-9a-f]{64}$/);
  assert.deepEqual(cookies,[`ee_admin_4180=${value}; Path=/api/private-access; HttpOnly; SameSite=Strict`,
    `ee_admin_4180=${value}; Path=/api/workspace/close; HttpOnly; SameSite=Strict`]);
  assert.doesNotMatch(cookies.join(' '),/Expires|Max-Age/i);
  assert.equal(access.redeem(code),null);
});

test('a desktop code expires after its lifetime and a printed code lasts until it is used',()=>{
  const f=fixture(),short=f.access.issueCode(LINK_TTL_MS),printed=f.access.issueCode(Infinity);
  f.advance(LINK_TTL_MS);
  assert.equal(f.access.redeem(short),null);
  f.advance(86400000);
  assert.notEqual(f.access.redeem(printed),null);
});

test('malformed codes are refused and the oldest code gives way to a ninth',()=>{
  const {access}=fixture();
  for(const code of ['z'.repeat(64),'1'.repeat(63),['1'.repeat(64)],undefined])assert.equal(access.redeem(code),null);
  const codes=Array.from({length:MAX_ADMIN_CODES+1},()=>access.issueCode(LINK_TTL_MS));
  assert.equal(access.redeem(codes[0]),null);
  for(const code of codes.slice(1))assert.notEqual(access.redeem(code),null);
  for(const ttl of [0,-1,1.5])assert.throws(()=>access.issueCode(ttl),RangeError);
});
