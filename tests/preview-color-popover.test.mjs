import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
import {bindPopoverDismissal} from '../preview/popover-interaction.mjs';
import {hexToHsv,hsvToHex,hexToRgb,rgbToHex,normalizeHex} from '../src/color.mjs';

const source=await readFile(new URL('../preview/color-picker.mjs',import.meta.url),'utf8');
const popoverSource=source.slice(source.indexOf('export function openColorPicker')).replace('export function','function');
const settle=()=>new Promise(resolve=>setTimeout(resolve,5));

function fixture({height=800,callback=false,color}={}){
  class Target {
    listeners=new Map();
    addEventListener(name,callback){if(!this.listeners.has(name))this.listeners.set(name,new Set());this.listeners.get(name).add(callback);}
    removeEventListener(name,callback){this.listeners.get(name)?.delete(callback);}
    emit(name,values={}){const event={target:this,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;},...values};for(const callback of this.listeners.get(name)||[])callback(event);return event;}
  }
  const document=new Target();
  class Element extends Target {
    children=[];parentNode=null;attributes=new Map();captured=new Set();value='';selectors=new Map();
    style={setProperty(name,value){this[name]=value;}};
    ownerDocument=document;
    append(...items){for(const item of items){this.children.push(item);item.parentNode=this;}}
    contains(target){return target===this||this.children.some(child=>child.contains(target));}
    get isConnected(){return document.body.contains(this);}
    remove(){if(this.parentNode)this.parentNode.children=this.parentNode.children.filter(item=>item!==this);this.parentNode=null;}
    setAttribute(name,value){this.attributes.set(name,value);}
    focus(){document.activeElement=this;}
    setPointerCapture(id){this.captured.add(id);}
    hasPointerCapture(id){return this.captured.has(id);}
    releasePointerCapture(id){this.captured.delete(id);}
    getBoundingClientRect(){return this.className==='color-sv'?{left:20,top:20,width:100,height:100,bottom:120}:{left:20,top:20,width:260,height:400,bottom:420};}
    checkValidity(){return !this.validationMessage;}
    reportValidity(){}
    setCustomValidity(message){this.validationMessage=message;}
    dispatchEvent(event){this.emit(event.type);}
    querySelector(selector){
      if(!this.selectors.has(selector)){const element=new Element();element.className=selector.startsWith('.')?selector.slice(1):'';this.selectors.set(selector,element);this.append(element);}
      return this.selectors.get(selector);
    }
    querySelectorAll(selector){return selector==='[data-rgb]'?[0,1,2].map(index=>this.querySelector(`[data-rgb="${index}"]`)):[];}
  }
  document.body=new Element();document.documentElement=new Element();document.activeElement=document.body;
  document.createElement=()=>new Element();
  const anchor=new Element();anchor.value='#FF0000';document.body.append(anchor);
  const draft={},writes=[];let activeClose,closed=0,rangeUpdates=0;anchor.addEventListener('input',()=>{draft.color=anchor.value;});
  const context={document,window:new Target(),innerWidth:800,innerHeight:height,hexToHsv,hsvToHex,hexToRgb,rgbToHex,normalizeHex,
    bindPopoverDismissal,ResizeObserver:class{observe(){}disconnect(){}},Event:class{constructor(type){this.type=type;}},
    mountColorLibrary:()=>Object.assign(()=>{},{update(){}}),
    request:(...args)=>writes.push(args)};
  vm.createContext(context);vm.runInContext(popoverSource,context);
  const open=()=>{activeClose?.();activeClose=context.openColorPicker(anchor,{color,
    ...(callback?{onChange:value=>{draft.color=value;}}:{}),onClose(){closed++;activeClose=undefined;},onRangeChange(){rangeUpdates++;}});};
  open();
  const popup=document.body.children.find(item=>item!==anchor),sv=popup.querySelector('.color-sv'),hue=popup.querySelector('.color-hue');
  const pointer=(name,target,id=1,values={})=>{
    const event={target,pointerId:id,pointerType:'touch',button:0,clientX:70,clientY:70,...values};
    document.emit(name,event);target.emit(name,event);
  };
  return {document,context,popup,anchor,sv,hue,draft,writes,pointer,Element,open,
    get activeClose(){return activeClose;},get closed(){return closed;},get rangeUpdates(){return rangeUpdates;},close:()=>activeClose?.()};
}

test('a short viewport keeps the scrollable picker below the top edge',()=>{
  const f=fixture({height:400});
  try{assert.equal(f.popup.style.top,'12px');}finally{f.close();}
});

