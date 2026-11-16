import { EFFECTS, setEffectCatalog, effectColor, resolvedColorParams, previewPhase, previewColorPhase, dataPreviewFacts, activityPeak, dim, mix, hsvToRgb } from './effect-renderer.mjs';
import { hexToHsv } from '../src/color.mjs';
import { smoothColor, rgbCss } from './port-animation.mjs';
import { getFavoriteEffects, toggleFavoriteEffect, getRecentEffects, recordRecentEffect } from './preferences.mjs';
import {sfpSync, effectiveSfpLight, sfpNativePeriod, sfpNativeAvailable, sfpCandidateSettings, sfpDraftPorts} from './sfp-sync.mjs';
import {hostScheduleDate,receiveScheduleClock} from './schedule-clock.mjs';
import {portDisplayRgb} from './port-display.mjs';
import {lightingPorts, previewGrid, portRanges} from './port-topology.mjs';
import {effectDrafts as pendingEffects, effectDraftForPort, portEffect} from './effect-draft.mjs';
import {node} from './dom.mjs';

const copy = value => structuredClone(value);
const percent = value => Math.max(0, Math.min(100, Number.isFinite(Number(value)) ? Number(value) : 100)) / 100;
const signature = config => JSON.stringify([config?.id, Object.entries(config?.params || {}).sort(([a], [b]) => a.localeCompare(b))]);
const lookup = (collection, key) => collection instanceof Map ? collection.get(key) : collection?.[key];
const selectedSfp = (state, selected) => selected.filter(port => lightingPorts(state).sfp.includes(port));
const selectedCopper = (state, selected) => selected.filter(port => lightingPorts(state).copper.includes(port));
let gallerySequence = 0;

export function orderGalleryCards(grid, cards, category, recent) {
  const ranks = new Map(recent.map((key, index) => [key, index]));
  const ordered = category === 'Recent' ? [...cards].sort((a, b) =>
    (ranks.get(a.key) ?? Infinity) - (ranks.get(b.key) ?? Infinity)) : cards;
  const focused = grid.ownerDocument.activeElement;
  const retainFocus = focused && cards.some(item => !item.card.hidden && item.card.contains(focused));
  ordered.forEach((item, index) => {
    if (grid.children[index] !== item.card) grid.insertBefore(item.card, grid.children[index] || null);
  });
  if (retainFocus && focused.isConnected && grid.ownerDocument.activeElement !== focused) focused.focus({preventScroll:true});
}

export function fitGalleryPreview(preview, ports) {
  if (ports.length <= 10) return;
  const {columns, rows} = previewGrid(ports.length);
  preview.style.display = 'grid';
  preview.style.gridTemplateColumns = `repeat(${columns}, minmax(0, 10px))`;
  preview.style.gap = '4px';
  preview.style.height = preview.style.flexBasis = `${Math.max(67, rows * 18 + 22)}px`;
  for (const port of ports) {port.style.width = '100%';port.style.height = '14px';}
}

export const SFP_EFFECTS = [
  {id:'steady', name:'Steady', description:'Keep a constant color.', category:'Ambient'},
  {id:'pulse', name:'Gentle pulse', description:'Fade the primary color in and out.', category:'Ambient'},
  {id:'two_color', name:'Two-color fade', description:'Blend smoothly between your two colors.', category:'Ambient'},
  {id:'color_cycle', name:'Color cycle', description:'Move smoothly through the color wheel.', category:'Motion'},
];

export function galleryConfig(effect, state = {}, selectedPorts = [], remembered = {}, draft = {}) {
  const defaults = Object.fromEntries(effect.params.map(param => [param.key, param.default]));
  const selected = new Set(selectedPorts), zones = state.zones || [];
  const activeDraft = pendingEffects(draft).findLast(item => item.id === effect.id && item.ports?.some(port => selected.has(port)));
  const current = zones.find(zone => zone.effect?.id === effect.id && zone.ports.some(port => selected.has(port)));
  const chosen = activeDraft || current?.effect || lookup(remembered, effect.id) || zones.find(zone => zone.effect?.id === effect.id)?.effect;
  return {id:effect.id, params:{...defaults, ...copy(chosen?.params || {})}};
}

