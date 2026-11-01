import test from 'node:test';
import assert from 'node:assert/strict';
import {renderAutomation,renderProfiles} from '../preview/secondary.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

function uiDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement;
  const matches=(element,selector)=>selector.split(',').some(part=>{
    const match=part.trim().match(/^([a-z]+)?(?:\[([\w-]+)(?:="([^"]*)")?\])?(?::(checked))?$/);if(!match)return false;
    const [,tag,key,value,checked]=match;
    const actual=key?.startsWith('data-')?element.dataset[key.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]:element[key]??element.getAttribute(key);
    return (!tag||element.tagName===tag)&&(!key||value===undefined?(!key||actual!=null):String(actual)===value)&&(!checked||element.checked);
  });
  dom.doc.createElement=tag=>{
    const element=create(tag);element.style.setProperty=(key,value)=>{element.style[key]=value;};
    element.removeAttribute=key=>element.attributes.delete(key);element.matches=selector=>matches(element,selector);
    element.querySelectorAll=selector=>dom.findAll(element,node=>node!==element&&matches(node,selector));element.querySelector=selector=>element.querySelectorAll(selector)[0]||null;
    element.closest=selector=>{let node=element;while(node){if(matches(node,selector))return node;node=node.parentNode;}return null;};
    element.reportValidity=()=>true;element.select=()=>{};
    element.showModal=()=>{element.open=true;};element.close=()=>{element.open=false;};
    element.remove=()=>{if(element.parentNode){const siblings=element.parentNode.children;siblings.splice(siblings.indexOf(element),1);element.parentNode=null;}};
    Object.defineProperty(element,'firstElementChild',{get:()=>element.children[0]});
    if(tag==='form')Object.defineProperty(element,'elements',{get:()=>Object.fromEntries(element.querySelectorAll('input,select,button').filter(node=>node.name).map(node=>[node.name,node]))});
    return element;
  };
  dom.host.querySelectorAll=selector=>dom.findAll(dom.host,node=>node!==dom.host&&matches(node,selector));dom.host.querySelector=selector=>dom.host.querySelectorAll(selector)[0]||null;
  dom.host.removeAttribute=key=>dom.host.attributes.delete(key);
  return {...dom,button:text=>dom.findAll(dom.doc.body,node=>node.tagName==='button'&&node.textContent===text)[0],form:()=>dom.findAll(dom.host,node=>node.tagName==='form')[0]};
}
const flush=()=>new Promise(resolve=>setImmediate(resolve));
const rule=(id='alert',changes={})=>({id,name:'Alert',enabled:true,match:'all',ports:[1],conditions:[{field:'anomaly',op:'is',value:true}],look:{style:'pulse',color:'#123456',rate:.123456789,brightness:37},...changes});
const scene=rules=>({version:2,zones:[{id:'all',name:'All ports',ports:[1,2],effect:{id:'static',params:{color:'#FFFFFF'}}}],rules,brightness:80,overrides:{2:{color:'#654321',brightness:50}},sfp:{},sfp_sync:{mode:'independent',source_port:1}});
const live=scene=>({...scene,num_ports:2,ports:[{port:1},{port:2}],sfp:[],revision:0});

async function automation(context,rules=[rule()],reject=false,{readScene,writeScene}={}){
  const dom=uiDom(context),sent=[],loaded={scene:scene(rules),revision:'loaded-revision'},state=live(loaded.scene);
  const request=async(path,method,body)=>{
    sent.push({path,method,body:structuredClone(body)});
    if(!method){if(path==='/api/rules/fields')return {fields:[{key:'anomaly',label:'Anomaly',type:'bool',operators:['is','is_not']}]};if(path==='/api/rules/templates')return [];if(path==='/api/scene')return readScene?readScene(loaded):structuredClone(loaded);throw new Error(`Unexpected read ${path}`);}
    assert.equal(path,'/api/scene');assert.equal(method,'PUT');if(reject||body.expected_revision!==loaded.revision)throw new Error('The lighting setup changed. Refresh the rules.');
    if(writeScene)return writeScene(loaded,body);
    return {scene:structuredClone(body.scene),revision:'saved-revision'};
  };
  const cleanup=await renderAutomation(dom.host,{request,state,getState:()=>state,onChange:async()=>{}});dom.after(cleanup);await flush();return {...dom,sent,loaded,state,cleanup};
}

function externalScene(ui,next,revision='external-revision'){
  ui.loaded.scene=structuredClone(next);ui.loaded.revision=revision;const nextRevision=ui.state.revision+1;Object.assign(ui.state,live(next),{revision:nextRevision});ui.cleanup.update(ui.state);
}

function loseFirstAcceptedReply() {
  let writes = 0;
  return async (loaded,body) => {
    loaded.scene = structuredClone(body.scene); loaded.revision = `accepted-${++writes}`;
    if (writes === 1) throw Object.assign(new Error('The reply was lost.'), {uncertain:true});
    return structuredClone(loaded);
  };
}

test('schedule help follows the host clock even while a remote user is editing',async context=>{
  const ui=await automation(context,[rule('clock',{conditions:[{field:'time',op:'between',value:['22:00','07:00']} ]})]);
  ui.state.schedule_clock={local_iso:'2026-10-31T22:00:00-05:00',zone_name:'Central Daylight Time'};
  ui.cleanup.update(ui.state);await flush();
  const hint=ui.byClass(ui.host,'help-text').find(item=>item.textContent.startsWith('Schedule time:'));
  assert.match(hint.textContent,/Central Daylight Time · EE computer/);
  assert.doesNotMatch(hint.textContent,/this computer/);
  ui.form().elements.ruleName.value='Unapplied local draft';ui.form().listeners.get('input')();
  ui.state.schedule_clock={local_iso:'2026-11-01T01:00:00-06:00',zone_name:'Central Standard Time'};
  ui.cleanup.update(ui.state);await flush();
  assert.match(hint.textContent,/Central Standard Time/);
  assert.equal(ui.form().elements.ruleName.value,'Unapplied local draft');assert.equal(ui.cleanup.hasChanges(),true);
  assert.equal(ui.sent.some(item=>item.method),false);
});

test('an accepted rule save with a lost reply reconciles without losing the error or duplicating edits',async context=>{
  const ui=await automation(context,[rule()],false,{writeScene:loseFirstAcceptedReply()});
  ui.form().elements.ruleName.value='Saved despite the lost reply';ui.form().listeners.get('input')();
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.cleanup.hasChanges(),false);
  assert.equal(ui.form().elements.ruleName.value,'Saved despite the lost reply');
  assert.match(ui.byClass(ui.host,'notice')[0].textContent,/saved.*not confirmed/);
  ui.form().elements.ruleName.value='Next edit';ui.form().listeners.get('input')();
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  const writes=ui.sent.filter(item=>item.method);
  assert.equal(writes.length,2);assert.equal(writes[1].body.expected_revision,'accepted-1');
  assert.equal(ui.loaded.scene.rules.length,1);assert.equal(ui.loaded.scene.rules[0].name,'Next edit');
});

