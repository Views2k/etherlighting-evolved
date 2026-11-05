import assert from 'node:assert/strict';
import {createTopologyFixture} from '../tests/fixtures/topology-layout.mjs';

const cases=[
  {name:'8-2',copper:8,sfp:2},{name:'9-0',copper:9,sfp:0},{name:'10-0',copper:10,sfp:0},
  {name:'10-2',copper:10,sfp:2},{name:'16-2',copper:16,sfp:2},
  {name:'24-2',copper:24,sfp:2},{name:'24-4',copper:24,sfp:4},
  {name:'48-4',copper:48,sfp:4},{name:'0-22',copper:0,sfp:22},
  {name:'0-32',copper:0,sfp:32},{name:'0-54',copper:0,sfp:54},
  {name:'sparse',copperIds:[1,3,4,8,9,12,15,20,25,30,32,38],sfpIds:[41,45,50,54]},
  {name:'empty',copper:0,sfp:0},
];
const expectedIds=spec=>{
  const copper=spec.copperIds??Array.from({length:spec.copper},(_,index)=>index+1);
  return [...copper,...(spec.sfpIds??Array.from({length:spec.sfp},(_,index)=>copper.length+index+1))];
};
const chunks=(ids,size)=>Array.from({length:Math.ceil(ids.length/size)},(_,index)=>ids.slice(index*size,(index+1)*size));

