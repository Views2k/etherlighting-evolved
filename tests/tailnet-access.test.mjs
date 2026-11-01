import test from 'node:test';
import assert from 'node:assert/strict';
import {createTailnetAccess} from '../preview/tailnet-access.mjs';

const origin='https://fixture.fixture-tailnet.ts.net:8444',ownerLogin='owner@example.invalid';
const gate=createTailnetAccess({origin,ownerLogin});
function request(changes={},extra={}){
  const headers={Host:'fixture.fixture-tailnet.ts.net:8444','X-Forwarded-Host':'fixture.fixture-tailnet.ts.net:8444',
    'X-Forwarded-Proto':'https','X-Forwarded-For':'100.100.0.20','Tailscale-User-Login':ownerLogin,...changes};
  return {method:'GET',url:'/api/state',socket:{remoteAddress:'127.0.0.1'},
    rawHeaders:Object.entries(headers).filter(([,value])=>value!==undefined).flat(),...extra};
}

test('approved Serve owner can navigate and use same-origin HTTP and WebSocket',()=>{
  assert.equal(gate.authorize(request()).ok,true);
  for(const method of ['POST','PUT','PATCH','DELETE'])assert.equal(gate.authorize(request({Origin:origin},{method})).ok,true);
  assert.equal(gate.authorize(request({Origin:origin}),{websocket:true}).ok,true);
});

test('both the transport and exact canonical HTTPS host are required',()=>{
  for(const address of [undefined,'100.100.0.20','192.0.2.20','::ffff:100.100.0.20'])
    assert.equal(gate.authorize(request({},{socket:{remoteAddress:address}})).ok,false);
  for(const change of [{Host:'localhost:4180'},{Host:'other.fixture-tailnet.ts.net:8444'},
    {'X-Forwarded-Host':'other.fixture-tailnet.ts.net:8444'},{'X-Forwarded-Proto':'http'},
    {'X-Forwarded-Proto':undefined},{'X-Forwarded-Host':undefined}])
    assert.equal(gate.authorize(request(change)).ok,false);
});

test('missing, different, tagged, Funnel, and ambiguous identities fail closed',()=>{
  for(const change of [{'Tailscale-User-Login':undefined},{'Tailscale-User-Login':'another@example.invalid'},
    {'Tailscale-Funnel-Request':'?1'},{Authorization:'Bearer forged'},{Forwarded:'for=100.100.0.20'},
    {'X-Forwarded-For':'100.100.0.20, 100.100.0.21'},{'X-Forwarded-For':'192.0.2.1'},
    {'X-Forwarded-For':undefined},{'Tailscale-User-Login':ownerLogin+'\n'}])
    assert.equal(gate.authorize(request(change)).ok,false);
  for(const name of ['HOST','origin','X-Forwarded-For','tailscale-user-login']){
    const req=request({Origin:origin});req.rawHeaders.push(name,name.toLowerCase()==='origin'?origin:ownerLogin);
    assert.equal(gate.authorize(req).ok,false);
  }
});

test('mutations and upgrades require Origin and foreign origins cannot read',()=>{
  for(const method of ['POST','PUT','PATCH','DELETE'])assert.equal(gate.authorize(request({},{method})).ok,false);
  assert.equal(gate.authorize(request(),{websocket:true}).ok,false);
  for(const supplied of ['null','https://foreign.example','http://fixture.fixture-tailnet.ts.net:8444',origin+'/'])
    assert.equal(gate.authorize(request({Origin:supplied})).ok,false);
});

test('Tailscale IPv4 and IPv6 ranges are accepted without accepting nearby addresses',()=>{
  for(const address of ['100.64.0.1','100.127.255.254','fd7a:115c:a1e0::1'])
    assert.equal(gate.authorize(request({'X-Forwarded-For':address})).ok,true);
  for(const address of ['100.63.255.255','100.128.0.1','fd7a:115c:a1e1::1','::1','fd7a:115c:a1e0::1%2'])
    assert.equal(gate.authorize(request({'X-Forwarded-For':address})).ok,false);
});

test('valid encoded owner identity is decoded strictly',()=>{
  const encodedGate=createTailnetAccess({origin,ownerLogin:'fixtur\u00e9@example.invalid'});
  assert.equal(encodedGate.authorize(request({'Tailscale-User-Login':'=?utf-8?q?fixtur=C3=A9@example.invalid?='})).ok,true);
  for(const value of ['=?utf-8?q?fixtur=GG@example.invalid?=','=?utf-8?q?fixtur=FF@example.invalid?=',
    '=?utf-8?b?Zml4dHVyZQ==?='])assert.equal(encodedGate.authorize(request({'Tailscale-User-Login':value})).ok,false);
});

test('configuration cannot broaden the origin or leave the owner implicit',()=>{
  for(const value of [undefined,'http://fixture.fixture-tailnet.ts.net','https://fixture.example',origin+'/',
    origin+'/path',origin+'?q=1','https://user:pass@fixture.fixture-tailnet.ts.net','https://*.fixture-tailnet.ts.net'])
    assert.throws(()=>createTailnetAccess({origin:value,ownerLogin}));
  for(const value of [undefined,'',ownerLogin+' ',['owner']])assert.throws(()=>createTailnetAccess({origin,ownerLogin:value}));
  assert.equal(createTailnetAccess({origin:'https://fixture.fixture-tailnet.ts.net',ownerLogin}).origin,'https://fixture.fixture-tailnet.ts.net');
});

test('authorization never returns an identity and rejects malformed request metadata',()=>{
  assert.deepEqual(gate.authorize(request({'Tailscale-User-Login':'another@example.invalid'})),
    {ok:false,status:403,code:'private_access_denied'});
  for(const extra of [{rawHeaders:[]},{rawHeaders:['Host']},{rawHeaders:undefined},{method:undefined},
    {url:'https://foreign.example/api/state'},{url:'//foreign.example/api/state'}])
    assert.equal(gate.authorize(request({},extra)).ok,false);
});
