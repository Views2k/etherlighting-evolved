import {lightingSnapshot,sfpSettings} from './lighting-state.mjs';
import {effectDraftForPort,portEffect} from './effect-draft.mjs';
import {sfpSync,sfpDraftPorts,effectiveSfpLight,sfpNativeAvailable} from './sfp-sync.mjs';
import {hostScheduleDate} from './schedule-clock.mjs';
import {masterDisplayRgb,manualDisplayRgb,portDisplayRgb} from './port-display.mjs';
import {defaultConfig,previewPhase,previewColorPhase,resolvedColorParams,activityPeak,effectColor,dim} from './effect-renderer.mjs';
import {matchingRule,matchingSfpRule,renderLook} from '../src/rules.mjs';
import {sfpPreviewColor} from './effect-gallery.mjs';
const number=(value,fallback=0)=>Number.isFinite(Number(value))?Number(value):fallback;
export function previewDraftColors(state,draft,time,{previewClocks=new Map(),previewStates=new Map()}={}){
  const result={}, master=number(draft.brightness??state.brightness,100)/100,date=hostScheduleDate(state),hour=date.getHours()+date.getMinutes()/60+date.getSeconds()/3600;
  if(draft.brightness!==undefined){
    const previous=number(state.brightness,100);
    for(const port of [...(state.ports||[]),...(state.sfp||[])])result[port.port]=masterDisplayRgb(port,master*100,previous,state.rules,state.output?.gamma,date);
  }
  const render=(config,ports,targets=new Set(ports))=>{
    const normalized={...config,params:{...defaultConfig(config.id).params,...config.params}};
    const key=config.id+':'+ports.join(',');if(!previewClocks.has(key))previewClocks.set(key,{motion:{},color:{}});
    const clocks=previewClocks.get(key),phase=previewPhase(clocks.motion,normalized,time), colorPhase=previewColorPhase(clocks.color,normalized,time);
    const resolvedParams=resolvedColorParams(normalized,time,colorPhase);
    const telemetry=ports.map(id=>state.ports?.find(p=>p.port===id)||state.sfp?.find(p=>p.port===id));
    const peakTraffic=activityPeak(telemetry.map(port=>port?.facts||{}),normalized.params.include_uplinks);
    ports.forEach((port,index)=>{
      if(!targets.has(port))return;
      if(state.overrides?.[port]&&!draft.returnToEffect?.includes(port)&&!effectDraftForPort(draft,port)?.clearOverrides){const live=state.ports?.find(p=>p.port===port)||state.sfp?.find(p=>p.port===port);result[port]=masterDisplayRgb(live,master*100,number(state.brightness,100),state.rules,state.output?.gamma,date);return;}
      const live=telemetry[index]||{}, optical=state.sfp?.find(light=>light.port===port);
      if(normalized.id==='daylight'&&!Number.isFinite(hour)){result[port]??=portDisplayRgb(live,state.output?.gamma);return;}
      const facts=live.facts||{};
      const rule=optical?matchingSfpRule(state.rules||[],{...optical,mode:'color'},date):matchingRule(state.rules||[],{id:port,facts},date);
      if(!previewStates.has(port))previewStates.set(port,{});
      const color=effectColor(normalized,index,ports.length,time,{...facts,hour,phase,colorPhase,resolvedParams,peakTraffic,animationState:previewStates.get(port)});
      const level=(rule?.look.brightness??master*100)/100, opticalLevel=optical?(draft.sfp?.[port]?.brightness??optical.brightness??100)/100:1;
      result[port]=dim(rule?renderLook(rule.look,color,time):color,level*opticalLevel);
    });
  };
  const projected=lightingSnapshot({
    zones:state.zones||[], rules:state.rules||[], overrides:state.overrides||{},
    brightness:state.brightness, sfp:Object.fromEntries((state.sfp||[]).map(light=>[light.port,sfpSettings(light)])),
    sfp_sync:state.sfp_sync
  },state,draft);
  const returned=new Set(draft.returnToEffect||[]);
  for(const zone of projected.zones){
    const prior=state.zones?.find(item=>item.id===zone.id);
    const changed=!prior||JSON.stringify(prior.ports)!==JSON.stringify(zone.ports)
      ||JSON.stringify(prior.effect)!==JSON.stringify(zone.effect);
    const targets=changed?new Set(zone.ports):new Set(zone.ports.filter(port=>returned.has(port)||effectDraftForPort(draft,port)));
    if(targets.size)render(zone.effect,zone.ports,targets);
  }
  for(const [port,color] of Object.entries(draft.colors||{}))result[port]=manualDisplayRgb(color.color,color.brightness,master*100);
  const sync=sfpSync(state,draft),sfpPorts=[...new Set([...sfpDraftPorts(state,draft),...(draft.brightness!==undefined?(state.sfp||[]).filter(light=>!portEffect(state,draft,light.port)).map(light=>light.port):[])])];
  for(const zone of draft.brightness!==undefined||sfpPorts.length?projected.zones:[]){
    const targets=new Set(zone.ports.filter(id=>(draft.brightness!==undefined||sfpPorts.includes(id))&&state.sfp?.some(port=>port.port===id)&&!draft.sfpDetached?.includes(id)&&!effectDraftForPort(draft,id)));
    if(targets.size)render(zone.effect,zone.ports,targets);
  }
  for(const port of sfpPorts){
    if(portEffect(state,draft,port))continue;
    const light=effectiveSfpLight(state,port,draft);
    const rule=matchingSfpRule(state.rules||[],{...light,mode:sync.mode==='ethernet'?'color':light.mode},date);
    if(rule){result[port]=masterDisplayRgb({...state.sfp.find(p=>p.port===port),mode:'color'},master*100,number(state.brightness,100),state.rules,state.output?.gamma,date);continue;}
    if(sync.mode==='ethernet'){
      const source=state.ports?.find(p=>p.port===sync.source_port);
      result[port]=result[sync.source_port]||portDisplayRgb(source||{},state.output?.gamma);continue;
    }
    if(light.mode!=='color'){result[port]=matchingSfpRule(state.rules||[],light,date)?portDisplayRgb(light,state.output?.gamma):[0,0,0];continue;}
    result[port]=sfpPreviewColor(light,light.effect,time,master*100,state.output?.gamma,sfpNativeAvailable(state));
  }
  return result;
}
