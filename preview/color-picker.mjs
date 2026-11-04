import {hexToHsv,hsvToHex,hexToRgb,rgbToHex,normalizeHex} from '../src/color.mjs';
import {mountColorLibrary} from './color-library.mjs';
import {bindPopoverDismissal} from './popover-interaction.mjs';

export function openColorPicker(anchor, {color = anchor.value, onChange, onClose, pointerType, onError = () => {}, onRangeChange = () => {}} = {}) {
  let [h,s,v]=hexToHsv(color),dismissal;
  const modal=pointerType==='touch'||pointerType==='pen'||window.matchMedia?.('(pointer: coarse), (min-width: 900px) and (max-height: 500px)')?.matches;
  const popup=document.createElement(modal?'dialog':'div'); popup.className='color-popover popover'; popup.setAttribute('role','dialog');popup.setAttribute('aria-label','Choose color');
  popup.innerHTML=`<div class="color-sv" tabindex="0" role="slider" aria-label="Saturation and value. Arrow keys adjust color." aria-valuemin="0" aria-valuemax="100"><span class="color-sv-cursor"></span></div><label class="sr-only" for="popup-hue">Hue</label><input class="color-hue" id="popup-hue" type="range" min="0" max="359" step="1"><div class="color-rgb">${['R','G','B'].map((key,index)=>`<label>${key}<input type="number" min="0" max="255" step="1" data-rgb="${index}" aria-label="${['Red','Green','Blue'][index]}"></label>`).join('')}</div><div class="color-popup-footer"><label class="sr-only" for="popup-hex">Hex color</label><input id="popup-hex" class="hex-input" maxlength="7" spellcheck="false" aria-label="Hex color"><button class="button quiet" type="button">Done</button></div>`;
  document.body.append(popup);
  const library=document.createElement('div');library.className='color-library';popup.append(library);
  if(modal){
    const footer=popup.querySelector('.color-popup-footer');
    const controls=document.createElement('div');controls.className='picker-scroll';
    controls.append(...[...popup.children].filter(child=>child!==footer));
    popup.prepend(controls);
    popup.showModal();
  }
  const colorLibrary=mountColorLibrary(library,{getColor:()=>hsvToHex(h,s,v),onChoose:color=>{[h,s,v]=hexToHsv(color);update();},onError});
  const sv=popup.querySelector('.color-sv'),cursor=popup.querySelector('.color-sv-cursor'),hue=popup.querySelector('.color-hue'),field=popup.querySelector('#popup-hex');
  const position=()=>{
    if(modal)return;
    const rect=anchor.getBoundingClientRect(),size=popup.getBoundingClientRect();
    popup.style.position='fixed';popup.style.zIndex='1000';
    popup.style.left=Math.max(12,Math.min(rect.left,innerWidth-size.width-12))+'px';
    popup.style.top=Math.max(12,rect.top-size.height-10>=12?rect.top-size.height-10:Math.min(rect.bottom+10,innerHeight-size.height-12))+'px';
  };
  const resizeObserver=new ResizeObserver(position);resizeObserver.observe(popup);
  const reposition=event=>{if(!popup.contains(event.target))position();};
  const update=(emit=true)=>{
    const color=hsvToHex(h,s,v);sv.style.setProperty('--hue-color',hsvToHex(h,1,1));cursor.style.left=`${s*100}%`;cursor.style.top=`${(1-v)*100}%`;
    sv.setAttribute('aria-valuenow',Math.round(s*100));sv.setAttribute('aria-valuetext',`${Math.round(s*100)}% saturation, ${Math.round(v*100)}% value`);hue.value=h;
    if(emit||document.activeElement!==field){field.value=color;field.setCustomValidity('');}
    hexToRgb(color).forEach((value,index)=>{const input=popup.querySelector(`[data-rgb="${index}"]`);if(input!==document.activeElement)input.value=value;});
    if(emit){if(onChange)onChange(color);else{anchor.value=color;anchor.dispatchEvent(new Event('input',{bubbles:true}));}}
    onRangeChange();colorLibrary?.update?.();
  };
  const close=(restore=false)=>{
    if(!popup.isConnected)return;
    dismissal?.();window.removeEventListener('resize',position);window.removeEventListener('scroll',reposition,true);resizeObserver.disconnect();colorLibrary?.();if(modal)popup.close();popup.remove();onClose?.();if(restore&&anchor.isConnected)anchor.focus();
  };
  const point=event=>{const rect=sv.getBoundingClientRect();s=Math.max(0,Math.min(1,(event.clientX-rect.left)/rect.width));v=1-Math.max(0,Math.min(1,(event.clientY-rect.top)/rect.height));update();};
  let activePointer=null;
  const endPointer=event=>{
    if(event.pointerId!==activePointer)return;
    activePointer=null;
    if(sv.hasPointerCapture(event.pointerId))sv.releasePointerCapture(event.pointerId);
  };
  sv.addEventListener('pointerdown',event=>{
    if(event.button!==0||activePointer!==null)return;
    event.preventDefault();activePointer=event.pointerId;sv.focus();sv.setPointerCapture(event.pointerId);point(event);
  });
  sv.addEventListener('pointermove',event=>{if(event.pointerId===activePointer&&sv.hasPointerCapture(event.pointerId))point(event);});
  for(const name of ['pointerup','pointercancel','lostpointercapture'])sv.addEventListener(name,endPointer);
  sv.addEventListener('keydown',event=>{const step=event.shiftKey ? 0.1 : 0.01;if(event.key==='ArrowLeft')s=Math.max(0,s-step);else if(event.key==='ArrowRight')s=Math.min(1,s+step);else if(event.key==='ArrowUp')v=Math.min(1,v+step);else if(event.key==='ArrowDown')v=Math.max(0,v-step);else return;event.preventDefault();update();});
  hue.addEventListener('input',()=>{h=Number(hue.value);update();});
  hue.style.touchAction='none';
  popup.querySelectorAll('[data-rgb]').forEach(input=>input.addEventListener('change',()=>{if(!input.checkValidity()){input.reportValidity();return;}const values=[...popup.querySelectorAll('[data-rgb]')].map(field=>Number(field.value));[h,s,v]=hexToHsv(rgbToHex(values));update();}));
  field.addEventListener('change',()=>{const color=normalizeHex(field.value);if(!color){field.setCustomValidity('Enter a hex color.');field.reportValidity();return;}field.setCustomValidity('');[h,s,v]=hexToHsv(color);update();field.value=color;});
  popup.querySelector('.color-popup-footer button').addEventListener('click',()=>{if(!field.checkValidity()){field.reportValidity();return;}close(true);});
  if(modal)popup.addEventListener('cancel',event=>{event.preventDefault();close(true);});
  else dismissal=bindPopoverDismissal(popup,anchor,close);
  window.addEventListener('resize',position);window.addEventListener('scroll',reposition,true);
  update(false);position();sv.focus();return close;
}
