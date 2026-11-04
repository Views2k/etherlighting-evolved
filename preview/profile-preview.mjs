import {defaultConfig, effectColor, resolvedColorParams, previewPhase, previewColorPhase, activityPeak, dim} from './effect-renderer.mjs';
import {matchingRule, matchingSfpRule, renderLook} from '../src/rules.mjs';
import {effectiveSfpLight, sfpLight, sfpSync, sfpNativeAvailable} from './sfp-sync.mjs';
import {sfpPreviewColor} from './effect-gallery.mjs';
import {portDisplayRgb} from './port-display.mjs';
import {ColorFrames, smoothColor, rgbCss} from './port-animation.mjs';
import {hostScheduleDate,receiveScheduleClock} from './schedule-clock.mjs';
import {lightingPorts, portIds, sceneTopology} from './port-topology.mjs';

const percent = value => Math.max(0, Math.min(100, Number.isFinite(Number(value)) ? Number(value) : 100)) / 100;
const lights = scene => Array.isArray(scene.sfp) ? scene.sfp : Object.entries(scene.sfp || {}).map(([id, light]) => ({...light, port:Number(id)}));

export function profilePorts(scene, state = {}) {
  const topology = sceneTopology(scene);
  if (topology) return topology;
  if (scene.version >= 3) return {copper:[], sfp:[]};
  const sfp = portIds(lights(scene).map(light => light.port));
  const known = portIds([...(Array.isArray(scene.zones) ? scene.zones : []).flatMap(zone => zone?.ports || []), ...Object.keys(scene.overrides || {}).map(Number)]);
  const count = state.num_ports ?? state.switch?.num_ports;
  const overlaps = sfp.some(id => lightingPorts(state).copper.includes(id));
  const extent = !overlaps && Number.isInteger(count) && count >= 0 && count <= 256 ? count : Math.max(8, ...known);
  return {copper:Array.from({length:extent},(_,index) => index+1).filter(id => !sfp.includes(id)), sfp};
}

export function profileLayoutNotice(scene, state = {}) {
  const version=scene.version??(Object.hasOwn(scene,'zones')?2:1),current=lightingPorts(state);
  const invalid='Review this profile’s saved port layout before applying it.';
  const legacy='This older profile uses a different port layout. Review its port assignments before applying it.';
  if(!Number.isInteger(version)||![1,2,3].includes(version))return invalid;
  if(version===3){
    const saved=sceneTopology(scene),topology=scene.topology,currentModel=state.topology?.model??state.hardware_profile?.id??null;
    if(!saved||Object.keys(topology).sort().join(',')!=='copper_ports,model,sfp_ports,version'
      ||topology.model!==null&&(typeof topology.model!=='string'||!topology.model.trim())
      ||[...saved.copper,...saved.sfp].some(id=>id>64))return invalid;
    if(JSON.stringify(saved.copper)!==JSON.stringify(current.copper)||JSON.stringify(saved.sfp)!==JSON.stringify(current.sfp))
      return `This profile uses ${saved.copper.length} Ethernet and ${saved.sfp.length} SFP ports. Choose a profile for this switch’s ${current.copper.length} Ethernet and ${current.sfp.length} SFP ports.`;
    if(topology.model!==null&&currentModel!==null&&topology.model!==currentModel)
      return 'This profile was saved for a different switch model. Choose a profile for the connected switch.';
  }else{
    if(Object.hasOwn(scene,'topology'))return invalid;
    if(version===2&&(JSON.stringify(current.copper)!=='[1,2,3,4,5,6,7,8]'||JSON.stringify(current.sfp)!=='[9,10]'))return legacy;
    if(version===1&&['zones','sfp','sfp_sync'].some(key=>Object.hasOwn(scene,key)))return invalid;
    if(version===1&&current.copper.length!==8&&(Object.keys(scene.overrides||{}).length||scene.rules?.length))return legacy;
  }
  return '';
}