function effectOwnership(state, id, draft) {
  const optical = state.sfp?.find(light=>light.port===id), port = optical || state.ports?.find(item=>item.port===id);
  const pending=effectDraftForPort(draft,id), effect=portEffect(state,draft,id);
  if (draft.colors?.[id]) return {kind:'manual-preview', effect};
  if ((port?.override || state.overrides?.[id] || port?.source?.kind === 'override') && !draft.returnToEffect?.includes(id) && !pending?.clearOverrides) return {kind:'manual', effect};
  if (pending) return {kind:'preview', effect};
  if (optical?.rule || port?.source?.kind === 'rule') return {kind:'rule',effect};
  return {kind:'effect',effect};
}

export function galleryStatus(effect, state = {}, selected = [], draft = {}, config) {
  const owners = [...selectedCopper(state,selected),...selectedSfp(state,selected)].map(id=>effectOwnership(state,id,draft));
  const assigned = owners.filter(owner => owner.effect?.id === effect.id);
  if (!assigned.length) return {label:'', selected:false};
  const previewing = assigned.filter(owner => owner.kind === 'preview').length;
  if (previewing) return {label:previewing === owners.length ? 'Previewing' : `Previewing on ${previewing} ${previewing===1?'port':'ports'}`, selected:true};
  const applied = assigned.filter(owner => owner.kind === 'effect');
  if (applied.length === owners.length) {
    const configs = new Set(applied.map(owner => signature({...owner.effect, params:{...Object.fromEntries(effect.params.map(param => [param.key,param.default])), ...owner.effect.params}})));
    if (configs.size > 1) return {label:'Mixed settings', selected:false};
    return {label:config && !configs.has(signature(config)) ? 'Applied with other settings' : state.mode && state.mode !== 'running' ? 'Saved effect' : 'Applied', selected:true};
  }
  if (applied.length) return {label:`Applied to ${applied.length} of ${owners.length}`, selected:false};
  const kinds = new Set(assigned.map(owner => owner.kind));
  const label = kinds.size > 1 ? 'Mixed output' : kinds.has('manual-preview') ? 'Fixed color preview' : kinds.has('manual') ? 'Fixed color active' : kinds.has('rule') ? 'Automation active' : '';
  return {label, selected:false};
}

export function sfpPreviewColor(settings, effect, time, master = 100, gamma = 2.2, native = true) {
  const light = {color:'#ffffff', secondary_color:'#006fff', color_brightness:100, secondary_brightness:100, brightness:100, period:6, ...settings};
  if(native&&sfpNativePeriod(effect,light.period)) {
    const primary=dim(light.color,percent(light.color_brightness));
    let anchors;
    if(effect==='pulse') anchors=[primary,[0,0,0]];
    else if(effect==='two_color') anchors=[primary,dim(light.secondary_color,percent(light.secondary_brightness))];
    else {
      const [h,s,v]=hexToHsv(light.color);
      anchors=Array.from({length:6},(_,index)=>dim(hsvToRgb((h+index*60)%360,Math.max(.75,s),v),percent(light.color_brightness)));
    }
    const position=((time/light.period)%1+1)%1*anchors.length,index=Math.floor(position),fraction=position-index;
    const curve=Number.isFinite(gamma)&&gamma>0?gamma:2.2,scale=percent(master)*percent(light.brightness);
    const encode=channel=>Math.round(65280*(channel/255*scale)**curve);
    return anchors[index].map((channel,c)=>255*((encode(channel)*(1-fraction)+encode(anchors[(index+1)%anchors.length][c])*fraction)/65280)**(1/curve));
  }
  const phase = time / Math.max(2, Number(light.period) || 6) % 1, weight = (1 - Math.cos(Math.PI * 2 * phase)) / 2;
  let color = dim(light.color, percent(light.color_brightness));
  if (effect === 'pulse') color = dim(color, 1 - weight);
  else if (effect === 'two_color') color = mix(color, dim(light.secondary_color, percent(light.secondary_brightness)), weight);
  else if (effect === 'color_cycle') {
    const [h,s,v] = hexToHsv(light.color), sector = ((h / 360 + phase) % 1) * 6, fraction = sector % 1;
    color = dim(hsvToRgb((Math.floor(sector) + fraction * fraction * (3 - 2 * fraction)) * 60, Math.max(.75,s),v), percent(light.color_brightness));
  }
  return dim(color, percent(master) * percent(light.brightness));
}

