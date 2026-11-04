import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {fixedColorSettings,fixedColorBrightness,equalSetting,sfpSettings} from '../preview/lighting-state.mjs';
import {sfpEditTargets,sfpSync,normalizeSfpBrightness,editableSfpLight} from '../preview/sfp-sync.mjs';
import {portEffect,leaveSfpEffect} from '../preview/effect-draft.mjs';
import {manualDisplayRgb} from '../preview/port-display.mjs';
import {sfpPreviewColor} from '../preview/effect-gallery.mjs';
import {sceneFingerprint} from '../preview/secondary-model.mjs';

const source=await readFile(new URL('../preview/app.mjs',import.meta.url),'utf8');
const extract=(start,end)=>source.slice(source.indexOf(start),source.indexOf(end,source.indexOf(start)));
const no=()=>{};
const effect={id:'static',params:{color:'#ff0000'}};
function fixture({selected=[1],master=100,overrides={1:{color:'#ff0000',brightness:100}}}={}){
  const controls=new Map(),element=key=>{if(!controls.has(key))controls.set(key,{dataset:{},setAttribute:no,addEventListener:no});return controls.get(key);};
  const grid={querySelector:element,innerHTML:''};
  const host={innerHTML:'',querySelector:key=>key==='#parameter-grid'?grid:element(key),querySelectorAll:()=>[],contains:()=>false,insertAdjacentHTML:no};
  let captured,rendered=0;
  const messages=[],displayed=[];
  const context={state:{brightness:master,num_ports:1,ports:[{port:1,color:'#ff0000'}],sfp:[{port:9,mode:'color',effect:'steady',color:'#ffffff',brightness:100,color_brightness:100}],sfp_sync:{mode:'independent',source_port:1},zones:[{id:'all',name:'All',ports:[1],effect}],overrides},
    selected:new Set(selected),draft:{},draftBaseline:null,editor:'color',effects:[{id:'static',name:'Static',params:[]}],copiedAppearance:null,
    portEffect,leaveSfpEffect,activeColorPopover:undefined,inspectorSignature:'',page:'Lighting',busy:0,edits:new Set(),
    document:{querySelector:key=>key==='#inspector'?host:null,activeElement:{}},
    copperSelection:()=>[...context.selected].filter(id=>id===1),sfpSelection:()=>[...context.selected].filter(id=>id===9),
    currentEffect:()=>effect,hasMixedEffects:()=>false,effectSpec:()=>({id:'static',params:[]}),defaultConfig:()=>({params:{}}),
    selectionEffects:()=>[JSON.stringify(effect)],selectionName:()=>'',sourceSummary:()=>'',escape:String,
    clone:structuredClone,controlSection:()=>'',colorControl:(_key,_label,color,brightness)=>{displayed.push({color,brightness});return '';},canFollowGroup:()=>true,
    bindColors:(_host,callback)=>{captured=callback;},fixedColorSettings,fixedColorBrightness,equalSetting,
    updateDraftBar:no,setMixedColor:no,queueEdit:()=>{context.draftBaseline??=sceneFingerprint(context.state);context.edits.add('colors');},
    sfpSync,sfpEditTargets,normalizeSfpBrightness,editableSfpLight,sfpSettings,
    sfpSyncControls:()=>'',bindSfpSync:no,sfpInspectorKey:()=>'',updateRangeProgress:no,
    renderInspector:()=>{rendered++;context.renderInspectorContent();},notify:(message,error)=>messages.push({message,error}),isSfpPort:id=>id===9,
    number:(value,fallback=0)=>Number.isFinite(Number(value))?Number(value):fallback,
  };
  vm.createContext(context);
  vm.runInContext(extract('function effectSelection()', 'function selectionEffects()'),context);
  vm.runInContext(extract('function groupedSfpSelection()', 'function hasMixedEffects()'),context);
  vm.runInContext(extract('function copperInspectorKey()', 'function renderInspector()'),context);
  vm.runInContext(extract('function renderInspectorContent()', 'function setMixedColor('),context);
  vm.runInContext(extract('function renderSfp(host)', 'function reconcileEditor('),context);
  vm.runInContext(extract('function reconcileEditor(', 'function updateRangeProgress('),context);
  return {context,host,element,messages,displayed,render:()=>context.renderInspectorContent(),edit:(...args)=>captured(...args),renderCount:()=>rendered};
}

test('clean fixed-color inspector reloads externally changed color and brightness',()=>{
  const ui=fixture();ui.render();
  ui.context.state={...ui.context.state,overrides:{1:{color:'#0000ff',brightness:75}}};
  ui.context.reconcileEditor();assert.equal(ui.renderCount(),1);
  assert.deepEqual(ui.displayed.at(-1),{color:'#0000ff',brightness:75});
  ui.edit('manual','#0000ff',50,'brightness');
  assert.equal(ui.context.draft.colors[1].color,'#0000ff');assert.equal(ui.context.draft.colors[1].brightness,50);
});

