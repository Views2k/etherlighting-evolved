import assert from 'node:assert/strict';

export async function checkPortNameWorkflows({evaluate,until,send,screenshot,url}){
  const request=async(method='GET',body)=>{
    const r=await fetch(url+'api/port-names',{method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});
    const value=await r.json();assert.ok(r.ok,JSON.stringify(value));return value;
  };
  const check=(expression,label)=>until(()=>evaluate(expression),label);
  const click=selector=>evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const fill=(id,text)=>evaluate(`(()=>{const input=document.querySelector('[data-port-name="${id}"]');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
  const scene=async()=>(await (await fetch(url+'api/scene')).json());
  const before=await scene(),original=await request(),report={};
  const open=async()=>{
    await click('#port-details');await check('!!document.querySelector(".port-details:modal")','port details');
    await evaluate('[...document.querySelectorAll(".port-details button")].find(b=>b.textContent==="Edit names").click()');
    await check('!!document.querySelector(".port-names:modal")&&!document.querySelector(".port-names button[type=submit]").disabled','port name editor loaded');
  };
  const closeDetails=()=>evaluate('document.querySelector(".port-details").close()');
  const save=async()=>{await evaluate('document.querySelector(".port-names form").requestSubmit()');await check('!document.querySelector(".port-names")','names saved');};
  const label=id=>evaluate(`document.querySelector('[data-port="${id}"] .port-label').textContent`);
  await click('[data-page=Lighting]');await check('!!document.querySelector("#port-details")','Lighting ready');
  await click('#select-all');
  await open();
  assert.equal(await evaluate('document.querySelector("[name=useUnifi]").checked'),true);
  await click('[name=useUnifi]');
  await fill(1,'Desk & dock');await fill(9,'Rack fiber');await save();await closeDetails();
  await until(async()=>await label(1)==='Desk & dock'&&await label(9)==='Rack fiber','saved labels reach map');
  await open();await click('[name=useUnifi]');await save();await closeDetails();
  await until(async()=>await label(1)!=='Desk & dock','automatic labels reach map');
  assert.equal((await request()).names['1'],'Desk & dock');
  await open();await click('[name=useUnifi]');
  assert.equal(await evaluate('document.querySelector("[data-port-name=\\"1\\"]").value'),'Desk & dock');
  await save();await closeDetails();
  const identity=await evaluate('performance.timeOrigin');await send('Page.reload',{ignoreCache:true});
  await until(async()=>{
    try{return await evaluate(`performance.timeOrigin!==${identity}&&document.querySelector('[data-port="1"] .port-label')?.textContent==='Desk & dock'`);}
    catch(e){if(/context|destroyed/i.test(e.message))return false;throw e;}
  },'names persist after reload');
  await click('#select-all');await open();
  await fill(1,'Cancelled name');await click('.port-names .dialog-footer button:nth-child(2)');await closeDetails();
  assert.equal((await request()).names['1'],'Desk & dock');
  await open();await fill(1,'Unsaved draft');
  const current=await request();await request('PUT',{expected_revision:current.revision,use_unifi:false,names:{'1':'Other window'}});
  await evaluate('document.querySelector(".port-names form").requestSubmit()');
  await check('!document.querySelector(".port-names [role=alert]").hidden','conflict shown');
  assert.equal(await evaluate('document.querySelector("[data-port-name=\\"1\\"]").value'),'Unsaved draft');
  await click('.port-names .dialog-footer button:first-child');
  await check('document.querySelector("[data-port-name=\\"1\\"]").value==="Other window"','explicit reload');
  await fill(1,'Retry draft');
  await evaluate(`window.savedNameFetch=window.fetch;window.fetch=(path,options)=>path==='/api/port-names'&&options?.method==='PUT'?Promise.reject(new Error('Fixture disconnect')):window.savedNameFetch(path,options)`);
  await evaluate('document.querySelector(".port-names form").requestSubmit()');
  await check('!document.querySelector(".port-names [role=alert]").hidden','save failure shown');
  assert.equal(await evaluate('document.querySelector("[data-port-name=\\"1\\"]").value'),'Retry draft');
  assert.equal((await request()).names['1'],'Other window');
  await evaluate('window.fetch=window.savedNameFetch;delete window.savedNameFetch');
  await fill(1,'<img src=x onerror=alert(1)>');await save();await closeDetails();
  await until(async()=>await label(1)==='<img src=x onerror=alert(1)>','literal label rendered');
  assert.equal(await evaluate('document.querySelectorAll("[data-port=\\"1\\"] .port-label img").length'),0);
  await open();await fill(1,'Office workstation and docking station');
  for(const [width,height] of [[1366,768],[390,844],[1280,400]]){
    await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
    await evaluate('document.querySelector(".port-names button[type=submit]").scrollIntoView({block:"nearest"})');
    assert.ok(await evaluate('(()=>{const d=document.querySelector(".port-names"),b=d.querySelector("button[type=submit]").getBoundingClientRect();return d.scrollWidth<=d.clientWidth+1&&b.bottom<=innerHeight&&b.top>=0;})()'),'editor fits with reachable save');
    await evaluate('document.querySelector("[data-port-name=\\"10\\"]").scrollIntoView({block:"nearest"})');
    assert.ok(await evaluate('(()=>{const d=document.querySelector(".port-names"),t=d.querySelector("[name=useUnifi]").getBoundingClientRect(),b=d.querySelector("button[type=submit]").getBoundingClientRect();return t.top>=d.getBoundingClientRect().top&&t.bottom<=innerHeight&&b.bottom<=innerHeight;})()'),'naming mode and Save stay visible while scrolling ports');
    await screenshot('port-names-'+width);report[width]={height,save_reachable:true,mode_visible:true};
  }
  await send('Emulation.setDeviceMetricsOverride',{width:1366,height:768,deviceScaleFactor:1,mobile:false});
  await fill(1,'');await save();await closeDetails();
  await until(async()=>await label(1)==='Port 1','cleared name falls back to port number');
  await click('#select-none');await click('[data-port="9"]');
  await check('!!document.querySelector("#sfp-sync")','SFP controls');
  await evaluate('(()=>{const input=document.querySelector("#sfp-sync");input.value="ethernet";input.dispatchEvent(new Event("change",{bubbles:true}));})()');
  await check('!!document.querySelector("#sfp-source")','SFP source selector');
  const follow=await request();
  await request('PUT',{expected_revision:follow.revision,use_unifi:false,names:{'1':'Follow link'}});
  await check('[...document.querySelector("#sfp-source").options].some(o=>o.value==="1"&&o.textContent==="Port 1 · Follow link")','live source label');
  await click('#cancel-draft');
  await evaluate('(()=>{const input=document.querySelector("#sfp-sync");input.value="ethernet";input.dispatchEvent(new Event("change",{bubbles:true}));})()');
  await check('[...document.querySelector("#sfp-source").options].some(o=>o.value==="1"&&o.textContent==="Port 1 · Follow link")','initial source label');
  await click('#cancel-draft');report.sfp_source_labels=true;
  const restore=await request();
  await request('PUT',{expected_revision:restore.revision,use_unifi:original.use_unifi,
    names:{...Object.fromEntries(Object.keys(restore.names).map(id=>[id,''])),...original.names}});
  assert.deepEqual(await scene(),before,'Naming never changes lighting or its revision');
  return {...report,persistence:true,mode_roundtrip:true,cancel:true,conflict:true,failed_save_retry:true,literal_text:true,clear:true};
}
