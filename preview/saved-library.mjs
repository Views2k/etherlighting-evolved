import {confirmAction} from './confirmation.mjs';
import {sceneTopology,lightingPorts,portRanges} from './port-topology.mjs';
import {node} from './dom.mjs';
export function selectionFits(item,state){
  const topology=sceneTopology({topology:item.value.topology}),current=lightingPorts(state);
  return topology&&JSON.stringify(topology)===JSON.stringify(current);
}
export async function openSavedLibrary({kind,request,getValue,onUse,getState}){
  const title=kind==='effect'?'Saved effect settings':'Saved selections';
  const dialog=node('dialog','dialog saved-library');dialog.setAttribute('aria-label',title);
  const header=node('header','dialog-header'),done=node('button','button quiet','Done');done.onclick=()=>dialog.close();
  header.append(node('h2','',title),done);
  const status=node('p','inline-error');status.setAttribute('role','alert');
  const form=node('form','library-save'),label=node('label','field'),input=node('input');
  input.required=true;input.maxLength=64;input.placeholder=kind==='effect'?'Effect settings name':'Selection name';
  label.append(node('span','','Name'),input);const save=node('button','button primary',kind==='effect'?'Save current settings':'Save current selection');
  const cancel=node('button','button quiet','Cancel edit');cancel.type='button';cancel.hidden=true;
  form.append(label,save,cancel);const list=node('div','library-list');let editing=null,busy=false,items=[];
  const error=ex=>{status.textContent=ex.message||'The change could not be completed.';};
  const setBusy=value=>{busy=value;form.inert=value;list.inert=value;dialog.setAttribute('aria-busy',String(value));};
  const reset=()=>{editing=null;input.value='';save.textContent=kind==='effect'?'Save current settings':'Save current selection';cancel.hidden=true;};
  cancel.onclick=reset;
  const load=async()=>{items=(await request('/api/library/'+kind)).items;paint();};
  function paint(){
    list.replaceChildren();
    if(!items.length)list.append(node('p','muted',kind==='effect'?'Save these settings to use them on another selection.':'Save selected ports to recall them together.'));
    for(const item of items){
      const row=node('article','library-item'),heading=node('h3','',item.name),description=node('p','muted',
        kind==='selection'?'Ports '+portRanges(item.value.ports):item.value.id.replaceAll('_',' '));
      const actions=node('div','dialog-actions'),use=node('button','button',kind==='selection'?'Select ports':'Preview settings'),
        rename=node('button','button quiet','Rename'),replace=node('button','button quiet','Replace'),remove=node('button','button quiet danger','Delete');
      use.disabled=kind==='selection'&&!selectionFits(item,getState());
      if(use.disabled)description.textContent+=' · Different switch layout';
      use.onclick=async()=>{setBusy(true);try{if(await onUse(item.value)!==false)dialog.close();}catch(ex){error(ex);}finally{setBusy(false);}};
      rename.onclick=()=>{editing={item,replace:false};input.value=item.name;save.textContent='Save name';cancel.hidden=false;input.focus();};
      replace.onclick=()=>{editing={item,replace:true};input.value=item.name;save.textContent='Replace saved settings';cancel.hidden=false;input.focus();};
      remove.onclick=async()=>{if(!await confirmAction(dialog,'Delete '+item.name+'?','Remove this saved '+(kind==='effect'?'effect configuration':'port selection')+'.','Delete'))return;
        setBusy(true);try{await request('/api/library/'+kind+'/'+item.id,'DELETE',{expected_revision:item.revision});reset();await load();}catch(ex){error(ex);}finally{setBusy(false);}};
      actions.append(use,rename,replace,remove);row.append(heading,description,actions);list.append(row);
    }
  }
  form.onsubmit=async event=>{event.preventDefault();if(busy)return;setBusy(true);status.textContent='';
    try{const value=editing&&!editing.replace?structuredClone(editing.item.value):getValue();
      if(kind==='selection')delete value.topology;
      if(!value)throw new Error('Choose effect settings first.');
      await request('/api/library/'+kind+(editing?'/'+editing.item.id:''),editing?'PUT':'POST',
        {name:input.value.trim(),value,...(editing?{expected_revision:editing.item.revision}:{})});reset();await load();
    }catch(ex){error(ex);}finally{setBusy(false);}};
  dialog.addEventListener('cancel',event=>{if(busy)event.preventDefault();});
  dialog.append(header,form,status,list);document.body.append(dialog);dialog.onclose=()=>dialog.remove();dialog.showModal();
  try{await load();}catch(ex){error(ex);}return dialog;
}
export async function openSceneHistory({request,onRestore}){
  const dialog=node('dialog','dialog scene-history');dialog.setAttribute('aria-label','Lighting history');
  const header=node('header','dialog-header'),done=node('button','button quiet','Done');done.onclick=()=>dialog.close();
  header.append(node('h2','','Lighting history'),done);const error=node('p','inline-error'),list=node('div','library-list');
  dialog.append(header,error,list);document.body.append(dialog);dialog.onclose=()=>dialog.remove();dialog.showModal();
  try{
    const result=await request('/api/history');
    if(result.error)error.textContent=result.error;
    for(const entry of result.entries||result.history||[]){
      const row=node('article','library-item'),button=node('button','button','Restore');
      row.append(node('h3','',new Date(entry.at||entry.created_at).toLocaleString()),node('p','muted',entry.summary||[String(entry.scene?.brightness??100)+'% brightness',...(entry.scene?.zones||[]).map(zone=>zone.name+' · '+zone.effect.id.replaceAll('_',' ')+' · Ports '+portRanges(zone.ports))].join(' / ')),button);
      button.onclick=async()=>{if(!await confirmAction(dialog,'Restore this setup?','Apply this saved lighting setup to the connected switch.','Restore'))return;
        button.disabled=true;try{await onRestore(entry);dialog.close();}catch(ex){error.textContent=ex.message;}finally{button.disabled=false;}};
      list.append(row);
    }
    if(!list.children.length)list.append(node('p','muted','Lighting changes will appear here.'));
  }catch(ex){error.textContent=ex.message;}
}