export async function checkTopologyWorkflows({evaluate,until,send,screenshot,url,root}){
  const original=await evaluate('({url:location.href,width:innerWidth,height:innerHeight,hasMaster:Boolean(document.querySelector("#master"))})');
  const fixture=createTopologyFixture({root});
  await new Promise((resolve,reject)=>{fixture.once('error',reject);fixture.listen(0,'127.0.0.1',resolve);});
  const address=`http://127.0.0.1:${fixture.address().port}/`;
  const report={cases:[],transitions:[],narrow_header:null};
  let failure;
  const frame=()=>evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
  const ready=(expression,label)=>until(async()=>{
    try{return await evaluate(expression);}catch(error){if(/context|destroyed|navigat/i.test(error.message))return false;throw error;}
  },label);
  const viewport=async(width,height)=>{
    await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<=700});
    await frame();
  };
  const navigate=async spec=>{
    const target=address+'?topology='+encodeURIComponent(JSON.stringify(spec));
    await send('Page.navigate',{url:target});
    await ready(`location.href===${JSON.stringify(target)}&&document.documentElement.dataset.fixtureReady==='true'`,'topology fixture '+spec.name);
    await frame();
  };
  const inspect=()=>evaluate(`(()=>{
    const canvas=document.querySelector('.canvas-area'),studio=document.querySelector('.studio-content');
    const scroller=innerWidth<=700?studio:canvas;
    const rect=node=>{const r=node.getBoundingClientRect();return {top:r.top,left:r.left,right:r.right,bottom:r.bottom,width:r.width,height:r.height};};
    const ports=[...document.querySelectorAll('.port-tile')].map(tile=>({id:Number(tile.dataset.port),sfp:tile.classList.contains('sfp'),...rect(tile)}));
    const rows=[];for(const port of ports){let row=rows.find(row=>Math.abs(row.top-port.top)<=2);if(!row){row={top:port.top,ports:[]};rows.push(row);}row.ports.push(port.id);}
    const imports=[...document.styleSheets].flatMap(sheet=>[...sheet.cssRules].filter(rule=>rule.type===CSSRule.IMPORT_RULE));
    return {width:innerWidth,height:innerHeight,canvas:rect(canvas),scroller:rect(scroller),ports,rows:rows.map(row=>row.ports),
      documentOverflow:document.documentElement.scrollWidth-innerWidth,canvasOverflow:canvas.scrollWidth-canvas.clientWidth,
      studioOverflow:studio.scrollWidth-studio.clientWidth,maxScroll:scroller.scrollHeight-scroller.clientHeight,
      outerScroll:document.documentElement.scrollHeight-innerHeight,overflowY:getComputedStyle(scroller).overflowY,
      classes:document.querySelector('.port-map').className,bankDisplays:[...document.querySelectorAll('.port-group')].map(group=>({hidden:group.hidden,display:getComputedStyle(group).display})),
      imports:imports.map(rule=>({href:rule.href,loaded:Boolean(rule.styleSheet?.cssRules.length)})),accent:getComputedStyle(document.documentElement).getPropertyValue('--accent').trim(),
      sfpPrefixes:[...document.querySelectorAll('.sfp .port-number')].map(number=>getComputedStyle(number,'::before').content)};
  })()`);
  const check=async(spec,label)=>{
    await frame();
    const ids=expectedIds(spec),geometry=await inspect(),phone=geometry.width<=700;
    assert.deepEqual(geometry.ports.map(port=>port.id),ids,label+' preserves every physical port in order');
    assert.equal(new Set(geometry.ports.map(port=>port.id)).size,ids.length,label+' has unique port IDs');
    assert.ok(geometry.documentOverflow<=1&&geometry.canvasOverflow<=1&&geometry.studioOverflow<=1,label+' has no horizontal overflow: '+JSON.stringify(geometry));
    assert.ok(geometry.outerScroll<=1,label+' retains the workspace scroll boundary');
    assert.ok(geometry.imports.length>=3&&geometry.imports.every(item=>item.loaded)&&geometry.accent,label+' loads production theme imports');
    for(const port of geometry.ports){
      assert.ok(port.width>=44&&port.height>=44,label+' keeps port targets usable');
      assert.ok(port.left>=geometry.canvas.left-1&&port.right<=geometry.canvas.right+1,label+' keeps ports inside the canvas');
    }
    if(phone){
      const copperCount=spec.copperIds?.length??spec.copper;
      assert.deepEqual(geometry.rows,[...chunks(ids.slice(0,copperCount),2),...chunks(ids.slice(copperCount),2)],label+' preserves two-column phone banks');
      assert.ok(Math.abs(geometry.scroller.bottom-geometry.height)<=1,label+' extends the idle phone map to the bottom');
    }else{
      assert.ok(geometry.canvas.width>=640,label+' exercises the desktop map breakpoint');
      assert.deepEqual(geometry.rows,chunks(ids,10),label+' fills rows of at most ten physical ports');
      if(ids.length>10)assert.ok(geometry.sfpPrefixes.every(value=>value.includes('SFP')),label+' labels SFP ports in shared rows');
    }
    if(!ids.length){
      assert.ok(geometry.bankDisplays.every(group=>group.hidden),label+' hides empty banks');
      return {name:spec.name,width:geometry.width,height:geometry.height,rows:geometry.rows,maxScroll:geometry.maxScroll};
    }
    const last=ids.at(-1);
    await evaluate(`document.querySelector('[data-port="${last}"]').scrollIntoView({block:'end',inline:'nearest'})`);
    await frame();
    const target=await evaluate(`(()=>{
      const tile=document.querySelector('[data-port="${last}"]'),r=tile.getBoundingClientRect();
      const scroller=document.querySelector(innerWidth<=700?'.studio-content':'.canvas-area'),s=scroller.getBoundingClientRect();
      const x=r.left+r.width/2,y=(Math.max(r.top,s.top)+Math.min(r.bottom,s.bottom))/2;
      return {x,y,top:r.top,bottom:r.bottom,scrollTop:scroller.scrollTop,scrollLeft:scroller.scrollLeft,hit:Number(document.elementFromPoint(x,y)?.closest('[data-port]')?.dataset.port),scroller:{top:s.top,bottom:s.bottom}};
    })()`);
    assert.ok(target.top>=target.scroller.top-1&&target.bottom<=target.scroller.bottom+1,label+' reaches the last port vertically');
    assert.equal(target.scrollLeft,0,label+' never requires horizontal scrolling');
    assert.equal(target.hit,last,label+' last port is unobstructed');
    if(geometry.ports.at(-1).bottom>geometry.scroller.bottom+1)assert.ok(target.scrollTop>0,label+' uses vertical scrolling to reveal the last port');
    await send('Input.dispatchMouseEvent',{type:'mousePressed',x:target.x,y:target.y,button:'left',clickCount:1});
    await send('Input.dispatchMouseEvent',{type:'mouseReleased',x:target.x,y:target.y,button:'left',clickCount:1});
    await ready(`window.topologyFixture.selected.length===1&&window.topologyFixture.selected[0]===${last}&&document.querySelector('[data-port="${last}"]').getAttribute('aria-pressed')==='true'`,label+' last port selected');
    return {name:spec.name,width:geometry.width,height:geometry.height,rows:geometry.rows,maxScroll:geometry.maxScroll,lastSelected:last,scrollTop:target.scrollTop};
  };
  try{
    for(const [width,height] of [[1366,768],[1280,400],[390,844]]){
      await viewport(width,height);
      for(const spec of cases){
        await navigate(spec);
        const result=await check(spec,`${spec.name} at ${width}x${height}`);report.cases.push(result);
        if(['8-2','24-4','48-4','0-54'].includes(spec.name))await screenshot(`topology-${spec.name}-${width}`);
      }
    }
    await viewport(1280,400);await navigate(cases.find(spec=>spec.name==='48-4'));
    for(const name of ['48-4','8-2','0-32','empty']){
      const spec=cases.find(item=>item.name===name);
      await evaluate(`window.topologyFixture.setTopology(${JSON.stringify(spec)});document.querySelector('.canvas-area').scrollTop=0`);
      report.transitions.push(await check(spec,'transition '+name));
    }
    await viewport(920,400);await navigate({...cases.find(spec=>spec.name==='24-4'),uplinkLast:true});
    for(let attempt=0;attempt<4;attempt++){
      const size=await evaluate('({width:innerWidth,canvas:document.querySelector(".canvas-area").getBoundingClientRect().width})');
      if(size.canvas>=640&&size.canvas<=680)break;
      await viewport(Math.max(900,Math.round(size.width+660-size.canvas)),400);
    }
    const spec={...cases.find(item=>item.name==='24-4'),uplinkLast:true};
    const result=await check(spec,'narrow selected SFP uplink');
    await ready("Number(getComputedStyle([...document.querySelectorAll('.port-tile')].at(-1),'::after').opacity)===1",'selected SFP checkmark visible');
    const header=await evaluate(`(()=>{
      const tile=[...document.querySelectorAll('.port-tile')].at(-1),number=tile.querySelector('.port-number'),badge=tile.querySelector('.uplink-badge');
      const rect=node=>{const r=node.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height};};
      const style=getComputedStyle(tile,'::after'),probe=document.createElement('span');
      for(const property of style)probe.style.setProperty(property,style.getPropertyValue(property));
      probe.style.opacity='0';tile.append(probe);const mark=rect(probe);probe.remove();
      return {canvas:document.querySelector('.canvas-area').getBoundingClientRect().width,number:rect(number),badge:rect(badge),mark,hasUplink:Boolean(badge.querySelector('.uplink-icon')),checkOpacity:Number(style.opacity)};
    })()`);
    const overlaps=(a,b)=>Math.min(a.right,b.right)-Math.max(a.left,b.left)>1&&Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top)>1;
    assert.ok(header.canvas>=640&&header.canvas<=680,'Narrow fixture exercises the breakpoint edge');
    assert.ok(header.hasUplink,'Narrow fixture includes a selected SFP uplink');
    assert.equal(header.checkOpacity,1,'Selected SFP checkmark is visible');
    assert.ok(!overlaps(header.number,header.badge),'SFP number and uplink badge do not overlap: '+JSON.stringify(header));
    assert.ok(!overlaps(header.number,header.mark)&&!overlaps(header.badge,header.mark),'Selection checkmark clears SFP identity and uplink badge: '+JSON.stringify(header));
    report.narrow_header={...result,header};await screenshot('topology-narrow-selected-sfp');
    return report;
  }catch(error){
    failure=error;
    try{await screenshot("topology-failure");}catch{}
    throw error;
  }finally{
    try{
      await send('Emulation.setDeviceMetricsOverride',{width:original.width,height:original.height,deviceScaleFactor:1,mobile:false});
      await send('Page.navigate',{url:original.url||url});
      if(original.hasMaster)await ready('!!document.querySelector("#master")','workspace restored after topology fixtures');
    }catch(error){if(!failure)throw error;}finally{
      await new Promise(resolve=>{fixture.close(resolve);fixture.closeAllConnections();});
    }
  }
}
