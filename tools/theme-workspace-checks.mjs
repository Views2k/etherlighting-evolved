import assert from 'node:assert/strict';
import {THEMES,DEFAULT_THEME} from '../preview/theme-catalog.mjs';

const contrastProbe = `(() => {
  const canvas=document.createElement('canvas');canvas.width=canvas.height=1;
  const context=canvas.getContext('2d',{willReadFrequently:true});
  const parse=value=>{
    if(!/^(?:rgba?\\(|color\\(srgb )/.test(value)){
      if(!CSS.supports('color',value))return null;
      context.clearRect(0,0,1,1);context.fillStyle=value;context.fillRect(0,0,1,1);
      const [r,g,b,a]=context.getImageData(0,0,1,1).data;return [r,g,b,a/255];
    }
    const values=value.match(/[\\d.]+/g)?.map(Number);if(!values||values.length<3)return null;
    const rgb=values.slice(0,3);return [...(value.startsWith('color(srgb ')?rgb.map(channel=>channel*255):rgb),values[3]??1];
  };
  const blend=(fg,bg)=>fg.slice(0,3).map((value,index)=>value*fg[3]+bg[index]*(1-fg[3]));
  const background=element=>{
    if(!element)return [255,255,255];
    const style=getComputedStyle(element),color=parse(style.backgroundColor)||[0,0,0,0];
    return blend(color,background(element.parentElement));
  };
  const luminance=rgb=>rgb.slice(0,3).map(value=>value/255).map(value=>value<=.04045?value/12.92:((value+.055)/1.055)**2.4).reduce((sum,value,index)=>sum+value*[.2126,.7152,.0722][index],0);
  const contrast=(a,b)=>{const values=[luminance(a),luminance(b)].sort((a,b)=>a-b);return (values[1]+.05)/(values[0]+.05);};
  const selectors=['#view h1','#view .muted','.nav-button.selected','#view .button.primary','#view input[type=text]','#view select','.theme-choice-title','.theme-choice-description','.theme-choice-state'];
  const rows=[];
  for(const selector of selectors){
    for(const element of [...document.querySelectorAll(selector)].slice(0,selector.startsWith('.theme-')?12:3)){
      const bounds=element.getBoundingClientRect(),style=getComputedStyle(element);
      if(!bounds.width||!bounds.height||element.disabled||Number(style.opacity)<.99||(!element.textContent.trim()&&!element.value))continue;
      const foreground=parse(style.color);if(!foreground)continue;
      const parentBackground=background(element.parentElement),base=background(element);
      const stops=[...style.backgroundImage.matchAll(/(?:rgba?|color|oklab|oklch|lab|lch)\\([^)]+\\)/g)].map(match=>parse(match[0])).filter(Boolean);
      const backgrounds=stops.length?stops.map(color=>blend(color,parentBackground)):[base];
      const ratio=Math.min(...backgrounds.map(color=>contrast(blend(foreground,color),color)));
      rows.push({selector,ratio:Number(ratio.toFixed(2)),color:style.color,background:style.backgroundColor});
    }
  }
  return {rows,failures:rows.filter(row=>row.ratio<4.45)};
})()`;

