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

test('requests from another site are refused unless they navigate to a workspace page',()=>{
  for(const site of ['same-origin','none'])assert.equal(gate.authorize(request({'Sec-Fetch-Site':site,'Sec-Fetch-Mode':'cors'})).ok,true);
  for(const site of ['cross-site','same-site'])
    assert.equal(gate.authorize(request({'Sec-Fetch-Site':site,'Sec-Fetch-Mode':'navigate'},{url:'/'})).ok,true);
  for(const [changes,extra] of [
    [{'Sec-Fetch-Site':'cross-site','Sec-Fetch-Mode':'no-cors'},{}],
    [{'Sec-Fetch-Site':'same-site','Sec-Fetch-Mode':'no-cors'},{}],
    [{'Sec-Fetch-Site':'cross-site','Sec-Fetch-Mode':'navigate'},{}],
    [{'Sec-Fetch-Site':'cross-site'},{url:'/'}],
    [{'Sec-Fetch-Site':'cross-site','Sec-Fetch-Mode':'no-cors'},{url:'/preview/app.mjs'}],
    [{'Sec-Fetch-Site':'cross-site','Sec-Fetch-Mode':'navigate',Origin:origin},{method:'POST',url:'/'}],
  ])assert.equal(gate.authorize(request(changes,extra)).ok,false,JSON.stringify([changes,extra]));
  assert.equal(gate.authorize(request({'Sec-Fetch-Site':'cross-site','Sec-Fetch-Mode':'websocket',Origin:origin},{url:'/api/ws'}),{websocket:true}).ok,false);
  const duplicate=request({'Sec-Fetch-Site':'same-origin'});duplicate.rawHeaders.push('Sec-Fetch-Site','same-origin');
  assert.equal(gate.authorize(duplicate).ok,false);
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

test('the accounts audience admits any identified Tailscale account and nothing else',()=>{
  const shared=createTailnetAccess({origin,ownerLogin,audience:'accounts'});
  for(const login of [ownerLogin,'another@example.invalid','=?utf-8?q?fixtur=C3=A9@example.invalid?='])
    assert.equal(shared.authorize(request({'Tailscale-User-Login':login})).ok,true);
  assert.equal(shared.authorize(request({'Tailscale-User-Login':'another@example.invalid',Origin:origin},{method:'PUT'})).ok,true);
  assert.equal(shared.authorize(request({'Tailscale-User-Login':'another@example.invalid',Origin:origin}),{websocket:true}).ok,true);
  for(const change of [{'Tailscale-User-Login':undefined},{'Tailscale-User-Login':''},{'Tailscale-User-Login':'=?utf-8?q?two_words@example.invalid?='},
    {'Tailscale-User-Login':'=?utf-8?q?fixtur=FF@example.invalid?='},{'Tailscale-Funnel-Request':'?1'},{Authorization:'Bearer forged'},
    {'X-Forwarded-For':'192.0.2.1'},{'X-Forwarded-Proto':'http'},{Host:'other.fixture-tailnet.ts.net:8444'}])
    assert.equal(shared.authorize(request({'Tailscale-User-Login':'another@example.invalid',...change})).ok,false);
  assert.equal(shared.authorize(request({'Tailscale-User-Login':'another@example.invalid'},{socket:{remoteAddress:'100.100.0.20'}})).ok,false);
  assert.equal(shared.authorize(request({'Tailscale-User-Login':'another@example.invalid'},{method:'POST'})).ok,false);
});

test('the owner audience is the default and unknown audiences are refused',()=>{
  for(const access of [createTailnetAccess({origin,ownerLogin}),createTailnetAccess({origin,ownerLogin,audience:'owner'})]){
    assert.equal(access.authorize(request()).ok,true);
    assert.equal(access.authorize(request({'Tailscale-User-Login':'another@example.invalid'})).ok,false);
  }
  for(const audience of ['everyone','tailnet','',null,['accounts']])assert.throws(()=>createTailnetAccess({origin,ownerLogin,audience}));
});

test('accountless Serve requests can use the specifically approved device',async()=>{
  const sources=[];
  const access=createTailnetAccess({origin,ownerLogin,verifyDevice:async address=>{
    sources.push(address);
    return address==='100.100.0.20';
  }});
  const anonymous={'Tailscale-User-Login':undefined};
  assert.deepEqual(await access.authorize(request(anonymous)),{ok:true,device:true});
  assert.deepEqual(await access.authorize(request({...anonymous,Origin:origin},{method:'POST'})),{ok:true,device:true});
  assert.deepEqual(await access.authorize(request({...anonymous,Origin:origin}),{websocket:true}),{ok:true,device:true});
  assert.equal((await access.authorize(request({...anonymous,'X-Forwarded-For':'100.100.0.21'}))).ok,false);
  assert.equal(sources.length,4);
  assert.deepEqual(access.authorize(request()),{ok:true});
  assert.equal(sources.length,4);
});

test('invalid requests and unapproved or malformed user logins cannot fall back to a device',()=>{
  let attempts=0;
  const access=createTailnetAccess({origin,ownerLogin,verifyDevice:()=>{attempts++;return true;}});
  const anonymous={'Tailscale-User-Login':undefined};
  for(const values of [
    {'Tailscale-User-Login':'other@example.invalid'},{'Tailscale-User-Login':''},
    {'Tailscale-User-Login':'=?utf-8?q?bad=FF?='},{'Tailscale-User-Login':'bad login'},
    {...anonymous,'Tailscale-Funnel-Request':'?1'},{...anonymous,Host:'localhost'},
    {...anonymous,'X-Forwarded-Host':'other.example'},{...anonymous,'X-Forwarded-Proto':'http'},
    {...anonymous,'X-Forwarded-For':'192.0.2.20'},{...anonymous,'X-Forwarded-For':'100.100.0.20,100.100.0.21'},
    {...anonymous,Authorization:'invalid'},{...anonymous,Forwarded:'invalid'},{...anonymous,Origin:'https://foreign.example'}
  ])assert.equal(access.authorize(request(values)).ok,false);
  for(const extra of [{method:'PUT'},{url:'//foreign.example/'},{socket:{remoteAddress:'100.100.0.20'}}])
    assert.equal(access.authorize(request(anonymous,extra)).ok,false);
  const duplicate=request(anonymous);
  duplicate.rawHeaders.push('X-Forwarded-For','100.100.0.20');
  assert.equal(access.authorize(duplicate).ok,false);
  assert.equal(access.authorize(request(anonymous),{websocket:true}).ok,false);
  assert.equal(attempts,0);
});

test('device lookup errors and unexpected results fail closed',async()=>{
  for(const verifyDevice of [()=>{throw new Error('unavailable');},async()=>{throw new Error('unavailable');},()=>({ok:true}),()=>1]){
    const access=createTailnetAccess({origin,ownerLogin,verifyDevice});
    const result=await access.authorize(request({'Tailscale-User-Login':undefined}));
    assert.deepEqual(result,{ok:false,status:403,code:'private_access_denied'});
  }
});