test('an accepted priority save reconciles its baseline while preserving a separate rule draft',async context=>{
  const ui=await automation(context,[rule('first'),rule('second')],false,{writeScene:loseFirstAcceptedReply()});
  ui.form().elements.ruleName.value='Unapplied name';ui.form().listeners.get('input')();
  ui.button('Move down').click();await flush();ui.button('Apply priority').click();await flush();
  assert.deepEqual(ui.loaded.scene.rules.map(item=>item.id),['second','first']);
  assert.equal(ui.button('Apply priority'),undefined);
  assert.equal(ui.form().elements.ruleName.value,'Unapplied name');assert.equal(ui.cleanup.hasChanges(),true);
  assert.match(ui.byClass(ui.host,'notice')[0].textContent,/not confirmed/);
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.sent.filter(item=>item.method)[1].body.expected_revision,'accepted-1');
  assert.equal(ui.loaded.scene.rules.find(item=>item.id==='first').name,'Unapplied name');
});

test('an accepted deletion with a lost reply removes the deleted editor and retains the new baseline',async context=>{
  const ui=await automation(context,[rule('first'),rule('second')],false,{writeScene:loseFirstAcceptedReply()});
  ui.button('Delete rule').click();await flush();ui.button('Delete').click();await flush();
  assert.deepEqual(ui.loaded.scene.rules.map(item=>item.id),['second']);
  assert.equal(ui.cleanup.hasChanges(),false);assert.match(ui.byClass(ui.host,'notice')[0].textContent,/not confirmed/);
  ui.form().elements.ruleName.value='Remaining rule';ui.form().listeners.get('input')();
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.sent.filter(item=>item.method)[1].body.expected_revision,'accepted-1');
  assert.deepEqual(ui.loaded.scene.rules.map(item=>item.id),['second']);
});

