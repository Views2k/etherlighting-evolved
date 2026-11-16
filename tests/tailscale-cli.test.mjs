import test from 'node:test';
import assert from 'node:assert/strict';
import {createTailscaleRunner,tailscaleActionUrl} from '../preview/tailscale-cli.mjs';

test('installed CLI uses an absolute executable, argument array and bounded hidden execution',async()=>{
  let actual;
  const run=createTailscaleRunner({platform:'win32',env:{ProgramFiles:'C:/Program Files'},execute:(...args)=>{actual=args;args.at(-1)(null,'{}','');}});
  assert.equal(await run(['status','--json']),'{}');assert.match(actual[0],/Program Files[\\/]Tailscale[\\/]tailscale.exe$/);
  assert.deepEqual(actual[1],['status','--json']);assert.equal(actual[2].windowsHide,true);assert.equal(actual[2].timeout,10000);
  assert.equal(actual[2].shell,undefined);
});

test('signed-out status survives a nonzero CLI exit and no raw errors escape',async()=>{
  const status='{"BackendState":"NeedsLogin"}';
  const run=createTailscaleRunner({execute:(_exe,_args,_options,done)=>done(new Error('sensitive'),status,'sensitive')});
  assert.equal(await run(['status','--json']),status);
  await assert.rejects(run(['serve','status','--json']),error=>!error.message.includes('sensitive'));
});

test('provider auth and HTTPS approval links survive timeout without leaking other output',async()=>{
  for(const url of ['https://login.tailscale.com/a/fixture','https://login.tailscale.com/f/serve?node=fixture']){
    const run=createTailscaleRunner({execute:(_exe,_args,_options,done)=>done(new Error('sensitive'),'private data',`Open ${url}\nprivate data`)});
    await assert.rejects(run(['login','--timeout=5s']),error=>error.actionUrl===url&&!error.message.includes('private data'));
  }
});

test('setup links reject deceptive origins, credentials, fragments and unexpected paths',()=>{
  for(const url of ['http://login.tailscale.com/a/x','https://login.tailscale.com.evil.invalid/a/x',
    'https://user@login.tailscale.com/a/x','https://login.tailscale.com:8444/a/x','https://login.tailscale.com/a/x#x',
    'https://login.tailscale.com/admin/dns','https://evil.invalid','javascript:alert(1)'])assert.equal(tailscaleActionUrl(url),null,url);
});

test('missing executable and permission failures produce distinct actionable codes',async()=>{
  for(const [code,expected] of [['ENOENT','tailscale_not_installed'],['EACCES','tailscale_permission_required']]){
    const run=createTailscaleRunner({execute:(_exe,_args,_options,done)=>done(Object.assign(new Error('sensitive'),{code}),'','')});
    await assert.rejects(run(['status','--json']),error=>error.code===expected);
  }
});

test('output beyond the buffer has its own code and callers can raise the limit',async()=>{
  const sizes=[],stdout=JSON.stringify({BackendState:'Running',Peer:{fixture:{DNSName:'x'.repeat(70000)}}});
  const execute=(_exe,_args,options,done)=>{
    sizes.push(options.maxBuffer);
    if(stdout.length>options.maxBuffer)done(Object.assign(new Error('sensitive'),{code:'ERR_CHILD_PROCESS_STDIO_MAXBUFFER'}),stdout.slice(0,options.maxBuffer),'');
    else done(null,stdout,'');
  };
  await assert.rejects(createTailscaleRunner({execute})(['status','--json']),error=>error.code==='tailscale_output_too_large'&&!error.message.includes('sensitive'));
  assert.equal(await createTailscaleRunner({execute,maxBuffer:4*1024*1024})(['status','--json']),stdout);
  assert.deepEqual(sizes,[65536,4194304]);
});
