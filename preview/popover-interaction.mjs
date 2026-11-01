export function bindPopoverDismissal(popup, anchor, close) {
  const document=popup.ownerDocument,pointers=new Set();
  let disposed=false,focusTimer,keyboardNavigation=false;
  const inside=target=>target===anchor||Boolean(target&&popup.contains(target));
  const cancelFocusCheck=()=>{clearTimeout(focusTimer);focusTimer=undefined;};
  const pointerDown=event=>{
    keyboardNavigation=false;
    const path=event.composedPath?.()||[];
    if(inside(event.target)||path.includes(popup)||path.includes(anchor)){
      pointers.add(event.pointerId);cancelFocusCheck();
    }else if(!pointers.size)close(false);
  };
  const pointerEnd=event=>{pointers.delete(event.pointerId);cancelFocusCheck();};
  const focusOut=event=>{
    if(!keyboardNavigation||inside(event.relatedTarget))return;
    cancelFocusCheck();
    focusTimer=setTimeout(()=>{
      const target=document.activeElement;
      if(!disposed&&keyboardNavigation&&!pointers.size&&target&&target!==document.body&&target!==document.documentElement&&!inside(target))close(false);
    },0);
  };
  const keyDown=event=>{
    if(event.key==='Tab'){keyboardNavigation=true;return;}
    if(event.key!=='Escape')return;
    event.preventDefault();event.stopPropagation();close(true);
  };
  document.addEventListener('pointerdown',pointerDown,true);
  for(const name of ['pointerup','pointercancel','lostpointercapture'])document.addEventListener(name,pointerEnd,true);
  document.addEventListener('keydown',keyDown,true);popup.addEventListener('focusout',focusOut);
  return ()=>{
    if(disposed)return;disposed=true;cancelFocusCheck();pointers.clear();
    document.removeEventListener('pointerdown',pointerDown,true);
    for(const name of ['pointerup','pointercancel','lostpointercapture'])document.removeEventListener(name,pointerEnd,true);
    document.removeEventListener('keydown',keyDown,true);popup.removeEventListener('focusout',focusOut);
  };
}
