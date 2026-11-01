import {createServer} from 'node:http';
import {readFileSync} from 'node:fs';
import {linkStatus, portPower, isUplink, portLabel} from '../../preview/port-map.mjs';
import {portDisplayRgb} from '../../preview/port-display.mjs';
import {portInk} from '../../preview/port-animation.mjs';

const source = readFileSync(new URL('../../preview/app.mjs',import.meta.url),'utf8');
const functions = source.slice(source.indexOf('function speedMarkup('),source.indexOf('function renderPorts('));
const escape = value => String(value??'').replace(/[&<>"']/g, char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const render = new Function('selected','state','number','linkStatus','portPower','isUplink','portLabel','portDisplayRgb','portInk','escape', `${functions};return portMarkup;`)
  (new Set(),{},Number,linkStatus,portPower,isUplink,portLabel,portDisplayRgb,portInk,escape);
const palette = [[67,99,255],[144,72,230],[227,71,167],[41,182,216]];
const server = createServer((request,response)=>{
  const url = new URL(request.url,'http://localhost');
  if(request.method==='GET' && /^\/(preview|src)\/[a-z0-9-]+\.mjs$/.test(url.pathname)) {
    try {
      const module=readFileSync(new URL(`../..${url.pathname}`,import.meta.url),'utf8');
      response.writeHead(200,{'Content-Type':'text/javascript; charset=utf-8'}).end(module);
    } catch {response.writeHead(404).end();}
    return;
  }
  if(request.method!=='GET'||url.pathname!=='/'){response.writeHead(404).end();return;}
  const css = readFileSync(new URL('../../preview/style.css',import.meta.url),'utf8');
  if(url.searchParams.has('gallery')) {
    const count=Math.max(1,Math.min(64,Number(url.searchParams.get('gallery'))||8));
    response.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Content-Security-Policy':"default-src 'none'; style-src 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src data:"});
    response.end(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Effect layout fixture</title><style>${css}</style><main class="secondary-content" id="gallery" style="height:100dvh"></main><script type="module">import {renderEffectGallery} from '/preview/effect-gallery.mjs';import {CATALOG} from '/src/catalog.mjs';const ports=Array.from({length:${count}},(_,i)=>({port:i+1,source:{kind:'zone'}}));renderEffectGallery(document.querySelector('#gallery'),{effects:CATALOG.filter(effect=>['static','breathing','marquee','gradient','aurora','comet'].includes(effect.id)),state:{num_ports:${count},ports,sfp:[],brightness:100,zones:[{ports:ports.map(port=>port.port),effect:{id:'marquee',params:{color:'#458aff',background:'#df4fb9'}}}]},selectedPorts:ports.map(port=>port.port)});</script>`);
    return;
  }
  const count=Math.max(0,Math.min(64,Number(url.searchParams.get('copper')??8)));
  const optics=Math.max(0,Math.min(64,Number(url.searchParams.get('sfp')??2)));
  const ports=Array.from({length:count+optics},(_,i)=>({port:i+1,display_rgb:palette[i%4],
    link:{link_up:true,speed_mbps:i<count?10000:25000},
    controller:{clients:i===2?[{name:'Home Assistant'},{name:'Workstation'}]:[{name:`Device ${i+1}`}]} }));
  const bank=(entries,sfp=false)=>`<section class="port-group ${sfp?'sfp-group':''} ${sfp&&!count?'optical-only':''}"><div class="port-group-heading"><h2>${sfp?'SFP':'Ethernet'}</h2></div><div class="port-bank ${sfp?'sfp-bank':'copper-bank'} ${entries.length>16?'dense-bank':''} ${sfp&&(optics>4||!count)?'wide-bank':''}" style="--port-count:${entries.length}">${entries.map(port=>render(port,sfp)).join('')}</div></section>`;
  response.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Content-Security-Policy':"default-src 'none'; style-src 'unsafe-inline'; img-src data:"});
  response.end(`<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Port layout fixture</title><style>${css}html,body{height:auto;overflow:auto}.fixture{max-width:1500px;margin:24px auto}.port-map{background:linear-gradient(135deg,#202b3c,#2d2538);border-radius:12px}h1{padding:16px 24px}</style><main class="fixture"><h1>${count} Ethernet · ${optics} SFP</h1><div class="canvas-area"><div class="port-map">${count?bank(ports.slice(0,count)):''}${optics?bank(ports.slice(count),true):''}</div></div></main>`);
});
server.listen(0,'127.0.0.1',()=>console.log(`Fixture: http://127.0.0.1:${server.address().port}/`));