test('uncertain unmatched or unreadable readback keeps the draft and original revision guard',async context=>{
  let reads=0;
  const ui=await automation(context,[rule()],false,{
    readScene:loaded=>{if(++reads>1)throw new Error('Read unavailable');return structuredClone(loaded);},
    writeScene:async()=>{throw Object.assign(new Error('Reply unavailable'),{uncertain:true});}
  });
  ui.form().elements.ruleName.value='Keep this draft';ui.form().listeners.get('input')();
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.cleanup.hasChanges(),true);assert.equal(ui.form().elements.ruleName.value,'Keep this draft');
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.deepEqual(ui.sent.filter(item=>item.method).map(item=>item.body.expected_revision),['loaded-revision','loaded-revision']);
  assert.equal(ui.loaded.scene.rules[0].name,'Alert');
});

test('clean rules adopt an external lighting baseline without repainting focused controls or writing it back',async context=>{
  const ui=await automation(context),form=ui.form(),name=form.elements.ruleName;name.focus();
  externalScene(ui,{...ui.loaded.scene,brightness:23});await flush();
  assert.equal(ui.form(),form);assert.equal(ui.doc.activeElement,name);assert.equal(ui.cleanup.hasChanges(),false);
  ui.cleanup.update(ui.state);ui.cleanup.update(ui.state);await flush();
  assert.equal(ui.sent.filter(item=>item.path==='/api/scene'&&!item.method).length,2);assert.equal(ui.sent.some(item=>item.method),false);
  name.value='After the external change';form.listeners.get('input')();form.listeners.get('submit')({preventDefault(){}});await flush();
  const write=ui.sent.find(item=>item.method);assert.equal(write.body.expected_revision,'external-revision');assert.equal(write.body.scene.brightness,23);
});

test('external rule changes update a clean selected rule and preserve its keyboard focus',async context=>{
  const ui=await automation(context);ui.form().elements.ruleName.focus();
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Changed elsewhere'})]});await flush();
  assert.equal(ui.form().elements.ruleName.value,'Changed elsewhere');assert.equal(ui.doc.activeElement,ui.form().elements.ruleName);
  assert.equal(ui.cleanup.hasChanges(),false);assert.equal(ui.sent.some(item=>item.method),false);
});

test('external changes preserve dirty rule and priority drafts with their original revision guard',async context=>{
  const ui=await automation(context,[rule('first'),rule('second')]);
  ui.form().elements.ruleName.value='Keep my draft';ui.form().listeners.get('input')();ui.button('Move down').click();await flush();
  externalScene(ui,{...ui.loaded.scene,brightness:23});await flush();
  assert.equal(ui.form().elements.ruleName.value,'Keep my draft');assert.ok(ui.button('Apply priority'));
  assert.equal(ui.sent.filter(item=>item.path==='/api/scene'&&!item.method).length,1);
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  const write=ui.sent.find(item=>item.method);assert.equal(write.body.expected_revision,'loaded-revision');assert.equal(write.body.scene.brightness,80);
  assert.equal(ui.form().elements.ruleName.value,'Keep my draft');assert.equal(ui.cleanup.hasChanges(),true);
  assert.match(ui.byClass(ui.host,'notice')[0].textContent,/Refresh/);
});