export function sfpGalleryStatus(effect, state = {}, selected = [], draft = {}) {
  const sync=sfpSync(state,draft);
  if(sync.mode==='ethernet')return {label:'',selected:false};
  const ports = selectedSfp(state, selected), lights = ports.map(port => effectiveSfpLight(state,port,draft));
  const matches = light => !portEffect(state,draft,light.port) && light.mode === 'color' && light.effect === effect.id;
  const matching = lights.filter(matches);
  if (!matching.length) return {label:'', selected:false};
  const changed = new Set(sfpDraftPorts(state,draft));
  const staged = ports.filter((port,index) => changed.has(port) && matches(lights[index]));
  if (staged.length) return {label:staged.length === ports.length ? 'Previewing' : `Previewing on ${staged.length} ${staged.length===1?'port':'ports'}`, selected:true};
  const active = matching.filter(light => !light.rule);
  if (!active.length) return {label:'Automation active', selected:false};
  if (active.length !== ports.length) return {label:`Applied to ${active.length} of ${ports.length}`, selected:false};
  const settings = new Set(active.map(({color,secondary_color,brightness,color_brightness,secondary_brightness,period}) => JSON.stringify([color,secondary_color,brightness,color_brightness,secondary_brightness,period])));
  if(settings.size > 1) return {label:'Mixed settings', selected:false};
  return {label:state.mode && state.mode !== 'running' ? 'Saved effect' : 'Applied', selected:true};
}

