import {THEMES,DEFAULT_THEME,validateThemeId} from './theme-catalog.mjs';
export const THEME_STORAGE_KEY='ee.theme.v1';

export function createThemeController({storage,root,events,onApply=()=>{}}){
  const subscribers=new Set();
  let current=DEFAULT_THEME;
  const normalize=id=>validateThemeId(id)?id:DEFAULT_THEME;
  const apply=id=>{
    current=normalize(id);
    root.dataset.theme=current;
    onApply(THEMES.find(theme=>theme.id===current));
    for(const subscriber of subscribers)subscriber(current);
    return current;
  };
  const read=()=>{try{return normalize(storage.getItem(THEME_STORAGE_KEY));}catch{return DEFAULT_THEME;}};
  const storageChanged=event=>{
    if(event.key===THEME_STORAGE_KEY||event.key===null)apply(event.key===null?read():event.newValue);
  };
  apply(read());
  events?.addEventListener('storage',storageChanged);
  return {
    getTheme:()=>current,
    setTheme(id){
      if(!validateThemeId(id))throw new TypeError('Choose an available theme.');
      try{storage.setItem(THEME_STORAGE_KEY,id);}
      catch{throw new Error('Could not save the theme on this device. Try again.');}
      return apply(id);
    },
    subscribeTheme(callback){subscribers.add(callback);return()=>subscribers.delete(callback);},
    dispose(){events?.removeEventListener('storage',storageChanged);subscribers.clear();}
  };
}

let browserController;
function controller(){
  if(!browserController){
    const storage={getItem:key=>globalThis.localStorage?.getItem(key)??null,setItem:(key,value)=>{
      if(!globalThis.localStorage)throw new Error('Storage unavailable');
      globalThis.localStorage.setItem(key,value);
    }};
    browserController=createThemeController({storage,root:document.documentElement,events:globalThis,onApply:theme=>{
      const color=document.querySelector('meta[name="theme-color"]'),scheme=document.querySelector('meta[name="color-scheme"]');
      if(color)color.content=theme.swatches[0];
      if(scheme)scheme.content=theme.mode;
    }});
  }
  return browserController;
}
export const initializeTheme=()=>controller().getTheme();
export const getTheme=()=>controller().getTheme();
export const setTheme=id=>controller().setTheme(id);
export const subscribeTheme=callback=>controller().subscribeTheme(callback);