test('touching and dragging the hue range survives transient focus loss and stages color only',async context=>{
  const f=fixture();context.after(f.close);
  f.pointer('pointerdown',f.hue);f.document.activeElement=f.document.body;
  f.popup.emit('focusout',{target:f.sv,relatedTarget:null});await settle();
  assert.equal(f.popup.isConnected,true);
  f.hue.value=180;f.hue.emit('input');f.pointer('pointermove',f.hue,1,{clientX:120});
  assert.equal(f.draft.color,'#00FFFF');assert.equal(f.popup.isConnected,true);
  f.pointer('pointerup',f.document.body);await settle();
  assert.equal(f.popup.isConnected,true);assert.deepEqual(f.writes,[]);
  f.pointer('pointerdown',f.document.body,2);assert.equal(f.popup.isConnected,false);
  assert.equal(f.draft.color,'#00FFFF');assert.deepEqual(f.writes,[]);
});

test('quick touch release before the focus check still keeps the picker open',async context=>{
  const f=fixture();context.after(f.close);
  f.pointer('pointerdown',f.hue);f.document.activeElement=f.document.body;
  f.popup.emit('focusout',{relatedTarget:null});f.pointer('pointerup',f.hue);await settle();
  assert.equal(f.popup.isConnected,true);
  f.popup.emit('focusout',{relatedTarget:null});await settle();assert.equal(f.popup.isConnected,true);
});

test('saturation/value captures a drag, clamps outside coordinates and releases on cancel',()=>{
  const f=fixture();
  try{
    f.pointer('pointerdown',f.sv,7,{clientX:65,clientY:65});assert.equal(f.sv.hasPointerCapture(7),true);
    f.pointer('pointermove',f.sv,7,{clientX:250,clientY:-50});assert.equal(f.draft.color,'#FF0000');
    f.pointer('pointermove',f.sv,7,{clientX:-50,clientY:250});assert.equal(f.draft.color,'#000000');
    assert.equal(f.popup.isConnected,true);f.pointer('pointercancel',f.sv,7);assert.equal(f.sv.hasPointerCapture(7),false);
    f.pointer('pointerdown',f.document.body,8);assert.equal(f.popup.isConnected,false);assert.deepEqual(f.writes,[]);
  }finally{f.close();}
});

test('an unrelated touch cannot dismiss an active drag, but a later outside touch can',()=>{
  const f=fixture();
  try{
    f.pointer('pointerdown',f.hue,1);f.pointer('pointerdown',f.document.body,2);assert.equal(f.popup.isConnected,true);
    f.pointer('pointerup',f.hue,1);f.pointer('pointerdown',f.document.body,3);assert.equal(f.popup.isConnected,false);
  }finally{f.close();}
});

test('keyboard focus can move within the picker and exits normally to another control',async context=>{
  const f=fixture();context.after(f.close);
  f.document.emit('keydown',{key:'Tab'});
  f.hue.focus();f.popup.emit('focusout',{relatedTarget:f.hue});await settle();assert.equal(f.popup.isConnected,true);
  const next=new f.Element();f.document.body.append(next);next.focus();f.popup.emit('focusout',{relatedTarget:next});await settle();
  assert.equal(f.popup.isConnected,false);assert.equal(f.document.activeElement,next);
});

test('focus returning to an outside control after touch release does not dismiss the picker',async context=>{
  const f=fixture();context.after(f.close);
  const outside=new f.Element();f.document.body.append(outside);
  f.pointer('pointerdown',f.hue);f.pointer('pointerup',f.hue);
  outside.focus();f.popup.emit('focusout',{relatedTarget:outside});await settle();
  assert.equal(f.popup.isConnected,true);
  f.hue.value=240;f.hue.emit('input');
  assert.equal(f.draft.color,'#0000FF');assert.deepEqual(f.writes,[]);
  f.pointer('pointerdown',outside,2);assert.equal(f.popup.isConnected,false);
});

test('touch cancels a pending keyboard dismissal before its focus check runs',async context=>{
  const f=fixture();context.after(f.close);
  const outside=new f.Element();f.document.body.append(outside);
  f.document.emit('keydown',{key:'Tab'});outside.focus();f.popup.emit('focusout',{relatedTarget:outside});
  f.pointer('pointerdown',f.hue);f.pointer('pointerup',f.hue);await settle();
  assert.equal(f.popup.isConnected,true);
  f.popup.querySelector('.color-popup-footer button').emit('click');
  assert.equal(f.popup.isConnected,false);assert.equal(f.document.activeElement,f.anchor);
});

test('Escape restores the anchor and removes all pointer and keyboard dismissal listeners',()=>{
  const f=fixture();
  const event=f.document.emit('keydown',{key:'Escape'});
  assert.equal(event.prevented,true);assert.equal(event.stopped,true);assert.equal(f.popup.isConnected,false);
  assert.equal(f.document.activeElement,f.anchor);
  for(const name of ['pointerdown','pointerup','pointercancel','lostpointercapture','keydown'])assert.equal(f.document.listeners.get(name).size,0);
});

test('closing a picker cancels deferred focus checks without dismissing its replacement',async context=>{
  const f=fixture();context.after(f.close);
  f.document.emit('keydown',{key:'Tab'});
  const next=new f.Element();f.document.body.append(next);next.focus();f.popup.emit('focusout',{relatedTarget:next});
  f.open();await settle();
  assert.equal(f.popup.isConnected,false);assert.equal(typeof f.activeClose,'function');
  assert.equal(f.document.body.children.filter(item=>item.className==='color-popover popover').length,1);
});

