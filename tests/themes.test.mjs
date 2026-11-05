import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {THEMES,DEFAULT_THEME,validateThemeId} from '../preview/theme-catalog.mjs';
import {createThemeController,THEME_STORAGE_KEY} from '../preview/theme.mjs';

function fixture(initial={},denied=false){
  const values=new Map(Object.entries(initial)),listeners=new Set(),root={dataset:{}},applied=[];
  const storage={getItem:key=>values.get(key)??null,setItem:(key,value)=>{if(denied)throw new Error('Storage denied');values.set(key,value);}};
  const events={addEventListener:(type,listener)=>{if(type==='storage')listeners.add(listener);},
    removeEventListener:(type,listener)=>{if(type==='storage')listeners.delete(listener);}};
  const controller=createThemeController({storage,root,events,onApply:theme=>applied.push(theme.id)});
  return {controller,root,applied,values,storage,events,emit:event=>{for(const listener of listeners)listener(event);},listeners};
}

test('theme collection contains exactly three light and nine dark themes with requested variants',()=>{
  assert.equal(THEMES.length,12);assert.equal(new Set(THEMES.map(theme=>theme.id)).size,12);
  assert.equal(THEMES.filter(theme=>theme.mode==='light').length,3);
  assert.equal(THEMES.filter(theme=>theme.mode==='dark').length,9);
  assert.equal(THEMES.find(theme=>theme.id==='silver').mode,'light');
  assert.equal(THEMES.find(theme=>theme.id==='midnight').mode,'dark');
  assert.equal(DEFAULT_THEME,'evolved');
  for(const theme of THEMES){
    assert.ok(theme.name.trim());assert.ok(theme.description.trim());
    assert.ok(theme.swatches.length>=3);
    assert.ok(theme.swatches.every(color=>/^#[a-f\d]{6}$/i.test(color)));
    assert.equal(validateThemeId(theme.id),true);
  }
  for(const value of [null,{},'Silver','../silver','silver; color:red','missing'])assert.equal(validateThemeId(value),false);
});

test('a theme persists across controller lifetimes without replacing other preferences',()=>{
  const favorites=JSON.stringify({favoriteColors:['#123456'],favoriteEffects:['activity_glow']});
  const first=fixture({'ee.ui-preferences.v1':favorites});
  first.controller.setTheme('silver');
  assert.equal(first.root.dataset.theme,'silver');assert.equal(first.values.get(THEME_STORAGE_KEY),'silver');
  assert.equal(first.values.get('ee.ui-preferences.v1'),favorites);
  first.controller.dispose();
  const second=createThemeController({storage:first.storage,root:{dataset:{}},events:first.events,onApply:()=>{}});
  assert.equal(second.getTheme(),'silver');second.dispose();
});

test('unrecognized saved themes and denied reads fall back safely at startup',()=>{
  for(const value of ['not-a-theme','null','{"id":"silver"}','']){
    const current=fixture({[THEME_STORAGE_KEY]:value});assert.equal(current.controller.getTheme(),DEFAULT_THEME);current.controller.dispose();
  }
  const root={dataset:{}},controller=createThemeController({root,storage:{getItem(){throw new Error('denied');},setItem(){}},events:{addEventListener(){},removeEventListener(){}},onApply(){}});
  assert.equal(controller.getTheme(),DEFAULT_THEME);assert.equal(root.dataset.theme,DEFAULT_THEME);controller.dispose();
});

test('failed persistence preserves the applied theme and does not publish a false selection',()=>{
  const current=fixture({[THEME_STORAGE_KEY]:'midnight'},true),changes=[];
  const remove=current.controller.subscribeTheme(theme=>changes.push(theme));
  const before=[...current.applied];
  assert.throws(()=>current.controller.setTheme('silver'),/save|storage|device/i);
  assert.equal(current.controller.getTheme(),'midnight');assert.equal(current.root.dataset.theme,'midnight');
  assert.deepEqual(current.applied,before);assert.deepEqual(changes,[]);
  remove();current.controller.dispose();
});

test('invalid explicit choices reject without overwriting the current preference',()=>{
  const current=fixture({[THEME_STORAGE_KEY]:'silver'});
  for(const value of ['unknown','SILVER',null,{},'../silver'])assert.throws(()=>current.controller.setTheme(value));
  assert.equal(current.controller.getTheme(),'silver');assert.equal(current.values.get(THEME_STORAGE_KEY),'silver');current.controller.dispose();
});

test('another tab updates the applied choice without writing back or changing unrelated preferences',()=>{
  const current=fixture({[THEME_STORAGE_KEY]:'silver'}),changes=[];
  const remove=current.controller.subscribeTheme(theme=>changes.push(theme));
  current.emit({key:'another-key',newValue:'forest'});
  assert.equal(current.controller.getTheme(),'silver');
  current.emit({key:THEME_STORAGE_KEY,newValue:'midnight'});
  assert.equal(current.controller.getTheme(),'midnight');assert.equal(current.root.dataset.theme,'midnight');
  assert.equal(current.values.get(THEME_STORAGE_KEY),'silver');
  assert.equal(changes.length,1);
  current.emit({key:THEME_STORAGE_KEY,newValue:null});
  assert.equal(current.controller.getTheme(),DEFAULT_THEME);
  remove();const count=changes.length;current.emit({key:THEME_STORAGE_KEY,newValue:'forest'});assert.equal(changes.length,count);
  current.controller.dispose();assert.equal(current.listeners.size,0);
});

test('every theme has explicit CSS coverage and the startup script runs before stylesheets',async()=>{
  const css=await readFile(new URL('../preview/themes.css',import.meta.url),'utf8');
  for(const theme of THEMES)assert.ok(new RegExp('data-theme\\s*=\\s*["\x27]?'+theme.id+'["\x27]?\\s*\\]').test(css),'Missing CSS for '+theme.id);
  const html=await readFile(new URL('../preview/index.html',import.meta.url),'utf8');
  const startup=html.indexOf('theme-init.mjs'),stylesheet=html.indexOf('rel="stylesheet"');
  assert.ok(startup>=0&&startup<stylesheet,'Saved theme must be applied before the first styled paint');
  const tag=html.slice(html.lastIndexOf('<script',startup),html.indexOf('>',startup)+1);
  assert.ok(!/\b(?:async|defer)\b|type=["']module/.test(tag),'First-paint selection must run synchronously');
});
