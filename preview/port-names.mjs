import {portLabel} from './port-map.mjs';

const node=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!==undefined)el.textContent=text;return el;};

export function openPortNames({ports,getState,request,onRefresh,onSaved}){
  const dialog=node('dialog','dialog port-names');dialog.setAttribute('aria-label','Port names');
  const form=node('form'),header=node('header','dialog-header'),title=node('h2','','Port names');header.append(title);
  const choice=node('label','field'),toggle=node('input');toggle.type='checkbox';toggle.name='useUnifi';
  choice.append(toggle,node('span','','Use UniFi port names'));
  const help=node('p','help-text','Applies to all ports on this switch. Your manual names are saved separately.');
  const connection=node('p','help-text');
  const fields=node('div','port-name-fields'),error=node('p','form-error');error.setAttribute('role','alert');error.hidden=true;
  const footer=node('footer','dialog-footer'),reload=node('button','button quiet','Reload names'),cancel=node('button','button','Cancel'),save=node('button','button primary','Save names');
  reload.type=cancel.type='button';save.type='submit';footer.append(reload,cancel,save);
  const inputs=new Map();let record=null,pending=false,manual={};
  for(const id of ports){
    const label=node('label','field'),input=node('input');input.type='text';input.maxLength=64;input.dataset.portName=String(id);input.autocomplete='off';
    const sfp=getState().sfp?.some(port=>port.port===id);label.append(node('span','',`${sfp?'SFP':'Port'} ${id}`),input);
    input.oninput=()=>{manual[id]=input.value;};fields.append(label);inputs.set(id,input);
  }
  function paint(){
    toggle.disabled=pending||!record;save.disabled=pending||!record;reload.disabled=pending;cancel.disabled=pending;
    connection.hidden=!toggle.checked||getState().controller?.connected===true;
    connection.textContent='Connect UniFi in Settings to load its port names.';
    for(const [id,input] of inputs){
      input.disabled=pending||!record||toggle.checked;
      const port=[...(getState().ports||[]),...(getState().sfp||[])].find(port=>port.port===id);
      input.value=toggle.checked&&port?portLabel({...port,name_mode:'unifi',display_name:port.controller?.port_name},getState().sfp?.some(p=>p.port===id)).text:manual[id]||'';
      input.placeholder=`${getState().sfp?.some(p=>p.port===id)?'SFP':'Port'} ${id}`;
    }
  }
  const showError=ex=>{error.textContent=ex.message;error.hidden=false;};
  async function load(){
    pending=true;error.hidden=true;paint();
    try{const next=await request('/api/port-names');if(!dialog.isConnected)return;
      record=next;manual={...next.names};toggle.checked=next.use_unifi;
    }catch(ex){showError(ex);}finally{pending=false;paint();}
  }
  toggle.onchange=paint;reload.onclick=load;cancel.onclick=()=>dialog.close();
  dialog.oncancel=event=>{if(pending)event.preventDefault();};
  form.onsubmit=async event=>{
    event.preventDefault();if(pending||!record)return;
    pending=true;error.hidden=true;paint();
    try{
      const names=Object.fromEntries(ports.map(id=>[String(id),manual[id]||'']));
      record=await request('/api/port-names','PUT',{expected_revision:record.revision,use_unifi:toggle.checked,names});
      if(!dialog.isConnected)return;
      await onRefresh();dialog.close();onSaved();
    }catch(ex){showError(ex);}finally{pending=false;paint();}
  };
  form.append(header,choice,help,connection,fields,error,footer);dialog.append(form);document.body.append(dialog);
  dialog.onclose=()=>dialog.remove();dialog.showModal();void load();return dialog;
}