test('a valid RGB edit clears a previous hex error and Done closes with the edited color still only staged',()=>{
  const f=fixture();
  try{
    const hex=f.popup.querySelector('#popup-hex'),green=f.popup.querySelector('[data-rgb="1"]'),done=f.popup.querySelector('.color-popup-footer button');
    hex.focus();hex.value='zzzzzz';hex.emit('change');done.emit('click');
    assert.equal(hex.checkValidity(),false);assert.equal(f.popup.isConnected,true);
    green.focus();green.value='128';green.emit('change');
    assert.equal(hex.value,'#FF8000');assert.equal(hex.checkValidity(),true);assert.equal(f.draft.color,'#FF8000');
    done.emit('click');assert.equal(f.popup.isConnected,false);assert.equal(f.document.activeElement,f.anchor);assert.deepEqual(f.writes,[]);
  }finally{f.close();}
});

test('a valid hue edit also replaces an invalid hex entry when focus has not moved',()=>{
  const f=fixture();
  try{
    const hex=f.popup.querySelector('#popup-hex');hex.focus();hex.value='invalid';hex.emit('change');
    f.hue.value=120;f.hue.emit('input');
    assert.equal(hex.value,'#00FF00');assert.equal(hex.checkValidity(),true);assert.equal(f.draft.color,'#00FF00');assert.deepEqual(f.writes,[]);
  }finally{f.close();}
});


test('a second finger cannot move the color or end the active saturation/value drag',()=>{
  const f=fixture();
  try{
    f.pointer('pointerdown',f.sv,1,{clientX:70,clientY:70});const first=f.draft.color;
    f.pointer('pointerdown',f.sv,2,{clientX:120,clientY:20});
    f.pointer('pointermove',f.sv,2,{clientX:20,clientY:120});
    assert.equal(f.draft.color,first);assert.equal(f.sv.hasPointerCapture(2),false);
    f.pointer('pointerup',f.sv,2);
    f.pointer('pointermove',f.sv,1,{clientX:120,clientY:20});assert.equal(f.draft.color,'#FF0000');
    f.pointer('pointerup',f.sv,1);
    f.pointer('pointerdown',f.sv,3,{clientX:20,clientY:120});assert.equal(f.draft.color,'#000000');
    assert.equal(f.popup.isConnected,true);assert.deepEqual(f.writes,[]);
  }finally{f.close();}
});

test('canceled or lost capture lets the next gesture start without accepting stale movement',()=>{
  for(const end of ['pointercancel','lostpointercapture']){
    const f=fixture();
    try{
      f.pointer('pointerdown',f.sv,1,{clientX:70,clientY:70});
      if(end==='lostpointercapture')f.sv.releasePointerCapture(1);
      f.pointer(end,f.sv,1);assert.equal(f.sv.hasPointerCapture(1),false);
      f.pointer('pointerdown',f.sv,2,{clientX:120,clientY:20});assert.equal(f.draft.color,'#FF0000');
      f.pointer('pointermove',f.sv,1,{clientX:20,clientY:120});assert.equal(f.draft.color,'#FF0000');
      f.pointer('pointermove',f.sv,2,{clientX:20,clientY:120});assert.equal(f.draft.color,'#000000');
      assert.equal(f.popup.isConnected,true);assert.deepEqual(f.writes,[]);
    }finally{f.close();}
  }
});

test('secondary mouse and pen buttons do not begin a saturation/value gesture',()=>{
  const f=fixture();
  try{
    for(const pointerType of ['mouse','pen']){
      f.pointer('pointerdown',f.sv,1,{pointerType,button:2});
      assert.equal(f.sv.hasPointerCapture(1),false);assert.equal(f.draft.color,undefined);
    }
    f.pointer('pointerdown',f.sv,2,{pointerType:'mouse',clientX:120,clientY:20});
    assert.equal(f.draft.color,'#FF0000');assert.deepEqual(f.writes,[]);
  }finally{f.close();}
});

test('callback mode starts from its supplied color and stages updates without changing the anchor or sending requests',()=>{
  const f=fixture({callback:true,color:'#00FF00'});
  try{
    assert.equal(f.popup.querySelector('#popup-hex').value,'#00FF00');assert.equal(f.draft.color,undefined);
    f.hue.value=240;f.hue.emit('input');
    assert.equal(f.draft.color,'#0000FF');assert.equal(f.anchor.value,'#FF0000');assert.deepEqual(f.writes,[]);
    assert.ok(f.rangeUpdates>=2);f.popup.querySelector('.color-popup-footer button').emit('click');
    assert.equal(f.closed,1);assert.equal(f.document.activeElement,f.anchor);assert.equal(f.activeClose,undefined);
  }finally{f.close();}
});