export function renderEffectGallery(host, context) {
  const galleryId = ++gallerySequence;
  let {effects, state = {}, simulated = true, selectedPorts = [], effectDrafts = {}, draft = {}, galleryState = {}, onChoose, onChooseSfp, onChangeSelection} = context;
  receiveScheduleClock(state.schedule_clock);
  setEffectCatalog(effects);
  host.replaceChildren();
  const page = node('section','effects-gallery'), header = node('header','view-header'), heading = node('div');
  heading.append(node('h1','','Effects'),node('p','muted','Preview an effect, then apply it in Port controls.'));
  header.append(heading);
  const target = node('div','gallery-target'), targetCopy = node('div','gallery-target-copy');
  const targetName = node('strong'), targetNote = node('span','gallery-preview-note');
  targetCopy.append(targetName,targetNote);
  const changeSelection = node('button','button quiet','Change selection'); changeSelection.type='button';
  changeSelection.addEventListener('click',()=>onChangeSelection?.());
  const typeFilters = node('div','gallery-filters gallery-type-filters');typeFilters.setAttribute('role','group');typeFilters.setAttribute('aria-label','Effect library');
  const includeLabel = node('label','check'), includeSfp = node('input');
  includeSfp.type='checkbox';includeSfp.checked=Boolean(selectedSfp(state,selectedPorts).length);
  includeLabel.append(includeSfp,node('span','','Include SFP lights'));
  target.append(targetCopy,typeFilters,includeLabel,changeSelection);
  const toolbar = node('div','gallery-toolbar'), search = node('input','gallery-search');
  search.type='search';search.placeholder='Search effects';search.setAttribute('aria-label','Search effects');search.value=galleryState.search || '';
  const filters = node('div','gallery-filters');filters.setAttribute('role','group');filters.setAttribute('aria-label','Effect category');
  let category = galleryState.category || 'All';
  for(const name of ['All','Favorites','Recent','Ambient','Motion','Activity']) {
    const control=node('button','button quiet',name);control.type='button';control.dataset.category=name;
    control.addEventListener('click',()=>{category=name;galleryState.category=name;galleryState.scroll=0;filter();grid.scrollTop=0;});filters.append(control);
  }
  toolbar.append(search,filters);
  const grid=node('div','gallery-grid');grid.setAttribute('aria-label','Effect previews');
  const empty=node('div','gallery-empty');empty.hidden=true;
  const emptyText=node('p','muted'),emptyAction=node('button','button quiet gallery-empty-action','Show all effects');emptyAction.type='button';
  emptyAction.addEventListener('click',()=>{category='All';search.value='';galleryState.category='All';galleryState.search='';filter();});empty.append(emptyText,emptyAction);
  const notice=node('p','inline-error');notice.setAttribute('role','status');notice.hidden=true;
  const galleryHeading=node('div','gallery-heading');galleryHeading.append(header,target);
  page.append(galleryHeading,toolbar,notice,grid,empty);host.append(page);
  let cards=[],favorites=getFavoriteEffects(),recent=getRecentEffects(),type=galleryState.type || 'ethernet';
  let frame=0,disposed=false,previousTime=0;
  let targetTypes='',configurationKey='';
  const visible=new Set(),started=performance.now(),reducedMotion=matchMedia('(prefers-reduced-motion: reduce)');
  const measureRender=new URLSearchParams(location.search).has('motion-check');
  const observer=new IntersectionObserver(entries=>{for(const entry of entries)entry.isIntersecting?visible.add(entry.target):visible.delete(entry.target);schedule();},{rootMargin:'40px'});
  const scope=()=>{
    const copper=selectedCopper(state,selectedPorts), sfp=selectedSfp(state,selectedPorts);
    if(type==='sfp')return sfp;
    if(!copper.length)return sfp;
    return includeSfp.checked?[...copper,...(sfp.length?sfp:lightingPorts(state).sfp)]:copper;
  };
  includeSfp.addEventListener('change',()=>{updateTarget();buildCards();});
  const master=()=>draft.brightness ?? state.brightness ?? 100;
  const reportError=error=>{notice.textContent=error.message;notice.hidden=false;};
  function statusFor(item){return type==='sfp'?sfpGalleryStatus(item.effect,state,scope(),draft):item.effect.id==='daylight'&&!Number.isFinite(hostScheduleDate(state).getHours())?{label:'Waiting for EE clock',selected:false}:galleryStatus(item.effect,state,scope(),draft,item.config);}
  function updateTarget(){
    const ports=scope(),copper=selectedCopper(state,selectedPorts),sfp=selectedSfp(state,selectedPorts);
    const opticalIds=lightingPorts(state).sfp, opticalCount=ports.filter(port=>opticalIds.includes(port)).length;
    const portType=opticalCount===ports.length?'SFP':!opticalCount?'Ethernet':'';
    const portLabel=ports.length===1?'port':'ports';
    targetName.textContent=ports.length?`${portType?portType+' '+portLabel:portLabel[0].toUpperCase()+portLabel.slice(1)} ${portRanges(ports)}`:'Select ports to preview effects';
    includeLabel.hidden=type==='sfp'||!copper.length||!opticalIds.length;
    const ownership=type==='sfp'?ports.map(port=>state.sfp?.find(light=>light.port===port)?.rule?'rule':'effect'):ports.map(port=>state.ports?.find(item=>item.port===port)?.source?.kind==='rule'?'rule':effectOwnership(state,port,draft).kind);
    const note=ownership.includes('rule')?' · Automation active':ownership.some(kind=>kind.startsWith('manual'))?' · Fixed colors active':'';
    targetNote.textContent=`Effect preview · ${Math.round(master())}% default brightness${note}${type==='sfp'&&sfpSync(state,draft).mode==='ethernet'?' · Choosing an effect replaces Follow Ethernet':''}`;
    changeSelection.textContent=ports.length?'Change selection':'Select ports';
    const nextTypes=JSON.stringify([copper,sfp,type]);
    if(nextTypes===targetTypes)return;
    targetTypes=nextTypes;typeFilters.replaceChildren();typeFilters.hidden=!sfp.length;
    for(const [kind,name,ids] of [['ethernet','Port effects',selectedPorts],['sfp','SFP lighting',sfp]]){
      if(!ids.length)continue;
      const button=node('button',`button quiet ${type===kind?'selected':''}`,name);button.type='button';button.setAttribute('aria-pressed',String(type===kind));
      button.addEventListener('click',()=>{type=kind;galleryState.type=kind;galleryState.scroll=0;buildCards();updateTarget();[...typeFilters.children].find(control=>control.dataset.type===kind)?.focus();});button.dataset.type=kind;typeFilters.append(button);
    }
  }
  function updateCard(item){
    item.config=type==='sfp'?null:galleryConfig(item.effect,state,scope(),effectDrafts,draft);
    const status=statusFor(item);item.card.classList.toggle('selected',status.selected);item.open.setAttribute('aria-pressed',String(status.selected));
    item.badge.textContent=status.label;item.badge.classList.toggle('empty',!status.label);
    const favorite=favorites.includes(item.key);item.favorite.classList.toggle('selected',favorite);item.favorite.setAttribute('aria-pressed',String(favorite));
    item.favorite.setAttribute('aria-label',`${favorite?'Remove':'Add'} ${item.effect.name} ${favorite?'from':'to'} favorites`);
    item.open.disabled=!scope().length || type==='sfp'&&!onChooseSfp;
  }
  function buildCards(){
    observer.disconnect();visible.clear();cards=[];grid.replaceChildren();
    const list=type==='sfp'?SFP_EFFECTS:effects,portCount=Math.max(1,scope().length || (type==='sfp'?2:8));
    for(const effect of list){
      const presentation=type==='sfp'?effect:EFFECTS.find(item=>item.id===effect.id),description=presentation?.description || effect.description;
      const card=node('article','effect-card');card.dataset.effect=effect.id;
      const cardHeader=node('div','effect-card-header'),badge=node('span','effect-card-state');
      badge.id=`effect-status-${galleryId}-${cards.length}`;
      const favorite=node('button','effect-favorite');favorite.type='button';
      favorite.innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9Z"/></svg>';
      cardHeader.append(badge,favorite);
      const open=node('button','effect-card-open');open.type='button';open.setAttribute('aria-label',`${effect.name}. ${description}`);
      open.setAttribute('aria-describedby',badge.id);
      const preview=node('div',`effect-preview${type==='sfp'?' sfp-preview':''}`);preview.setAttribute('aria-hidden','true');
      const ports=Array.from({length:portCount},()=>node('span','effect-preview-port'));preview.append(...ports);fitGalleryPreview(preview,ports);
      const content=node('div','effect-card-copy');content.append(node('h2','',effect.name),node('p','muted',description));open.append(preview,content);card.append(cardHeader,open);
      const key=type==='sfp'?`sfp:${effect.id}`:effect.id;
      const item={card,open,favorite,ports,badge,effect,key,description,category:({Dynamic:'Motion',Data:'Activity'})[presentation?.category] || presentation?.category || 'Ambient',clock:{},colorClock:{},animation:Array.from({length:portCount},()=>({})),colors:[],styles:[]};
      favorite.addEventListener('click',()=>{try{favorites=toggleFavoriteEffect(key);updateCard(item);filter();}catch(error){reportError(error);}});
      open.addEventListener('click',async()=>{
        try{recent=recordRecentEffect(key);}catch(error){reportError(error);}
        try{await(type==='sfp'?onChooseSfp?.({effect:effect.id,ports:scope()}):onChoose?.({...copy(item.config),ports:scope()}));}catch(error){reportError(error);}
      });
      cards.push(item);grid.append(card);updateCard(item);observer.observe(card);
    }
    filter();grid.scrollTop=galleryState.scroll || 0;
  }
  function filter(){
    const query=search.value.trim().toLocaleLowerCase();let count=0;
    for(const control of filters.children){const active=control.dataset.category===category;control.classList.toggle('selected',active);control.setAttribute('aria-pressed',String(active));}
    for(const item of cards){
      const included=category==='All'||category==='Favorites'&&favorites.includes(item.key)||category==='Recent'&&recent.includes(item.key)||item.category===category;
      const matches=included&&`${item.effect.name} ${item.description} ${item.category}`.toLocaleLowerCase().includes(query);
      item.card.hidden=!matches;if(matches)count++;
    }
    orderGalleryCards(grid,cards,category,recent);
    empty.hidden=count!==0;grid.hidden=count===0;
    emptyText.textContent=category==='Favorites'&&!query?'Star an effect to keep it here.':category==='Recent'&&!query?'Effects you choose appear here.':'No effects match your search.';
    schedule();
  }
  function schedule(){if(!disposed&&!document.hidden&&!frame)frame=requestAnimationFrame(animate);}
  function animate(now){
    frame=0;if(disposed||document.hidden)return;
    const renderStarted=measureRender?performance.now():0,elapsed=previousTime?now-previousTime:16;previousTime=now;
    const time=reducedMotion.matches?1.25:(now-started)/1000,shown=cards.filter(item=>!item.card.hidden&&visible.has(item.card)),ids=scope(),count=Math.max(1,ids.length);
    const date=hostScheduleDate(state),hour=date.getHours()+date.getMinutes()/60+date.getSeconds()/3600;
    const traffic=shown.some(item=>item.category==='Activity')?(simulated?dataPreviewFacts(count,time):ids.map(id=>(state.ports?.find(port=>port.port===id)||state.sfp?.find(port=>port.port===id))?.facts||{})):[],brightness=percent(master());
    for(const item of shown){
      const phase=type==='sfp'?0:previewPhase(item.clock,item.config,time),colorPhase=type==='sfp'?0:previewColorPhase(item.colorClock,item.config,time);
      const resolvedParams=type==='sfp'?null:resolvedColorParams(item.config,time,colorPhase);
      const peakTraffic=item.category==='Activity'?activityPeak(traffic,item.config?.params?.include_uplinks):0;
      for(let index=0;index<item.ports.length;index++){
        const port=ids[index], optical=type==='sfp'?null:state.sfp?.find(light=>light.port===port);
        const portBrightness=brightness*(optical?percent(draft.sfp?.[port]?.brightness??optical.brightness):1);
        const target=type==='sfp'?sfpPreviewColor(sfpCandidateSettings(state,port,item.effect.id,draft),item.effect.id,time,master(),state.output?.gamma,sfpNativeAvailable(state)):item.effect.id==='daylight'&&!Number.isFinite(hour)?portDisplayRgb(state.ports?.find(item=>item.port===port)||state.sfp?.find(item=>item.port===port)||{},state.output?.gamma):dim(effectColor(item.config,index,item.ports.length,time,{...traffic[index],hour,phase,colorPhase,resolvedParams,peakTraffic,animationState:item.animation[index]}),portBrightness);
        const color=smoothColor(reducedMotion.matches?undefined:item.colors[index],target,elapsed),css=rgbCss(color);
        if(item.styles[index]!==css){item.ports[index].style.backgroundColor=css;item.ports[index].style.color=css;item.styles[index]=css;}item.colors[index]=color;
      }
    }
    if(measureRender)document.dispatchEvent(new CustomEvent('ee-render-sample',{detail:{kind:'Effects',ms:performance.now()-renderStarted,ports:shown.reduce((sum,item)=>sum+item.ports.length,0)}}));
    if(!reducedMotion.matches&&shown.length)schedule();
  }
  search.addEventListener('input',()=>{galleryState.search=search.value;galleryState.scroll=0;filter();grid.scrollTop=0;});
  grid.addEventListener('scroll',()=>{galleryState.scroll=grid.scrollTop;},{passive:true});
  const synchronize=()=>{cancelAnimationFrame(frame);frame=0;previousTime=0;schedule();};
  document.addEventListener('visibilitychange',synchronize);reducedMotion.addEventListener('change',synchronize);
  if(type==='sfp'&&!selectedSfp(state,selectedPorts).length&&selectedCopper(state,selectedPorts).length)type='ethernet';
  updateTarget();buildCards();schedule();
  const cleanup=()=>{galleryState.search=search.value;galleryState.category=category;galleryState.type=type;galleryState.scroll=grid.scrollTop;disposed=true;cancelAnimationFrame(frame);observer.disconnect();document.removeEventListener('visibilitychange',synchronize);reducedMotion.removeEventListener('change',synchronize);};
  cleanup.update=next=>{
    receiveScheduleClock(next.state?.schedule_clock);
    const previousScope=scope().join(','), previousSelection=selectedPorts.join(',');state=next.state ?? state;selectedPorts=next.selectedPorts ?? selectedPorts;effectDrafts=next.effectDrafts ?? effectDrafts;draft=next.draft ?? draft;
    if(selectedPorts.join(',')!==previousSelection)includeSfp.checked=Boolean(selectedSfp(state,selectedPorts).length);
    const key=JSON.stringify([Number.isFinite(hostScheduleDate(state).getHours()),selectedPorts,draft,effectDrafts instanceof Map?[...effectDrafts]:effectDrafts,state.zones,state.overrides,state.brightness,state.mode,state.sfp_sync,state.ports?.map(port=>[port.port,port.source,port.override]),state.sfp?.map(({port,mode,effect,color,secondary_color,brightness,color_brightness,secondary_brightness,period,rule})=>({port,mode,effect,color,secondary_color,brightness,color_brightness,secondary_brightness,period,rule}))]);
    if(key===configurationKey)return;configurationKey=key;
    if(type==='sfp'&&!selectedSfp(state,selectedPorts).length&&selectedCopper(state,selectedPorts).length)type='ethernet';
    updateTarget();if(previousScope!==scope().join(','))buildCards();else for(const item of cards)updateCard(item);schedule();
  };
  return cleanup;
}
