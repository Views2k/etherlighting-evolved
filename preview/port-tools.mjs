import {linkStatus,portLabel,portPower,isUplink} from './port-map.mjs';
import {controlSource} from './lighting-state.mjs';
const portsOf=state=>[...(state.ports||[]),...(state.sfp||[])];
const node=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!==undefined)el.textContent=text;return el;};
export function findPorts(state,query){
  const words=query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  return portsOf(state).filter(port=>{
    const text=[port.port,`port ${port.port}`,state.sfp?.some(p=>p.port===port.port)?`sfp ${port.port}`:'',
      port.name,port.label,port.controller?.name,portLabel(port).text,...(port.controller?.clients||[]).map(c=>c.name)].join(' ').toLocaleLowerCase();
    return words.every(word=>text.includes(word));
  });
}
export function createPortHistory(){
  let generation=null,sequence=null,lastConnected=false,segment=0;
  const records=new Map();
  return {
    clear(){generation=null;sequence=null;lastConnected=false;segment=0;records.clear();},
    update(state,disconnected=false){
      const sample=state.telemetry||{},connected=!disconnected&&state.connected&& !sample.stale;
      if(!connected){lastConnected=false;return;}
      if(!sample.generation||sample.sequence===undefined||!sample.sampled_at)return;
      if(generation===sample.generation&&sequence===sample.sequence)return;
      const at=Date.parse(sample.sampled_at);if(!Number.isFinite(at))return;
      const continuous=lastConnected&&generation===sample.generation;
      if(!continuous)segment++;
      generation=sample.generation;sequence=sample.sequence;lastConnected=true;
      for(const port of portsOf(state)){
        const prior=records.get(port.port)||{samples:[],events:[],link:null};
        const link=linkStatus(port),traffic=port.facts?.traffic;
        if(continuous&&prior.link&&prior.link.label!==link.label&&link.online!==null&&prior.link.online!==null)
          prior.events.unshift({at,text:`${prior.link.label} → ${link.label}`});
        if(!continuous&&prior.samples.length)prior.events.unshift({at,text:'Monitoring resumed'});
        prior.link=link;
        prior.samples.push({at,value:Number.isFinite(traffic)&&traffic>=0?traffic:null,segment});
        prior.samples=prior.samples.filter(p=>at-p.at<=60000).slice(-120);prior.events=prior.events.slice(0,12);
        records.set(port.port,prior);
      }
    },
    get(port){return records.get(port)||{samples:[],events:[]};}
  };
}
export function openFindPorts({getState,onSelect}){
  const dialog=node('dialog','dialog port-finder');dialog.setAttribute('aria-label','Find a port');
  const header=node('header','dialog-header'),title=node('h2','','Find a port'),close=node('button','button quiet','Done');
  close.onclick=()=>dialog.close();header.append(title,close);
  const field=node('label','field'),label=node('span','','Search ports and devices'),input=node('input');
  input.type='search';input.placeholder='Port number or device name';field.append(label,input);
  const results=node('div','port-search-results');results.setAttribute('aria-live','polite');
  const paint=()=>{results.replaceChildren();const matches=findPorts(getState(),input.value);
    if(!matches.length)results.append(node('p','muted','No matching ports.'));
    for(const port of matches){const sfp=getState().sfp?.some(p=>p.port===port.port),button=node('button','button port-search-result');
      button.append(node('span','',`${sfp?'SFP':'Port'} ${port.port} · ${portLabel(port,sfp).text}`),node('span','muted',linkStatus(port).label));
      button.onclick=async()=>{if(await onSelect([port.port]))dialog.close();};results.append(button);}
  };
  input.oninput=paint;dialog.append(header,field,results);document.body.append(dialog);dialog.onclose=()=>{clearInterval(timer);dialog.remove();};const timer=setInterval(paint,1000);paint();dialog.showModal();input.focus();return dialog;
}
function trendMarkup(samples){
  const values=samples.filter(p=>p.value!==null);if(values.length<2)return '';
  const end=samples.at(-1).at,start=end-60000,max=Math.max(1,...values.map(p=>p.value));
  let paths=[],line=[],segment=null;
  for(const p of samples){if(p.value===null||segment!==null&&segment!==p.segment){if(line.length>1)paths.push(line.join(' '));line=[];}
    segment=p.segment;if(p.value!==null)line.push(`${((p.at-start)/60000*300).toFixed(1)},${(52-p.value/max*48).toFixed(1)}`);}
  if(line.length>1)paths.push(line.join(' '));
  return `<svg viewBox="0 0 300 56" role="img" aria-label="Combined traffic over the last minute">${paths.map(points=>`<polyline points="${points}" fill="none" stroke="currentColor" stroke-width="2"/>`).join('')}</svg>`;
}
export function openLivePortDetails({ports,getState,effects,history,request,onRefresh,onError,isDisconnected=()=>false}){
  const dialog=node('dialog','dialog port-details');dialog.setAttribute('aria-label','Selected port details');
  const header=node('header','dialog-header'),close=node('button','button quiet','Done');header.append(node('h2','','Port details'),close);close.onclick=()=>dialog.close();
  const content=node('div','port-details-content'),rows=new Map();let pending=false,sampleKey='',sampleReceived=performance.now(),sampleAge=0;
  for(const id of ports){
    const section=node('section'),title=node('h3'),dl=node('dl','settings-details'),fields={};
    for(const key of ['Link','Traffic','Power','Lighting','Sample']){dl.append(node('dt','',key));fields[key]=node('dd');dl.append(fields[key]);}
    const clients=node('p','muted'),trend=node('div','port-trend'),events=node('ul','port-events'),identify=node('button','button','Identify for 10 seconds');
    identify.type='button';identify.onclick=async()=>{if(pending)return;pending=true;paint();
      try{const active=getState().identify;if(active?.port===id)await request('/api/identify','DELETE',{id:active.id});else await request('/api/identify','POST',{port:id});await onRefresh();}
      catch(error){onError(error.message,true);}finally{pending=false;paint();}
    };
    section.append(title,dl,clients,trend,events,identify);content.append(section);rows.set(id,{title,fields,clients,trend,events,identify});
  }
  function paint(){
    if(!dialog.isConnected)return;
    const state=getState(),key=state.telemetry?.generation+':'+state.telemetry?.sequence;
    if(key!==sampleKey){sampleKey=key;sampleReceived=performance.now();sampleAge=state.telemetry?.sample_age_ms||0;}
    const stale=isDisconnected()||!state.connected||state.telemetry?.stale||performance.now()-sampleReceived+sampleAge>10000;
    for(const [id,row] of rows){
      const port=portsOf(state).find(p=>p.port===id);if(!port)continue;
      const sfp=state.sfp?.some(p=>p.port===id),link=linkStatus(port),sample=state.telemetry;
      const name=portLabel(port,sfp).text,number=`${sfp?'SFP':'Port'} ${id}`;row.title.textContent=number+(name===number?'':' · '+name);
      row.fields.Link.textContent=link.label+(isUplink(port)?' · Uplink':'');
      row.fields.Traffic.textContent=!stale&&Number.isFinite(port.facts?.traffic)?`${port.facts.traffic.toFixed(2)} Mbps · RX + TX`:'Waiting for a sample';
      row.fields.Power.textContent=sfp?'—':portPower(port).description;
      row.fields.Lighting.textContent=state.identify?.port===id?'Identifying':controlSource(state,id,effects).label;
      row.fields.Sample.textContent=stale?'Monitoring interrupted':sample?.sampled_at?new Date(sample.sampled_at).toLocaleTimeString():'Waiting for a sample';
      row.clients.textContent=(port.controller?.clients||[]).map(client=>client.name||'Unnamed device').join(', ');
      const record=history.get(id);row.trend.innerHTML=trendMarkup(record.samples);
      if(row.trend.childElementCount){
        const peak=Math.max(...record.samples.filter(sample=>sample.value!==null).map(sample=>sample.value));
        row.trend.append(node('p','help-text','Last minute · Peak '+peak.toFixed(2)+' Mbps'));
      }
      row.events.replaceChildren(...record.events.map(event=>node('li','',`${new Date(event.at).toLocaleTimeString()} · ${event.text}`)));
      const active=state.identify?.port===id;
      row.identify.textContent=active?`Stop identifying · ${Math.ceil(state.identify.seconds_remaining)}s`:'Identify for 10 seconds';
      row.identify.disabled=pending||stale||state.mode!=='running'||Boolean(state.reconnect_required)||Boolean(state.identify&&!active);
    }
  }
  dialog.append(header,content);document.body.append(dialog);dialog.onclose=()=>{clearInterval(timer);dialog.remove();};const timer=setInterval(paint,1000);paint();dialog.showModal();
  return {update:paint,close:()=>dialog.close(),get active(){return dialog.isConnected;}};
}
