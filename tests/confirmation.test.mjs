import test from 'node:test';
import assert from 'node:assert/strict';
import {confirmAction} from '../preview/confirmation.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

function confirmationDom(context){
  const dom=rendererDom(context),create=dom.doc.createElement;
  dom.doc.createElement=tag=>{
    const element=create(tag);
    element.showModal=()=>{element.open=true;};element.close=()=>{element.open=false;};
    element.remove=()=>{const siblings=element.parentNode?.children;if(siblings)siblings.splice(siblings.indexOf(element),1);element.parentNode=null;};
    Object.defineProperty(element,'firstElementChild',{get:()=>element.children[0]});
    return element;
  };
  return {...dom,dialogs:()=>dom.findAll(dom.doc.body,e=>e.tagName==='dialog'),button:label=>dom.findAll(dom.doc.body,e=>e.tagName==='button'&&e.textContent===label)[0]};
}

test('repeated leave requests share one decision and restore focus once',async context=>{
  const dom=confirmationDom(context),focus=dom.doc.createElement('button');dom.host.append(focus);focus.focus();
  const requests=Array.from({length:3},()=>confirmAction(dom.host,'Discard changes?','Unsaved changes will be discarded.','Discard'));
  assert.equal(dom.dialogs().length,1);
  dom.button('Cancel').click();assert.deepEqual(await Promise.all(requests),[false,false,false]);
  assert.equal(dom.dialogs().length,0);assert.equal(dom.doc.activeElement,focus);
  const next=confirmAction(dom.host,'Discard changes?','Unsaved changes will be discarded.','Discard');
  dom.button('Discard').click();assert.equal(await next,true);
});

test('a different action cannot reuse a pending approval and abort clears the pending dialog',async context=>{
  const dom=confirmationDom(context),controller=new AbortController();
  const first=confirmAction(dom.host,'Delete profile?','Delete this saved profile.','Delete',controller.signal);
  const different=confirmAction(dom.host,'Discard changes?','Discard unsaved changes.','Discard',controller.signal);
  assert.equal(dom.dialogs().length,1);assert.equal(await different,false);
  controller.abort();assert.equal(await first,false);assert.equal(dom.dialogs().length,0);
  const next=confirmAction(dom.host,'Discard changes?','Discard unsaved changes.','Discard');
  dom.button('Discard').click();assert.equal(await next,true);
});