export function profileColors(scene, state, time, animation = new Map(), date = hostScheduleDate(state)) {
  const colors = {}, master = scene.brightness ?? 100, ports = profilePorts(scene,state);
  const copper = new Map((state.ports || []).map(port => [port.port,port]));
  const optical = new Map((state.sfp || []).map(port => [port.port,port]));
  const opticalIds = new Set(ports.sfp);
  const zoneColors = new Map();
  const composed = new Map();
  for (const zone of Array.isArray(scene.zones) ? scene.zones : []) {
    if (!Array.isArray(zone?.ports) || !zone.effect?.id) continue;
    const defaults=defaultConfig(zone.effect.id), config={...defaults,...zone.effect,params:{...defaults.params,...zone.effect.params}};
    const key=JSON.stringify([zone.id,zone.ports,config]);
    if (!animation.has(key)) animation.set(key,{motion:{},colors:{},ports:new Map()});
    const clock=animation.get(key), phase=previewPhase(clock.motion,config,time), colorPhase=previewColorPhase(clock.colors,config,time);
    const resolvedParams=resolvedColorParams(config,time,colorPhase);
    const facts=zone.ports.map(id => (opticalIds.has(id) ? optical : copper).get(id)?.facts || {}), peakTraffic=activityPeak(facts,config.params.include_uplinks);
    zone.ports.forEach((id,index) => {
      if (!clock.ports.has(id)) clock.ports.set(id,{});
      const hour=date.getHours()+date.getMinutes()/60+date.getSeconds()/3600;
      zoneColors.set(id,config.id==='daylight'&&!Number.isFinite(hour)?[0,0,0]:effectColor(config,index,zone.ports.length,time,{...facts[index],phase,colorPhase,resolvedParams,peakTraffic,animationState:clock.ports.get(id),hour}));
    });
  }
  for (const id of ports.copper) {
    const output={color:zoneColors.get(id) ?? [0,0,0],brightness:master};
    const facts=copper.get(id)?.facts || {};
    const rule=matchingRule(scene.rules || [],{id,facts},date);
    if (rule) {output.color=renderLook(rule.look,output.color,time);output.brightness=rule.look.brightness ?? output.brightness;}
    const override=scene.overrides?.[id];
    if (override) {output.color=override.color;output.brightness=override.brightness ?? output.brightness;}
    composed.set(id,output);
    colors[id]=dim(output.color,percent(output.brightness));
  }
  const sync=sfpSync(scene);
  for (const id of ports.sfp) {
    const member=zoneColors.has(id);
    const settings=member ? sfpLight(scene,id) : effectiveSfpLight(scene,id);
    const telemetry=optical.get(id) || {};
    const follows=!member && sync.mode==='ethernet',source=composed.get(sync.source_port);
    const light={...telemetry,...settings,mode:member || follows ? 'color' : settings.mode};
    const rule=matchingSfpRule(scene.rules || [],light,date);
    const brightness=rule?.look.brightness ?? (follows ? source?.brightness ?? master : master);
    if (member || follows || (rule && rule.look.style!=='effect')) {
      const base=member ? zoneColors.get(id) : source?.color || [0,0,0];
      const color=rule ? renderLook(rule.look,base,time) : base;
      colors[id]=dim(color,percent(brightness)*(follows ? 1 : percent(light.brightness)));
    } else colors[id]=light.mode==='color'
      ? sfpPreviewColor(light,light.effect || 'steady',time,brightness,state.output?.gamma,sfpNativeAvailable(state))
      : light.mode==='off' ? [0,0,0] : portDisplayRgb(telemetry,state.output?.gamma);
  }
  return colors;
}

export function createProfilePreviews(initialState, environment = globalThis) {
  receiveScheduleClock(initialState?.schedule_clock);
  let state=initialState, frame=0, disposed=false, previous=0;
  const items=new Set(), visible=new Set(), frames=new ColorFrames(), started=environment.performance.now();
  const motion=environment.matchMedia('(prefers-reduced-motion: reduce)');
  const observer=new environment.IntersectionObserver(entries => {
    for (const entry of entries) entry.isIntersecting ? visible.add(entry.target) : visible.delete(entry.target);
    schedule();
  },{rootMargin:'40px'});
  function schedule() {if (!disposed && !environment.document.hidden && !frame) frame=environment.requestAnimationFrame(draw);}
  function push() {
    frames.push(Object.fromEntries([...(state.ports || []),...(state.sfp || [])].map(port => [port.port,portDisplayRgb(port,state.output?.gamma)])),environment.performance.now());
    schedule();
  }
  function syncPorts(item) {
    const ports = item.active ? lightingPorts(state) : profilePorts(item.scene,state), signature=JSON.stringify(ports);
    if (item.topology === signature) return;
    item.topology=signature;item.bars.clear();item.colors.clear();item.styles.clear();
    item.element.replaceChildren();
    for (const id of [...ports.copper,...ports.sfp]) {
      const bar=environment.document.createElement('i');if (ports.sfp.includes(id)) bar.className='sfp-preview';
      item.bars.set(id,bar);item.element.append(bar);
    }
  }
  function draw(now) {
    frame=0;if (disposed || environment.document.hidden) return;
    const elapsed=previous ? now-previous : 16;previous=now;
    const time=motion.matches ? 1.25 : (now-started)/1000, live=motion.matches ? frames.latest() : frames.sample(now);
    let painted=false;
    for (const item of items) {
      if (!visible.has(item.element)) continue;
      const colors=item.active ? live : profileColors(item.scene,state,time,item.animation);
      for (const [id,bar] of item.bars) {
        const target=colors[id] || [0,0,0], color=motion.matches ? target : smoothColor(item.colors.get(id),target,elapsed), css=rgbCss(color);
        if (item.styles.get(id)!==css) {bar.style.backgroundColor=css;item.styles.set(id,css);}
        item.colors.set(id,color);
      }
      painted=true;
    }
    if (!motion.matches && painted) schedule();
  }
  const synchronize=()=>{environment.cancelAnimationFrame(frame);frame=0;previous=0;schedule();};
  environment.document.addEventListener('visibilitychange',synchronize);motion.addEventListener('change',synchronize);
  push();
  return {
    add(scene, active=false) {
      const element=environment.document.createElement('span');element.className='profile-preview';element.setAttribute('aria-hidden','true');
      const item={element,scene,active,bars:new Map(),colors:new Map(),styles:new Map(),animation:new Map()};
      syncPorts(item);
      items.add(item);observer.observe(element);schedule();return element;
    },
    update(next) {receiveScheduleClock(next.schedule_clock);state=next;for (const item of items) syncPorts(item);push();},
    clear() {observer.disconnect();items.clear();visible.clear();},
    dispose() {disposed=true;environment.cancelAnimationFrame(frame);observer.disconnect();items.clear();visible.clear();environment.document.removeEventListener('visibilitychange',synchronize);motion.removeEventListener('change',synchronize);}
  };
}
