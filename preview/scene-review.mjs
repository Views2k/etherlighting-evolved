import {sceneFingerprint} from './secondary-model.mjs';
import {lightingSnapshot,sfpSettings} from './lighting-state.mjs';
const clone=value=>value===undefined?undefined:structuredClone(value);
const canonical=value=>Array.isArray(value)?value.map(canonical):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])])):value;
const same=(a,b)=>JSON.stringify(canonical(a))===JSON.stringify(canonical(b));
export function sceneFromState(state){
  return lightingSnapshot({version:3,topology:state.topology,zones:state.zones||[],rules:state.rules||[],overrides:state.overrides||{},
    brightness:state.brightness,sfp:Object.fromEntries((state.sfp||[]).map(light=>[light.port,sfpSettings(light)])),sfp_sync:state.sfp_sync},state,{});
}
const fields=scene=>({...scene,zones:Object.fromEntries((scene.zones||[]).map(zone=>[zone.id,zone]))});
export function compareScenes(base,current,desired,{preferDraft=false}={}){
  const conflicts=[],changes=[];
  function merge(a,b,c,path){
    if(same(a,c))return clone(b);
    if(same(b,c))return clone(c);
    const membership=value=>Object.fromEntries(Object.entries(value||{}).map(([id,zone])=>[id,zone.ports]));
    const groupConflict=path.length===1&&path[0]==='zones'&&!same(membership(a),membership(b))&&!same(membership(a),membership(c));
    const effectConflict=path.at(-1)==='effect'&&a&&b&&c&&(a.id!==b.id||a.id!==c.id);
    const objects=!groupConflict&&!effectConflict&&[a,b,c].every(v=>v&&typeof v==='object'&&!Array.isArray(v));
    if(objects){
      const result={};for(const key of new Set([...Object.keys(a),...Object.keys(b),...Object.keys(c)])){
        const value=merge(a[key],b[key],c[key],[...path,key]);if(value!==undefined)Object.defineProperty(result,key,{value,enumerable:true,writable:true,configurable:true});
      }return result;
    }
    const change={path:path.join('.'),before:clone(a),current:clone(b),draft:clone(c)};
    changes.push(change);
    if(!same(a,b)){conflicts.push(change);return clone(preferDraft?c:b);}
    return clone(c);
  }
  const scene=merge(fields(base),fields(current),fields(desired),[]);
  scene.zones=Object.values(scene.zones||{});
  const ports=scene.zones.flatMap(zone=>zone.ports||[]);
  if(new Set(ports).size!==ports.length){
    scene.zones=clone(preferDraft?desired.zones:current.zones);
    for(let index=conflicts.length-1;index>=0;index--)if(conflicts[index].path.startsWith('zones'))conflicts.splice(index,1);
    conflicts.push({path:'zones',before:base.zones,current:current.zones,draft:desired.zones});
  }
  return {scene,changes,conflicts,changed:sceneFingerprint(scene)!==sceneFingerprint(current)};
}
const describe=change=>{
  const path=change.path.replace(/^zones$/,'Port groups').replace(/^brightness$/,'Default brightness').replace(/^overrides\.(\d+)/,'Port $1 fixed color')
    .replace(/^sfp\.(\d+)/,'SFP $1').replaceAll('.',' · ').replaceAll('_',' ');
  const show=value=>{
    if(value===undefined)return 'removed';
    if(value===null)return 'inherited';
    if(Array.isArray(value))return value.map(show).join(', ')||'none';
    if(typeof value==='object'){
      if(value.ports)return (value.name||'Port group')+' ('+value.ports.join(', ')+')'+(value.effect?' · '+show(value.effect):'');
      if(value.id&&value.params)return value.id.replaceAll('_',' ')+' · '+show(value.params);
      if(value.name)return value.name;
      return Object.entries(value).map(([key,item])=>key.replaceAll('_',' ')+' '+show(item)).join(', ');
    }return String(value);
  };
  return `${path}: current ${show(change.current)}; preview ${show(change.draft)}`;
};
export function reviewSceneConflict(base,current,desired){
  const review=compareScenes(base,current,desired),remote=compareScenes(base,base,current).changes;
  return new Promise(resolve=>{
    const dialog=document.createElement('dialog');dialog.className='dialog scene-conflict';dialog.setAttribute('aria-label','Review lighting changes');
    const heading=document.createElement('h2');heading.textContent='Review lighting changes';
    const message=document.createElement('p');message.className='muted';message.textContent=review.conflicts.length?'Both clients changed these settings. Choose which values to keep.':'Another client changed the setup. Your preview can be applied while keeping its other changes.';
    const latest=document.createElement('p');latest.textContent='Changes from another client';const remoteList=document.createElement('ul');for(const change of remote){const li=document.createElement('li');li.textContent=describe({...change,current:change.before}).replace('current ','previous ').replace('preview ','now ');remoteList.append(li);}
    const list=document.createElement('ul');for(const change of review.changes){const li=document.createElement('li');li.textContent=describe(change);list.append(li);}
    const actions=document.createElement('div');actions.className='view-actions';
    let finished=false;const finish=value=>{if(finished)return;finished=true;dialog.close();dialog.remove();resolve(value);};
    dialog.addEventListener('close',()=>finish(null),{once:true});
    const add=(text,fn)=>{const button=document.createElement('button');button.className='button';button.textContent=text;button.onclick=fn;actions.append(button);};
    add('Cancel',()=>finish(null));
    if(review.conflicts.length)add('Keep current overlapping settings',()=>finish(review.scene));
    add(review.conflicts.length?'Use my preview settings':'Apply my preview',()=>finish(compareScenes(base,current,desired,{preferDraft:true}).scene));
    dialog.append(heading,message,latest,remoteList,list,actions);document.body.append(dialog);dialog.addEventListener('cancel',event=>{event.preventDefault();finish(null);});dialog.showModal();
  });
}
