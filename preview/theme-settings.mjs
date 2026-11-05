import {THEMES,DEFAULT_THEME} from './theme-catalog.mjs';
import {getTheme,setTheme,subscribeTheme} from './theme.mjs';

const node=(tag,className='',text)=>{
  const element=document.createElement(tag);
  if(className)element.className=className;
  if(text!==undefined)element.textContent=text;
  return element;
};

export function mountThemeSettings(host){
  host.classList.add('theme-settings');
  const title=node('h2','','Appearance');
  const description=node('p','muted','Choose a theme for this device.');
  const current=node('p','theme-current muted');
  const error=node('span','theme-error inline-error');
  error.setAttribute('role','alert');error.hidden=true;
  const cards=new Map(),inputs=[];
  let active=true;
  host.append(title,description,current);

  function clearError(){error.hidden=true;error.textContent='';}

  function paint(){
    if(!active)return;
    const selected=getTheme(),theme=THEMES.find(item=>item.id===selected)||THEMES.find(item=>item.id===DEFAULT_THEME);
    current.textContent='Current theme: '+theme.name;
    for(const [id,card] of cards){
      const checked=id===theme.id;
      card.input.checked=checked;
      card.label.dataset.selected=String(checked);
      card.status.textContent=checked?'Selected':'';
    }
  }

  for(const [mode,label] of [['dark','Dark themes'],['light','Light themes']]){
    const group=node('fieldset','theme-group'),legend=node('legend','',label),grid=node('div','theme-grid');
    group.append(legend,grid);
    for(const theme of THEMES.filter(item=>item.mode===mode)){
      const choice=node('label','theme-option'),input=node('input','theme-choice-control');
      const id='theme-choice-'+theme.id;
      input.type='radio';input.name='theme';input.value=theme.id;input.dataset.themeChoice=theme.id;
      input.setAttribute('aria-labelledby',id+'-name');input.setAttribute('aria-describedby',id+'-description');
      const preview=node('span','theme-preview');preview.setAttribute('aria-hidden','true');
      preview.style.setProperty('--preview-bg',theme.swatches[0]);
      preview.style.setProperty('--preview-panel',theme.swatches[1]);
      preview.style.setProperty('--preview-accent',theme.swatches[2]);
      preview.append(node('span','theme-preview-nav'),node('span','theme-preview-panel'),node('span','theme-preview-accent'));
      const heading=node('span','theme-choice-title',theme.name),detail=node('span','theme-choice-description',theme.description),
        status=node('span','theme-choice-state');
      heading.id=id+'-name';detail.id=id+'-description';status.setAttribute('aria-hidden','true');
      input.addEventListener('change',()=>{
        if(!active||!input.checked)return;
        clearError();
        try{setTheme(theme.id);}
        catch(exception){choice.append(error);error.textContent=exception.message||'Could not save the theme on this device.';error.hidden=false;}
        paint();
      });
      choice.append(preview,heading,input,detail,status);grid.append(choice);
      cards.set(theme.id,{input,label:choice,status});inputs.push(input);
    }
    host.append(group);
  }
  host.append(error);
  const unsubscribe=subscribeTheme(()=>{clearError();paint();});
  paint();
  return ()=>{
    active=false;unsubscribe();
    for(const input of inputs)input.disabled=true;
  };
}
