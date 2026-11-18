import {createServer} from 'node:http';
import {readFileSync} from 'node:fs';
import {fileURLToPath, pathToFileURL} from 'node:url';
import path from 'node:path';

const defaultRoot=fileURLToPath(new URL('../../',import.meta.url));
const palette=[[67,99,255],[144,72,230],[227,71,167],[41,182,216]];
const escape=value=>String(value??'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const count=value=>Math.max(0,Math.min(64,Math.trunc(Number(value)||0)));
const section=(source,start,end)=>{
  const first=source.indexOf(start),last=source.indexOf(end,first+start.length);
  if(first<0||last<0)throw new Error(`Production fixture boundary missing: ${start}`);
  return source.slice(first,last);
};

export function createTopologyFixture({root=defaultRoot}={}){
  const source=readFileSync(path.join(root,'preview/app.mjs'),'utf8');
  const shell=section(source,'app.innerHTML =','const view = document.querySelector');
  const studio=section(source,'function renderStudio() {','  uplinkBandwidth?.dispose();')+'}\n';
  const ports=section(source,'function speedLegend() {','function updateLive() {');
  const runtime=configuration=>`
import {LINK_SPEEDS,linkStatus,portPower,isUplink,portLabel,selectPort,selectionHint,switchTitle} from '/preview/port-map.mjs';
import {portRanges} from '/preview/port-topology.mjs';
import {portDisplayRgb} from '/preview/port-display.mjs';
import {portInk} from '/preview/port-animation.mjs';
import {createPortGlow} from '/preview/motion.mjs';
import {mountUplinkBandwidth} from '/preview/uplink-bandwidth.mjs';
import {icon} from '/src/icons.mjs';
const app=document.querySelector('#app'),page='Lighting',session={simulated:false},draft={};
const escape=${escape.toString()};
const number=(value,fallback=0)=>Number.isFinite(Number(value))?Number(value):fallback;
const count=${count.toString()},palette=${JSON.stringify(palette)};
let state={},selected=new Set(),selectionAnchor=null,multipleSelection=false,inspectorCollapsed=false;
let secondaryCleanup,portGlow,telemetryPaintAt=0;
const currentSelectionHint=()=>selectionHint(navigator.platform,multipleSelection,navigator.maxTouchPoints>0);
function allPorts(){return [...state.ports,...state.sfp].map(port=>port.port);}
async function setSelection(ids){
  selected=new Set(ids);
  document.querySelectorAll('[data-port]').forEach(tile=>{
    const active=selected.has(Number(tile.dataset.port));
    tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));
  });
  return true;
}
${shell}
const view=document.querySelector('#view');
${studio}
${ports}
function makeState(spec){
  const copper=spec.copperIds??Array.from({length:count(spec.copper??8)},(_,index)=>index+1);
  const optics=spec.sfpIds??Array.from({length:count(spec.sfp??2)},(_,index)=>copper.length+index+1);
  const entry=(id,index,sfp)=>({port:id,display_rgb:palette[index%palette.length],
    link:{link_up:index%13!==6,speed_mbps:sfp?25000:10000,uplink:Boolean(spec.uplinkLast&&sfp&&id===optics.at(-1))},
    poe:sfp?undefined:{delivering:true,power_w:3.5,pd_class:4},
    controller:{clients:index===2?[{name:'Home Assistant'},{name:'Workstation'}]:[{name:'Device '+id}]}});
  return {connected:true,mode:'running',brightness:100,num_ports:copper.length,sfp_ports:optics,
    switch:{model:copper.length+' Ethernet / '+optics.length+' SFP'},
    ports:copper.map((id,index)=>entry(id,index,false)),sfp:optics.map((id,index)=>entry(id,copper.length+index,true))};
}
state=makeState(${JSON.stringify(configuration)});
renderStudio();
const meter=mountUplinkBandwidth(document.querySelector('#uplink-bandwidth'));
meter.update({...state,uplink_bandwidth:{status:'no_uplink'}},false);
document.querySelector('#group-select').innerHTML='<option>All ports</option>';
document.querySelector('#quick-profile').innerHTML='<select aria-label="Profile"><option>Live</option></select><button class="button quiet">Apply profile</button>';
document.querySelector('#toggle-output').textContent='Release output';
document.querySelector('#inspector').innerHTML='<h2>Port controls</h2>';
document.querySelector('#status').textContent='Lighting output';
document.querySelector('#connection').className='connection-state online';
document.querySelector('#connection').textContent='Switch connected';
function paint(){renderPorts();portGlow?.paint(new Map([...state.ports,...state.sfp].map(port=>[String(port.port),port.display_rgb])));}
paint();
window.topologyFixture={
  setBandwidth(record){meter.update({...state,connected:true,uplink_bandwidth:record});},
  setTopology(spec){state=makeState(spec);selected=new Set();selectionAnchor=null;paint();document.querySelector('#switch-title').textContent=switchTitle(state);},
  get portIds(){return allPorts();},get selected(){return [...selected];}
};
document.documentElement.dataset.fixtureReady='true';
`;
  const headers={'Content-Security-Policy':"default-src 'none'; style-src 'self' 'unsafe-inline'; script-src 'self'; img-src 'self' data:",'Cache-Control':'no-store'};
  return createServer((request,response)=>{
    const url=new URL(request.url,'http://localhost');
    const finish=(type,body,status=200)=>response.writeHead(status,{...headers,'Content-Type':type}).end(body);
    if(request.method!=='GET'){finish('text/plain','Not found',404);return;}
    if(/^\/(?:preview|src)\/[a-z0-9-]+\.(?:mjs|css)$/.test(url.pathname)||url.pathname==='/assets/logo-256.png'){
      try{finish(url.pathname.endsWith('.css')?'text/css; charset=utf-8':url.pathname.endsWith('.png')?'image/png':'text/javascript; charset=utf-8',readFileSync(path.join(root,url.pathname.slice(1))));}
      catch{finish('text/plain','Not found',404);}return;
    }
    if(url.pathname==='/fixture.mjs'){
      try{
        const configuration=url.searchParams.has('topology')?JSON.parse(url.searchParams.get('topology')):{copper:count(url.searchParams.get('copper')??8),sfp:count(url.searchParams.get('sfp')??2)};
        finish('text/javascript; charset=utf-8',runtime(configuration));
      }catch{finish('text/plain','Invalid fixture topology',400);}return;
    }
    if(url.pathname==='/gallery.mjs'){
      const total=Math.max(1,count(url.searchParams.get('gallery')??8));
      finish('text/javascript; charset=utf-8',`import {renderEffectGallery} from '/preview/effect-gallery.mjs';import {CATALOG} from '/src/catalog.mjs';const ports=Array.from({length:${total}},(_,i)=>({port:i+1,source:{kind:'zone'}}));renderEffectGallery(document.querySelector('#gallery'),{effects:CATALOG.filter(effect=>['static','breathing','marquee','gradient','aurora','comet'].includes(effect.id)),state:{num_ports:${total},ports,sfp:[],brightness:100,zones:[{ports:ports.map(port=>port.port),effect:{id:'marquee',params:{color:'#458aff',background:'#df4fb9'}}}]},selectedPorts:ports.map(port=>port.port)});`);return;
    }
    if(url.pathname!=='/'){finish('text/plain','Not found',404);return;}
    const gallery=url.searchParams.has('gallery');
    finish('text/html; charset=utf-8',`<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><title>${gallery?'Effect':'Port'} layout fixture</title><link rel="stylesheet" href="/preview/style.css"></head><body>${gallery?'<main class="secondary-content" id="gallery" style="height:100dvh"></main>':'<div id="app"></div>'}<script type="module" src="/${gallery?'gallery':'fixture'}.mjs${escape(url.search)}"></script></body></html>`);
  });
}

if(process.argv[1]&&import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href){
  const server=createTopologyFixture();
  server.listen(0,'127.0.0.1',()=>console.log(`Fixture: http://127.0.0.1:${server.address().port}/`));
}