test('an unapplied priority change alone prevents automatic rebasing',async context=>{
  const ui=await automation(context,[rule('first'),rule('second')]);ui.button('Move down').click();await flush();
  externalScene(ui,{...ui.loaded.scene,brightness:23});await flush();
  assert.equal(ui.sent.filter(item=>item.path==='/api/scene'&&!item.method).length,1);assert.ok(ui.button('Apply priority'));
  ui.button('Apply priority').click();await flush();
  assert.equal(ui.sent.find(item=>item.method).body.expected_revision,'loaded-revision');assert.equal(ui.cleanup.hasChanges(),true);
});

test('a delayed baseline read cannot overwrite a draft entered while it was pending',async context=>{
  let reads=0,finish;const ui=await automation(context,[rule()],false,{readScene:loaded=>++reads===1?structuredClone(loaded):new Promise(resolve=>{finish=()=>resolve(structuredClone(loaded));})});
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Elsewhere'})]});
  ui.form().elements.ruleName.value='Typing now';ui.form().listeners.get('input')();finish();await flush();
  assert.equal(ui.form().elements.ruleName.value,'Typing now');assert.equal(reads,2);
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.sent.find(item=>item.method).body.expected_revision,'loaded-revision');assert.equal(ui.cleanup.hasChanges(),true);
  ui.button('Cancel rule changes').click();await flush();ui.cleanup.update(ui.state);finish();await flush();
  assert.equal(reads,3);assert.equal(ui.form().elements.ruleName.value,'Elsewhere');assert.equal(ui.cleanup.hasChanges(),false);
});

test('a completed explicit refresh supersedes an older background response',async context=>{
  let reads=0,finish;const ui=await automation(context,[rule()],false,{readScene:loaded=>{reads++;const result=structuredClone(loaded);return reads===2?new Promise(resolve=>{finish=()=>resolve(result);}):result;}});
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Older'})]},'older');
  ui.loaded.scene.rules[0].name='Explicit refresh';ui.loaded.revision='fresh';ui.button('Refresh rules').click();await flush();
  assert.equal(ui.form().elements.ruleName.value,'Explicit refresh');finish();await flush();assert.equal(ui.form().elements.ruleName.value,'Explicit refresh');
  ui.form().elements.ruleName.value='My revision';ui.form().listeners.get('input')();ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.sent.find(item=>item.method).body.expected_revision,'fresh');assert.equal(reads,3);
});

test('newer scene notifications coalesce behind an in-flight read and stale responses never repaint rules',async context=>{
  let reads=0,finish;const ui=await automation(context,[rule()],false,{readScene:loaded=>{reads++;const result=structuredClone(loaded);return reads===2?new Promise(resolve=>{finish=()=>resolve(result);}):result;}});
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Intermediate'})]},'intermediate');
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Newest'})]},'newest');assert.equal(reads,2);
  finish();await flush();assert.equal(reads,3);assert.equal(ui.form().elements.ruleName.value,'Newest');
  assert.equal(ui.sent.some(item=>item.method),false);assert.equal(ui.cleanup.hasChanges(),false);
});

test('failed background reads do not loop and disposed rules ignore a delayed response',async context=>{
  let reads=0,finish;const ui=await automation(context,[rule()],false,{readScene:loaded=>{
    if(++reads===1)return structuredClone(loaded);if(reads===2)throw new Error('Unavailable');
    return new Promise(resolve=>{finish=()=>resolve(structuredClone(loaded));});
  }});
  externalScene(ui,{...ui.loaded.scene,brightness:23});await flush();ui.cleanup.update(ui.state);await flush();assert.equal(reads,2);
  assert.match(ui.byClass(ui.host,'notice')[0].textContent,/Refresh rules/);
  externalScene(ui,{...ui.loaded.scene,rules:[rule('alert',{name:'Too late'})]},'later');const form=ui.form();ui.cleanup();finish();await flush();
  assert.equal(ui.form(),form);assert.equal(form.elements.ruleName.value,'Alert');assert.equal(reads,3);
});