test('focused clean controls resolve untouched values freshly without replacing the focused control',()=>{
  const ui=fixture();ui.render();ui.host.contains=()=>true;
  ui.context.state={...ui.context.state,overrides:{1:{color:'#0000ff',brightness:75}}};
  ui.context.reconcileEditor();assert.equal(ui.renderCount(),0);
  ui.edit('manual','#ff0000',50,'brightness');
  assert.equal(ui.context.draft.colors[1].color,'#0000ff');assert.equal(ui.context.draft.colors[1].brightness,50);
});

test('an existing color draft and original conflict baseline survive external scene changes',()=>{
  const ui=fixture();ui.render();ui.edit('manual','#00ff00',100,'color');const baseline=ui.context.draftBaseline;
  ui.context.state={...ui.context.state,overrides:{1:{color:'#0000ff',brightness:75}}};
  ui.context.reconcileEditor();assert.equal(ui.renderCount(),0);ui.edit('manual','#00ff00',50,'brightness');
  assert.equal(ui.context.draft.colors[1].color,'#00ff00');assert.equal(ui.context.draftBaseline,baseline);
  assert.notEqual(baseline,sceneFingerprint(ui.context.state));
});

test('first fixed-color brightness edit captures the displayed field rather than a later animation frame',()=>{
  const ui=fixture({overrides:{}});ui.render();
  const visible=ui.displayed.at(-1).color;
  ui.context.state.ports[0].color='#ff8800';
  ui.edit('manual',visible,40,'brightness');
  assert.equal(ui.context.draft.colors[1].color,visible);
  ui.context.state.ports[0].color='#00ff00';
  ui.edit('manual',visible,41,'brightness');
  assert.equal(ui.context.draft.colors[1].color,visible);
  assert.equal(ui.context.draft.colors[1].brightness,41);
});

test('inherited brightness refreshes clean controls when Default brightness changes',()=>{
  const ui=fixture({overrides:{1:{color:'#ffffff',brightness:null}}});ui.render();ui.context.state.brightness=30;
  ui.context.reconcileEditor();assert.deepEqual(ui.displayed.at(-1),{color:'#ffffff',brightness:30});
});

test('both mixed and SFP-only paste preserve effective copper brightness',()=>{
  for(const selected of [[1,9],[9]]){
    const ui=fixture({selected,master:50});ui.context.copiedAppearance={type:'color',settings:{color:'#ffffff',brightness:50}};ui.render();
    ui.element(selected.length===1?'#paste-sfp':'#paste-appearance').onclick();
    assert.deepEqual(sfpPreviewColor(ui.context.draft.sfp[9],'steady',0,50),manualDisplayRgb('#ffffff',50,50));
    if(selected.length>1)assert.equal(ui.context.draft.colors[1].brightness,50);
    assert.equal(ui.context.state.brightness,50);assert.deepEqual(ui.messages,[]);
  }
});

test('unrepresentable SFP paste leaves all selected targets and existing drafts unchanged',()=>{
  for(const selected of [[1,9],[9]]){
    const ui=fixture({selected,master:40});ui.context.copiedAppearance={type:'color',settings:{color:'#ffffff',brightness:80}};
    ui.context.draft={colors:{1:{color:'#123456',brightness:20}}};const before=JSON.stringify(ui.context.draft);ui.render();
    ui.element(selected.length===1?'#paste-sfp':'#paste-appearance').onclick();
    assert.equal(JSON.stringify(ui.context.draft),before);assert.equal(ui.context.edits.size,0);assert.equal(ui.context.state.brightness,40);
    assert.match(ui.messages[0].message,/Raise Default brightness to at least 80%/);
  }
});

test('paste handles zero, inheritance and integer precision without changing Default brightness',()=>{
  const ui=fixture({master:0});
  assert.equal(ui.context.sfpColorFromAppearance({color:'#ffffff',brightness:0}).color_brightness,0);
  assert.equal(ui.context.sfpColorFromAppearance({color:'#ffffff',brightness:null}).color_brightness,100);
  assert.throws(()=>ui.context.sfpColorFromAppearance({color:'#ffffff',brightness:1}),/Raise Default brightness/);
  ui.context.state.brightness=80;
  const light=ui.context.sfpColorFromAppearance({color:'#ffffff',brightness:50});
  assert.equal(light.color_brightness,63);
  assert.ok(Math.abs(sfpPreviewColor(light,'steady',0,80)[0]-127.5)<=255*.8/200+1e-8);
});
