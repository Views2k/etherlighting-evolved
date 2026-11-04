const stages={engine:'Lighting service',shutdown:'Workspace shutdown',parent_cleanup:'Desktop connection lost',desktop_exit:'Desktop shutdown'};
const outcomes={closed:'Cleanup confirmed',failed:'Service stopped with an error',cleanup_unconfirmed:'Lighting cleanup needs review'};

export function lastSessionView(payload){
  const entry=payload?.last_session;
  if(payload?.available!==true)return {message:'Session details could not be read. Try Refresh.'};
  if(!entry)return {message:'No earlier session has been recorded.'};
  if(!stages[entry.stage]||!outcomes[entry.outcome]||!Number.isFinite(Date.parse(entry.time)))return {message:'Session details could not be read. Try Refresh.'};
  return {rows:[['Time',new Date(entry.time).toLocaleString()],['Stage',stages[entry.stage]],
    ['Result',outcomes[entry.outcome]],['Version',entry.build||'Unknown'],
    ...(Number.isSafeInteger(entry.attempts)?[['Cleanup attempts',String(entry.attempts)]]:[])],
    message:entry.outcome==='closed'?'':'Review the connection status before resuming lighting.'};
}

export function mountLastSession(host,{request}){
  let active=true,pending=false;
  const title=document.createElement('h2');title.textContent='Last session';
  const content=document.createElement('div');content.className='last-session-content';
  const refresh=document.createElement('button');refresh.type='button';refresh.className='button quiet';refresh.textContent='Refresh session details';
  host.append(title,content,refresh);
  const render=payload=>{
    const model=lastSessionView(payload);content.replaceChildren();
    if(model.rows){
      const details=document.createElement('dl');details.className='settings-details';
      for(const [label,value] of model.rows){const dt=document.createElement('dt'),dd=document.createElement('dd');dt.className='muted';dt.textContent=label;dd.textContent=value;details.append(dt,dd);}
      content.append(details);
    }
    if(model.message){const message=document.createElement('p');message.className='help-text';message.textContent=model.message;content.append(message);}
  };
  const reload=async()=>{
    if(!active||pending)return;
    pending=true;refresh.disabled=true;content.setAttribute('aria-busy','true');
    try{const result=await request('/api/last-session');if(active)render(result);}
    catch{if(active)render({available:false});}
    finally{pending=false;if(active){refresh.disabled=false;content.removeAttribute('aria-busy');}}
  };
  refresh.addEventListener('click',reload);void reload();
  const cleanup=()=>{active=false;refresh.removeEventListener('click',reload);};
  cleanup.refresh=reload;return cleanup;
}