export async function checkThemeWorkflows({evaluate,until,send,screenshot,url}){
  const result={themes:[],responsive:[],contrast:[],accents:[],storage_failure:false,cross_document:false,persistence:false};
  const ready=(expression,label)=>until(()=>evaluate(expression),label);
  const settledFrame=()=>evaluate(`(async()=>{
    const frame=()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
    await frame();
    await Promise.all(document.getAnimations().filter(animation=>animation.effect?.getComputedTiming().iterations!==Infinity).map(animation=>animation.finished.catch(()=>{})));
    await frame();
  })()`);
  const revision=async()=>{const response=await fetch(url+'api/scene');assert.ok(response.ok,'Read scene revision');const value=await response.json();assert.ok(value.revision!==undefined,'Scene revision exists');return value.revision;};
  const baseline=await revision();
  const route=async page=>{
    await evaluate(`document.querySelector('[data-page="${page}"]').click()`);
    await ready(`document.querySelector('[data-page="${page}"]').getAttribute('aria-current')==='page' && !!document.querySelector('#view h1')`,'theme route '+page);
    if(page!=='Lighting')await ready(`document.querySelector('#view h1')?.textContent===${JSON.stringify(page)}`,'theme heading '+page);
    if(page==='Lighting')await ready('document.querySelectorAll(".port-tile").length===10','lighting rendered');
    if(page==='Settings')await ready('document.querySelectorAll(".theme-settings input[name=theme]").length===12','theme selector');
    if(page==='Automation')await ready('!!document.querySelector(".automation-tabs")','automation rendered');
    await settledFrame();
  };
  const selectTheme=async id=>{
    await route('Settings');
    await evaluate(`document.querySelector('input[name=theme][value="${id}"]').click()`);
    await ready(`document.documentElement.dataset.theme===${JSON.stringify(id)} && document.querySelector('input[name=theme][value="${id}"]').checked`,'theme applied '+id);
  };
  const checkLayout=async label=>{
    const layout=await evaluate('({width:innerWidth,height:innerHeight,overflow:document.documentElement.scrollWidth-innerWidth})');
    assert.ok(layout.overflow<=1,label+' overflows the document');
    return layout;
  };
  const checkContrast=async(theme,page)=>{
    await settledFrame();
    const values=await evaluate(contrastProbe);
    assert.deepEqual(values.failures,[],theme+' / '+page+' text contrast');
    result.contrast.push({theme,page,minimum:values.rows.length?Math.min(...values.rows.map(row=>row.ratio)):null});
  };
  const checkAccents=async(theme,page)=>{
    const values=await evaluate(`(()=>{
      const probe=document.createElement('span');document.body.append(probe);
      const expected=token=>{probe.style.color='var(--'+token+')';return getComputedStyle(probe).color;};
      const rows=[];
      const targets=[['[data-page][aria-current="page"] .icon','pink'],['.control-section-heading','accent'],['.control-section-heading .icon','blue'],['.settings-nav .button.selected','pink'],['.secondary-list > button.selected','pink'],['.theme-choice-state','accent']];
      try{
        for(const [selector,token] of targets)for(const element of document.querySelectorAll(selector)){
          const bounds=element.getBoundingClientRect();if(!bounds.width||!bounds.height||selector==='.theme-choice-state'&&!element.textContent.trim())continue;
          rows.push({selector,actual:getComputedStyle(element).color,expected:expected(${JSON.stringify(theme)}==='evolved'?token:'accent')});
        }
      }finally{probe.remove();}
      return {rows,failures:rows.filter(row=>row.actual!==row.expected)};
    })()`);
    assert.ok(values.rows.some(row=>row.selector==='[data-page][aria-current="page"] .icon'),'Selected navigation icon is present');
    assert.deepEqual(values.failures,[],theme+' / '+page+' decorative accents match the palette');
    result.accents.push({theme,page,checked:values.rows.length});
  };
  await send('Emulation.setDeviceMetricsOverride',{width:1366,height:768,deviceScaleFactor:1,mobile:false});
  for(const theme of THEMES){
    await selectTheme(theme.id);
    assert.equal(await evaluate('getComputedStyle(document.documentElement).colorScheme'),theme.mode);
    for(const page of ['Settings','Lighting','Effects','Automation','Profiles','Help']){
      await route(page);await checkLayout(theme.id+' '+page);await checkContrast(theme.id,page);await checkAccents(theme.id,page);
      if(page==='Lighting')await screenshot('theme-'+theme.id);
    }
    result.themes.push(theme.id);
  }
  for(const id of ['silver','midnight']){
    await selectTheme(id);
    for(const [width,height] of [[390,844],[1280,400]]){
      await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
      await route('Settings');
      const input='input[name=theme][value="'+id+'"]';
      await evaluate(`document.querySelector(${JSON.stringify(input)}).scrollIntoView({block:'center'})`);
      await checkLayout(id+' settings '+width);await screenshot('theme-settings-'+id+'-'+width);
      assert.ok(await evaluate('[...document.querySelectorAll(".theme-option")].every(el=>el.scrollWidth<=el.clientWidth+1)'),'Theme cards fit their labels');
      await route('Automation');
      await ready('!!document.querySelector("input[type=checkbox]")','themed automation checkbox');
      assert.equal(await evaluate('getComputedStyle(document.querySelector("input[type=checkbox]")).appearance'),'none');
      await screenshot('theme-rules-'+id+'-'+width);
      await route('Lighting');
      await evaluate(`document.querySelector('[data-port="1"]').click();document.querySelector('#port-details').click()`);
      await ready('!!document.querySelector(".port-details[open]")','themed port details');
      await checkLayout(id+' details '+width);await screenshot('theme-details-'+id+'-'+width);
      await evaluate('document.querySelector(".port-details").close()');
      await evaluate('(()=>{const open=document.querySelector("#open-port-controls"),expand=document.querySelector("#expand-inspector");if(open.getClientRects().length)open.click();else if(expand.getClientRects().length&&expand.getAttribute("aria-expanded")!=="true")expand.click();})()');
      await evaluate('document.querySelector("[data-editor=color]").click()');
      await ready('!!document.querySelector("[data-color=manual]")','fixed color editor');
      await evaluate('document.querySelector("[data-color=manual]").click()');
      await ready('!!document.querySelector(".color-popover")','themed color picker');
      assert.equal(await evaluate('getComputedStyle(document.querySelector(".color-popover")).colorScheme'),id==='silver'?'light':'dark');
      await screenshot('theme-picker-'+id+'-'+width);
      await evaluate('document.querySelector(".color-popup-footer button").click()');
      await ready('!document.querySelector(".color-popover")','color picker closed');
      result.responsive.push({id,width,height});
    }
  }
  await send('Emulation.setDeviceMetricsOverride',{width:1366,height:768,deviceScaleFactor:1,mobile:false});
  await selectTheme('silver');
  await evaluate('document.querySelector("input[name=theme][value=silver]").focus()');
  await send('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowRight',code:'ArrowRight'});
  await send('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowRight',code:'ArrowRight'});
  await ready('document.documentElement.dataset.theme==="pearl"','theme keyboard selection');
  await selectTheme('midnight');
  await evaluate('document.querySelector("input[name=theme][value=silver]").scrollIntoView({block:"center"})');
  await evaluate(`(()=>{window.__themeSetItem=Storage.prototype.setItem;Storage.prototype.setItem=function(key,value){if(key==='ee.theme.v1')throw new Error('test storage failure');return window.__themeSetItem.call(this,key,value);};document.querySelector('input[name=theme][value=silver]').click();})()`);
  try{
    await ready('document.querySelector(".theme-error").textContent.trim().length>0','theme storage error');
    assert.equal(await evaluate('document.documentElement.dataset.theme'),'midnight');
    assert.equal(await evaluate('document.querySelector("input[name=theme][value=midnight]").checked'),true);
    assert.equal(await evaluate('document.querySelector(".theme-error").closest(".theme-option").querySelector("input[name=theme]").value'),'silver','Save failure stays with the attempted choice');
    result.storage_failure=true;
  }finally{await evaluate('Storage.prototype.setItem=window.__themeSetItem;delete window.__themeSetItem;');}
  await evaluate(`(()=>{const iframe=document.createElement('iframe');iframe.hidden=true;document.body.append(iframe);iframe.contentWindow.localStorage.setItem('ee.theme.v1','silver');iframe.remove();})()`);
  await ready('document.documentElement.dataset.theme==="silver" && document.querySelector("input[name=theme][value=silver]").checked','theme change from another document');
  assert.equal(await evaluate('document.querySelector(".theme-error").hidden'),true,'Successful cross-document choice clears the save error');result.cross_document=true;
  const oldDocument=await evaluate('performance.timeOrigin');
  await send('Page.reload',{ignoreCache:true});
  await until(async()=>{try{return await evaluate(`performance.timeOrigin!==${oldDocument} && document.documentElement.dataset.theme==='silver' && !!document.querySelector('[data-page=Settings]')`);}catch(error){if(/context|destroyed/i.test(error.message))return false;throw error;}},'saved theme after reload');
  assert.equal(await evaluate('getComputedStyle(document.documentElement).colorScheme'),'light');result.persistence=true;
  assert.equal(await revision(),baseline,'Theme and control appearance checks never change lighting');
  await selectTheme(DEFAULT_THEME);await route('Lighting');
  return result;
}
