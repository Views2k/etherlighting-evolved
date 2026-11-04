import assert from 'node:assert/strict';

export async function checkProductWorkflows({evaluate,until,send,screenshot,url}){
  const check=(expression,label)=>until(()=>evaluate(expression),label);
  const clickText=(selector,text)=>evaluate(`[...document.querySelectorAll(${JSON.stringify(selector)})].find(el=>el.textContent.trim()===${JSON.stringify(text)}).click()`);
  const request=async(path,method='GET',body)=>{const response=await fetch(url+path.replace(/^\//,''),{method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});const value=await response.json();assert.ok(response.ok,JSON.stringify(value));return value;};
  const revision=async()=>(await request('/api/scene')).revision;
  const fill=(selector,value)=>evaluate(`(()=>{const el=document.querySelector(${JSON.stringify(selector)});el.value=${JSON.stringify(value)};el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  const close=()=>evaluate('document.querySelector("dialog[open]").close()');
  async function captureResponsive(name){
    for(const [width,height] of [[390,844],[1280,400]]){
      await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
      assert.ok(await evaluate('document.documentElement.scrollWidth<=innerWidth+1'),'New workflow fits the viewport');
      assert.ok(await evaluate('[...document.querySelectorAll(".saved-library button")].filter(el=>el.getClientRects().length).every(el=>el.scrollWidth<=el.clientWidth+1)'), 'Saved settings button text fits');
      await screenshot(name+'-'+width);
    }
    await send('Emulation.setDeviceMetricsOverride',{width:1366,height:768,deviceScaleFactor:1,mobile:false});
  }
  await check('document.querySelector("#draft-actions").hidden','idle map');
  await evaluate('document.querySelector("#find-port").click()');
  await fill('.port-finder input','Port 3');
  assert.equal(await evaluate('document.querySelectorAll(".port-search-result").length'),1);
  await evaluate('document.querySelector(".port-search-result").click()');
  await check('document.querySelector("[data-port=\\"3\\"]").getAttribute("aria-pressed")==="true"','search selected port');
  await evaluate('document.querySelector("#port-details").click()');
  await check('!!document.querySelector(".port-details-content")','live inspector');
  await request('/api/simulator/links/3','PUT',{up:false});
  await check('document.querySelector(".port-details-content dd").textContent.includes("Disconnected")','open details link refresh');
  await request('/api/simulator/links/3','PUT',{up:true});
  await check('!document.querySelector(".port-details-content dd").textContent.includes("Disconnected")','open details link restore');
  await clickText('.port-details button','Identify for 10 seconds');
  await check('document.querySelector(".port-details-content").textContent.includes("Stop identifying")','host identify began');
  const identifying=await revision();
  await check('!document.querySelector(".port-details-content").textContent.includes("Stop identifying")','host identify expired');
  assert.equal(await revision(),identifying,'Identification does not alter the saved scene');
  await close();
  const selectionBefore=await revision();
  await evaluate('document.querySelector("#saved-selections").click()');
  await fill('.saved-library input','Rack selection');await evaluate('document.querySelector(".library-save").requestSubmit()');
  await check('document.querySelector(".library-list").textContent.includes("Rack selection")','selection saved');
  await clickText('.saved-library button','Select ports');
  assert.equal(await revision(),selectionBefore,'Saving and recalling a selection never writes lighting');
  await evaluate('document.querySelector("[data-port=\\"1\\"]").click();document.querySelector("[data-editor=effect]").click()');
  await clickText('#inspector button','Saved effect settings');
  await captureResponsive('saved-effects');
  await fill('.saved-library input','Quiet rack');await evaluate('document.querySelector(".library-save").requestSubmit()');
  await check('document.querySelector(".library-list").textContent.includes("Quiet rack")','effect recipe saved');
  await clickText('.saved-library button','Preview settings');
  await check('!document.querySelector("#draft-actions").hidden','recipe staged');
  assert.equal(await revision(),selectionBefore,'Recalling a recipe is a local preview');
  await evaluate('document.querySelector("#cancel-draft").click()');
  await evaluate('document.querySelector("[data-page=Automation]").click()');
  await check('!!document.querySelector(".automation-tabs")','automation tabs');
  await clickText('.automation-tabs button','Schedules');
  await check('!!document.querySelector("[name=scheduleName]")','schedule editor');
  await captureResponsive('weekly-schedules');
  await fill('[name=scheduleName]','Weeknight test');
  await fill('[name=scheduleTime]','23:57');
  await evaluate('document.querySelector(".schedule-form").requestSubmit()');
  await check('document.querySelector(".secondary-list").textContent.includes("Weeknight test")','weekly schedule saved');
  await clickText('.automation-tabs button','Rules');
  await check('[...document.querySelectorAll("button")].some(el=>el.textContent==="Test rules")','rule editor');
  const beforeTest=await revision();await clickText('button','Test rules');
  await check('!!document.querySelector(".rule-test-result h3")','rule tester result');
  await captureResponsive('rule-tester');
  assert.equal(await revision(),beforeTest,'Rule testing never applies edits');await clickText('.rule-test-dialog button','Done');
  await evaluate('document.querySelector("[data-page=Lighting]").click()');
  await check('!!document.querySelector("#master")','lighting again');
  const base=await request('/api/scene');
  await fill('#master','81');
  const updated=structuredClone(base.scene);updated.zones[0].name='Shared group name';
  await request('/api/scene','PUT',{scene:updated,expected_revision:base.revision,confirm_output:true});
  await evaluate('document.querySelector("#apply-draft").click()');
  await check('!!document.querySelector(".scene-conflict")','concurrent edit review');
  await clickText('.scene-conflict button','Apply my preview');
  await check('document.querySelector("#draft-actions").hidden','rebased draft applied');
  const applied=await request('/api/scene');assert.equal(applied.scene.brightness,81);assert.equal(applied.scene.zones[0].name,'Shared group name');
  await evaluate('document.querySelector("#scene-history").click()');
  await check('document.querySelectorAll(".scene-history .library-item").length>1','durable history displayed');await close();
  const layouts=[];
  for(const [width,height] of [[390,844],[1280,400],[1366,768]]){
    await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
    await evaluate('document.querySelector("#port-details").click()');
    await check('!!document.querySelector(".port-details-content")','responsive details');
    const fit=await evaluate('(()=>{const el=document.querySelector(".port-details");const r=el.getBoundingClientRect();return {right:r.right,bottom:r.bottom,width:el.scrollWidth,client:el.clientWidth,viewport:innerWidth};})()');
    assert.ok(fit.right<=width+1&&fit.bottom<=height+1&&fit.width<=fit.client+1,'Port inspector fits viewport');
    await screenshot('product-details-'+width);await close();layouts.push({width,height});
  }
  return {search:true,live_details:true,identify_expiry:true,saved_selection:true,effect_recipe:true,schedule:true,rule_test:true,concurrent_edit:true,history:true,layouts};
}