test('rule Apply uses its loaded scene revision, preserves unrelated data and does not round an untouched cycle',async context=>{
  const ui=await automation(context),form=ui.form();assert.ok(form);
  const brightness=form.elements.brightness;assert.equal(brightness.style['--range-progress'],'37%');assert.equal(brightness.getAttribute('aria-valuetext'),'37 percent');
  form.elements.ruleName.value='Renamed alert';form.listeners.get('input')();form.listeners.get('submit')({preventDefault(){}});await flush();
  const writes=ui.sent.filter(item=>item.method);assert.equal(writes.length,1);assert.equal(writes[0].path,'/api/scene');
  const result=writes[0].body;assert.equal(result.preserve_unchanged,true);assert.equal(result.confirm_output,true);assert.equal(result.scene.rules[0].look.rate,.123456789);
  assert.deepEqual({...result.scene,rules:ui.loaded.scene.rules},ui.loaded.scene);assert.equal(result.scene.rules[0].name,'Renamed alert');
  assert.equal(ui.sent.filter(item=>item.path==='/api/scene'&&!item.method).length,1);
});

test('a new rule remains selected and editable after the backend enforces its ID limit',async context=>{
  const ui=await automation(context,[],false,{writeScene:(loaded,body)=>{
    loaded.scene=structuredClone(body.scene);
    loaded.scene.rules=loaded.scene.rules.map(rule=>({...rule,id:rule.id.slice(0,32)}));
    return structuredClone(loaded);
  }});
  ui.button('New rule').click();await flush();
  ui.form().elements.ruleName.value='New alert';ui.button('Add condition').click();
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.ok(ui.sent.find(item=>item.method).body.scene.rules[0].id.length<=32);
  assert.equal(ui.form().elements.ruleName.value,'New alert');
  ui.form().elements.ruleName.value='Unapplied';ui.form().listeners.get('input')();
  ui.button('Cancel rule changes').click();assert.equal(ui.form().elements.ruleName.value,'New alert');
  ui.form().elements.ruleName.value='Updated alert';ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.loaded.scene.rules.length,1);assert.equal(ui.loaded.scene.rules[0].name,'Updated alert');
});

test('the last rule condition can be removed and Apply remains blocked until a replacement exists',async context=>{
  const ui=await automation(context);
  const remove=ui.button('Remove');assert.equal(Boolean(remove.disabled),false);remove.focus();remove.click();await flush();
  assert.equal(ui.byClass(ui.host,'condition-row').length,0);assert.equal(ui.button('Apply rule').disabled,true);
  assert.equal(ui.doc.activeElement===ui.button('Add condition'),true,'focus moves to Add condition after removing the last condition');
  assert.equal(ui.sent.some(item=>item.method),false);
  ui.button('Add condition').click();assert.equal(ui.byClass(ui.host,'condition-row').length,1);assert.equal(ui.button('Apply rule').disabled,false);
});

test('unsupported saved condition renders a replacement control and blocks Apply until repaired',async context=>{
  const ui=await automation(context,[rule('old',{conditions:[{field:'retired_field',op:'is',value:true}]})]);
  assert.equal(ui.button('Apply rule').disabled,true);
  const replacement=ui.findAll(ui.host,node=>node.getAttribute('aria-label')==='Replace unavailable condition')[0];assert.ok(replacement);
  replacement.value='anomaly';replacement.listeners.get('change')();assert.equal(ui.button('Apply rule').disabled,false);
  ui.form().listeners.get('submit')({preventDefault(){}});await flush();
  assert.equal(ui.sent.find(item=>item.method)?.body.scene.rules[0].conditions[0].field,'anomaly');
});

