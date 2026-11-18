import {getFavoriteColors, toggleFavoriteColor, getPalettes, savePalette, deletePalette} from './preferences.mjs';
import {node} from './dom.mjs';

let instance = 0;
const validColor = value => /^#[\da-f]{6}$/i.test(value || '') ? value.toUpperCase() : null;
const uniqueColors = values => [...new Set(values.map(validColor).filter(Boolean))];

export function mountColorLibrary(host, {getColor, onChoose, onError = () => {}}) {
  const id = `color-library-${++instance}`;
  let favorites = getFavoriteColors(), palettes = getPalettes(), selectedPalette = '', current = null, currentSaved;
  let candidates = [], chosen = new Set(), pendingDelete = null, undoPalette = null, disposed = false;
  const root = node('section', 'color-library');root.setAttribute('aria-label','Saved colors');
  const heading = node('div','color-library-heading'), favorite = node('button','button quiet favorite-current','Save color');favorite.type='button';
  heading.append(node('h3','','Favorites'),favorite);
  const favoriteColors = node('div','favorite-colors');favoriteColors.setAttribute('role','group');favoriteColors.setAttribute('aria-label','Favorite colors');
  const empty = node('p','muted color-library-empty','Save colors here for quick access.');
  const paletteRow = node('div','palette-select-row'), paletteLabel = node('label','field'), paletteSelect = node('select');
  paletteSelect.id=`${id}-palette`;paletteLabel.htmlFor=paletteSelect.id;paletteLabel.append(node('span','','Palette'),paletteSelect);
  const remove = node('button','button quiet palette-delete','Delete');remove.type='button';remove.disabled=true;remove.setAttribute('aria-label','Delete selected palette');
  paletteRow.append(paletteLabel,remove);
  const paletteColors = node('div','palette-colors');paletteColors.setAttribute('role','group');paletteColors.setAttribute('aria-label','Palette colors');
  const saveDetails = node('details','palette-save'), summary = node('summary','','Save palette');
  const form = node('form','palette-save-form'), nameLabel = node('label','field'), name = node('input');
  name.id=`${id}-name`;name.type='text';name.required=true;name.maxLength=40;name.placeholder='Palette name';name.autocomplete='off';
  nameLabel.htmlFor=name.id;nameLabel.append(node('span','','Palette name'),name);
  const builderLabel = node('span','muted','Colors to save'), builder = node('div','palette-builder');
  builder.setAttribute('role','group');builder.setAttribute('aria-label','Colors to save');
  const count = node('p','palette-color-count muted');count.id=`${id}-count`;
  const save = node('button','button primary','Save palette');save.type='submit';save.setAttribute('aria-describedby',count.id);
  form.append(nameLabel,builderLabel,builder,count,save);saveDetails.append(summary,form);
  const confirmation = node('div','palette-confirm');confirmation.hidden=true;
  const confirmText = node('p'), cancelDelete = node('button','button quiet','Cancel'), confirmDelete = node('button','button danger','Delete palette');
  cancelDelete.type=confirmDelete.type='button';confirmation.append(confirmText,cancelDelete,confirmDelete);
  const error = node('p','inline-error');error.hidden=true;error.setAttribute('role','alert');
  const status = node('div','palette-status');status.hidden=true;status.setAttribute('role','status');
  const statusText = node('span'), undo = node('button','button quiet','Undo');undo.type='button';undo.hidden=true;status.append(statusText,undo);
  root.append(heading,favoriteColors,empty,paletteRow,paletteColors,saveDetails,confirmation,error,status);host.append(root);

  function failure(problem) {
    const message = problem instanceof Error ? problem.message : String(problem);
    error.textContent=message;error.hidden=false;onError(message);
  }
  function clearError(){error.hidden=true;error.textContent='';}
  function message(text,canUndo=false){statusText.textContent=text;status.hidden=false;undo.hidden=!canUndo;}
  function chip(color, callback, selected) {
    const button=node('button','color-chip');button.type='button';button.style.setProperty('--swatch',color);
    button.setAttribute('aria-label',`${selected === undefined?'Use':'Include'} ${color}`);
    if(selected!==undefined)button.setAttribute('aria-pressed',String(selected));
    button.title=color;button.addEventListener('click',()=>callback(button));return button;
  }
  async function choose(color){
    clearError();
    try{await onChoose(color);if(!disposed)update();}catch(problem){if(!disposed)failure(problem);}
  }
  function renderFavorites(){
    favoriteColors.replaceChildren(...favorites.map(color=>chip(color,()=>{void choose(color);})));empty.hidden=favorites.length>0;
  }
  function renderPalette(){
    const palette=palettes.find(item=>item.name===selectedPalette);
    paletteColors.replaceChildren(...(palette?.colors || []).map(color=>chip(color,()=>{void choose(color);})));paletteColors.hidden=!palette;
    remove.disabled=!palette;remove.setAttribute('aria-label',palette?`Delete palette ${palette.name}`:'Delete selected palette');
  }
  function renderPalettes(){
    if(!palettes.some(item=>item.name===selectedPalette))selectedPalette='';
    const placeholder=node('option','',palettes.length?'Choose palette':'No saved palettes');placeholder.value='';
    paletteSelect.replaceChildren(placeholder,...palettes.map(palette=>{const option=node('option','',palette.name);option.value=palette.name;return option;}));
    paletteSelect.disabled=!palettes.length;paletteSelect.value=selectedPalette;renderPalette();
  }
  function refreshSave(){
    const size=chosen.size;count.textContent=`${size} ${size===1?'color':'colors'} selected${size>12?' · Select up to 12':''}`;
    const duplicate=palettes.some(palette=>palette.name.toLocaleLowerCase()===name.value.trim().toLocaleLowerCase());
    save.textContent=duplicate?'Update palette':'Save palette';save.disabled=!name.value.trim() || !size || size>12;
  }
  function renderBuilder(){
    builder.replaceChildren(...candidates.map(color=>chip(color,button=>{
      chosen.has(color)?chosen.delete(color):chosen.add(color);button.setAttribute('aria-pressed',String(chosen.has(color)));refreshSave();
    },chosen.has(color))));refreshSave();
  }
  function startSave(){
    candidates=uniqueColors([...favorites,current]);chosen=new Set(candidates);renderBuilder();
  }
  function closeDelete(){pendingDelete=null;confirmation.hidden=true;remove.disabled=!selectedPalette;}

  favorite.addEventListener('click',()=>{
    clearError();if(!current)return;
    try{favorites=toggleFavoriteColor(current);renderFavorites();if(saveDetails.open)startSave();update();}
    catch(problem){failure(problem);}
  });
  paletteSelect.addEventListener('change',()=>{selectedPalette=paletteSelect.value;closeDelete();renderPalette();});
  remove.addEventListener('click',()=>{
    pendingDelete=palettes.find(palette=>palette.name===selectedPalette);if(!pendingDelete)return;
    clearError();confirmText.textContent=`Delete palette “${pendingDelete.name}”?`;confirmation.hidden=false;cancelDelete.focus();
  });
  cancelDelete.addEventListener('click',()=>{closeDelete();remove.focus();});
  confirmDelete.addEventListener('click',()=>{
    if(!pendingDelete)return;
    clearError();
    try{
      const removed=structuredClone(pendingDelete);palettes=deletePalette(removed.name);undoPalette=removed;
      selectedPalette='';closeDelete();renderPalettes();refreshSave();message(`Deleted “${removed.name}”.`,true);undo.focus();
    }catch(problem){failure(problem);}
  });
  undo.addEventListener('click',()=>{
    if(!undoPalette)return;
    clearError();
    try{
      const existing=getPalettes().find(palette=>palette.name.toLocaleLowerCase()===undoPalette.name.toLocaleLowerCase());
      if(existing)throw new Error('A palette with this name already exists.');
      palettes=savePalette(undoPalette.name,undoPalette.colors);selectedPalette=undoPalette.name;undoPalette=null;
      renderPalettes();refreshSave();message('Palette restored.');paletteSelect.focus();
    }catch(problem){failure(problem);}
  });
  saveDetails.addEventListener('toggle',()=>{if(saveDetails.open)startSave();});
  name.addEventListener('input',()=>{name.setCustomValidity('');refreshSave();});
  form.addEventListener('submit',event=>{
    event.preventDefault();clearError();
    if(!name.value.trim()){name.setCustomValidity('Enter a palette name.');name.reportValidity();return;}
    if(!form.reportValidity())return;
    if(!chosen.size || chosen.size>12){failure('Choose between 1 and 12 colors.');return;}
    try{
      const paletteName=name.value.trim();palettes=savePalette(paletteName,[...chosen]);selectedPalette=paletteName;
      undoPalette=null;renderPalettes();saveDetails.open=false;name.value='';refreshSave();
      message(`Saved “${paletteName}”.`);paletteSelect.focus();
    }catch(problem){failure(problem);}
  });
  function update(){
    if(disposed)return;
    const next=validColor(getColor()),saved=favorites.includes(next);if(next===current && saved===currentSaved)return;current=next;currentSaved=saved;
    favorite.disabled=!current;
    favorite.textContent=saved?'Saved color':'Save color';favorite.setAttribute('aria-pressed',String(saved));
    favorite.setAttribute('aria-label',current?`${saved?'Remove':'Save'} ${current} ${saved?'from':'to'} favorites`:'Save current color');
  }
  const storageChanged=event=>{
    if(event.key && event.key!=='ee.ui-preferences.v1')return;
    favorites=getFavoriteColors();palettes=getPalettes();renderFavorites();renderPalettes();refreshSave();current=null;update();
  };
  window.addEventListener('storage',storageChanged);
  renderFavorites();renderPalettes();refreshSave();update();
  const cleanup=()=>{disposed=true;window.removeEventListener('storage',storageChanged);root.remove();};
  cleanup.update=update;
  return cleanup;
}