test('stale rule Apply retains the editable draft and never retries through an unversioned route',async context=>{
  const ui=await automation(context,[rule()],true),form=ui.form();form.elements.ruleName.value='Keep this draft';form.listeners.get('input')();
  form.listeners.get('submit')({preventDefault(){}});await flush();assert.equal(ui.form().elements.ruleName.value,'Keep this draft');
  assert.ok(ui.byClass(ui.host,'notice')[0].textContent.includes('Refresh'));assert.equal(ui.byClass(ui.host,'notice')[0].hidden,false);
  assert.deepEqual(ui.sent.filter(item=>item.method).map(item=>[item.path,item.body.expected_revision]),[['/api/scene','loaded-revision']]);
});

test('priority Apply uses the loaded full scene and preserves rule content',async context=>{
  const ui=await automation(context,[rule('first'),rule('second',{name:'Second'})]);ui.button('Move down').click();await flush();
  assert.ok(ui.button('Apply rule and priority'));
  ui.button('Apply priority').click();await flush();const write=ui.sent.find(item=>item.method);
  assert.deepEqual(write.body.scene.rules.map(item=>item.id),['second','first']);assert.deepEqual({...write.body.scene,rules:ui.loaded.scene.rules},ui.loaded.scene);
});

test('deleting a rule does not apply a separate pending priority edit',async context=>{
  const ui=await automation(context,[rule('first'),rule('second',{name:'Second'}),rule('third',{name:'Third'})]);
  ui.button('Move down').click();await flush();
  const third=ui.byClass(ui.host,'rule-item').find(item=>ui.byClass(item,'item-name')[0].textContent.includes('Third'));
  third.click();await flush();ui.button('Delete rule').click();await flush();ui.button('Delete').click();await flush();
  const write=ui.sent.find(item=>item.method);assert.deepEqual(write.body.scene.rules.map(item=>item.id),['first','second']);
  assert.ok(ui.button('Apply priority'));
});

test('profile Apply delegates to the workspace history owner and legacy rename uses a body route',async context=>{
  const ui=uiDom(context),profiles={'.':scene([])},state=live(profiles['.']),sent=[],applied=[];
  const request=async(path,method,body)=>{sent.push({path,method,body});if(path==='/api/presets'&&!method)return profiles;if(path==='/api/state')return state;if(path==='/api/presets/action')return {Renamed:profiles['.']};throw new Error('Unexpected profile request');};
  const cleanup=await renderProfiles(ui.host,{request,state,getState:()=>state,onChange:async()=>{},onApplyProfile:async name=>applied.push(name)});ui.after(cleanup);await flush();
  assert.equal(ui.button('Update from current').disabled,true);ui.button('Apply profile').click();await flush();assert.deepEqual(applied,['.']);assert.equal(sent.some(item=>item.method),false);
  ui.button('Rename').click();await flush();const dialog=ui.findAll(ui.doc.body,node=>node.tagName==='dialog')[0],form=dialog.querySelector('form');form.querySelector('input').value='Renamed';form.listeners.get('submit')({preventDefault(){}});await flush();
  assert.deepEqual(sent.find(item=>item.method),{path:'/api/presets/action',method:'POST',body:{action:'rename',name:'.',target:'Renamed'}});
});

test('unchanged profile Rename closes without an error or request',async context=>{
  const ui=uiDom(context),profiles={Evening:scene([])},state=live(profiles.Evening),sent=[];
  const request=async(path,method)=>{if(method)sent.push(path);return path==='/api/presets'?profiles:state;};
  const cleanup=await renderProfiles(ui.host,{request,state,getState:()=>state});ui.after(cleanup);await flush();ui.button('Rename').click();await flush();
  const dialog=ui.findAll(ui.doc.body,node=>node.tagName==='dialog')[0];dialog.querySelector('form').listeners.get('submit')({preventDefault(){}});await flush();
  assert.deepEqual(sent,[]);assert.equal(ui.findAll(ui.doc.body,node=>node.tagName==='dialog').length,0);assert.equal(ui.byClass(ui.host,'notice')[0].textContent,'Profile name unchanged.');
});
