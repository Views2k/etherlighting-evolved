import test from 'node:test';
import assert from 'node:assert/strict';
import {formatBandwidth, mountUplinkBandwidth, uplinkReading} from '../preview/uplink-bandwidth.mjs';

const sample=(overrides={})=>({status:'ready',ports:[5],upload_bps:25_000_000,download_bps:400_000_000,
  sample_period_ms:5000,sample_age_ms:0,sequence:1,refresh_interval_ms:5000,...overrides});

function fixture() {
  let time=0, next=0;
  const frames=new Map(), timers=new Map(), listeners=new Map(), motionListeners=new Map();
  const doc={hidden:false,activeElement:{name:'color input'},
    addEventListener:(type,fn)=>listeners.set(type,fn),removeEventListener:type=>listeners.delete(type)};
  class Element {
    constructor(tag){this.tagName=tag;this.children=[];this.attributes=new Map();this.dataset={};this.textContent='';this.ownerDocument=doc;}
    append(...children){for(const child of children){child.parentNode=this;this.children.push(child);}}
    setAttribute(name,value){this.attributes.set(name,String(value));}
    getAttribute(name){return this.attributes.get(name);}
    remove(){this.parentNode.children=this.parentNode.children.filter(child=>child!==this);}
  }
  doc.createElement=tag=>new Element(tag);
  doc.createElementNS=(_,tag)=>new Element(tag);
  const host=new Element('div'), media={matches:false,addEventListener:(type,fn)=>motionListeners.set(type,fn),removeEventListener:type=>motionListeners.delete(type)};
  const environment={document:doc,performance:{now:()=>time},matchMedia:()=>media,
    requestAnimationFrame:fn=>{const id=++next;frames.set(id,fn);return id;},cancelAnimationFrame:id=>frames.delete(id),
    setTimeout:(fn,delay)=>{const id=++next;timers.set(id,{fn,at:time+delay});return id;},clearTimeout:id=>timers.delete(id)};
  const panel=mountUplinkBandwidth(host,environment), section=host.children[0];
  const readings=section.children[1].children;
  const text=index=>readings[index].children[1].children.map(child=>child.textContent).join(' ').trim();
  return {panel,section,readings,text,frames,timers,doc,host,listeners,motionListeners,
    update:(record=sample(),state={})=>panel.update({connected:true,uplink_bandwidth:record,...state}),
    tick(ms){time+=ms;for(const [id,fn] of [...frames]){frames.delete(id);fn(time);}for(const [id,timer] of [...timers])if(timer.at<=time){timers.delete(id);timer.fn();}},
    hide(value){doc.hidden=value;listeners.get('visibilitychange')?.();},
    reduced(value){media.matches=value;motionListeners.get('change')?.();}};
}

test('bandwidth uses decimal network units without presenting tiny positive traffic as zero',()=>{
  assert.deepEqual(formatBandwidth(0),{value:'0.00',unit:'Mbps'});
  assert.deepEqual(formatBandwidth(125),{value:'<0.01',unit:'Mbps'});
  assert.deepEqual(formatBandwidth(10_000),{value:'0.01',unit:'Mbps'});
  assert.deepEqual(formatBandwidth(42_420_000),{value:'42.42',unit:'Mbps'});
  assert.deepEqual(formatBandwidth(1_250_000_000),{value:'1250',unit:'Mbps'});
  for(const invalid of [null,undefined,'5000',-1,NaN,Infinity])assert.deepEqual(formatBandwidth(invalid),{value:'—',unit:''});
});

test('a speed test remains visible after the live sample returns to idle',()=>{
  const ui=fixture();
  ui.update(sample({upload_bps:500_000,download_bps:750_000,history:[
    [-10_000,1_000_000,1_500_000_000],[-5000,200_000_000,750_000],[0,500_000,750_000]]}));
  assert.equal(ui.text(0),'0.75 Mbps');
  assert.equal(ui.readings[0].children[2].textContent,'Peak 1500 Mbps');
  assert.equal(ui.readings[1].children[2].textContent,'Peak 200.00 Mbps');
  assert.equal(ui.section.children[2].hidden,false);
  ui.update(sample({status:'stale'}));
  assert.equal(ui.readings[0].children[2].textContent,'');
  assert.equal(ui.section.children[2].hidden,true);
  ui.panel.dispose();
});

test('uplink data requires complete measured rates and fresh sample timing',()=>{
  assert.equal(uplinkReading(sample()).status,'ready');
  for(const record of [null,sample({upload_bps:null}),sample({download_bps:-1}),sample({sample_age_ms:null}),
    sample({sample_period_ms:0}),sample({ports:[]}),sample({ports:[0]}),sample({status:'invented'})]) {
    assert.equal(uplinkReading(record).status,'unavailable');
  }
  assert.equal(uplinkReading(sample({sample_age_ms:9999})).status,'ready');
  assert.equal(uplinkReading(sample({sample_age_ms:9999}),{elapsedMs:1}).status,'stale');
  assert.equal(uplinkReading(sample(),{connected:false}).status,'unavailable');
  assert.equal(uplinkReading(sample(),{disconnected:true}).status,'unavailable');
});

test('a first confirmed sample displays directly and keeps focus and DOM nodes intact',()=>{
  const ui=fixture(), focus=ui.doc.activeElement, original=[...ui.readings];
  ui.update();assert.equal(ui.text(1),'25.00 Mbps');assert.equal(ui.text(0),'400.00 Mbps');
  assert.deepEqual(ui.readings.map(item=>item.children[0].children[1].textContent),['Download','Upload']);
  assert.equal(ui.frames.size,0);assert.equal(ui.doc.activeElement,focus);
  assert.deepEqual(ui.section.children[1].children,original);
  assert.match(ui.section.title,/Port 5.*Upload leaves.*download enters/);
  assert.match(ui.section.children[0].children[1].textContent,/5 s average.*5 s/);
  assert.equal(ui.readings[1].children[1].getAttribute('aria-label'),'Upload: 25.00 Mbps');
  ui.panel.dispose();
});

test('new measurements ease monotonically to the confirmed target and then stop requesting frames',()=>{
  const ui=fixture();ui.update(sample({upload_bps:10_000_000}));
  ui.update(sample({sequence:2,upload_bps:110_000_000,download_bps:100_000_000}));
  const observed=[];
  for(let i=0;i<6;i++){ui.tick(100);observed.push(Number.parseFloat(ui.text(1)));}
  assert.ok(observed[0]>10&&observed[0]<110);
  assert.ok(observed.every((value,index)=>index===0||value>=observed[index-1]));
  assert.equal(observed.at(-1),110);assert.equal(ui.text(0),'100.00 Mbps');assert.equal(ui.frames.size,0);
  assert.equal(ui.readings[1].children[1].getAttribute('aria-label'),'Upload: 110.00 Mbps');
  ui.panel.dispose();
});

test('repeated frame messages for one measurement do not restart its transition',()=>{
  const ui=fixture();ui.update(sample({upload_bps:10_000_000}));
  ui.update(sample({sequence:2,upload_bps:110_000_000}));
  for(let age=100;age<=600;age+=100){ui.tick(100);ui.update(sample({sequence:2,upload_bps:110_000_000,sample_age_ms:age}));}
  assert.equal(ui.text(1),'110.00 Mbps');assert.equal(ui.frames.size,0);ui.panel.dispose();
});

test('waiting, missing, stale, and absent uplinks show a status instead of invented zeros',()=>{
  const ui=fixture();
  for(const [status,label] of [['waiting','Waiting for samples'],['unavailable','Unavailable'],['stale','Readings delayed'],['no_uplink','No uplink detected']]){
    ui.update(sample({status}));assert.equal(ui.text(1),'—');assert.equal(ui.text(0),'—');
    assert.equal(ui.section.children[0].children[1].textContent,label);assert.equal(ui.frames.size,0);
  }
  ui.update(sample({upload_bps:0,download_bps:0}));assert.equal(ui.text(1),'0.00 Mbps');ui.panel.dispose();
});

test('local freshness expires without another backend event and repeated local renders cannot renew it',()=>{
  const ui=fixture(), record=sample();ui.update(record);ui.tick(5000);ui.update(record);
  assert.equal(ui.section.dataset.status,'ready');ui.tick(5000);
  assert.equal(ui.section.dataset.status,'stale');assert.equal(ui.text(1),'—');assert.equal(ui.timers.size,0);ui.panel.dispose();
});

test('websocket loss immediately clears retained rates even when the engine state said connected',()=>{
  const ui=fixture(), state={connected:true,uplink_bandwidth:sample()};ui.panel.update(state);
  ui.panel.update(state,true);assert.equal(ui.section.dataset.status,'unavailable');assert.equal(ui.text(1),'—');
  assert.equal(ui.frames.size,0);assert.equal(ui.timers.size,0);ui.panel.dispose();
});

test('hidden pages stop animation and settle to fresh confirmed data on return',()=>{
  const ui=fixture();ui.update();ui.update(sample({sequence:2,upload_bps:80_000_000}));ui.tick(100);
  ui.hide(true);assert.equal(ui.frames.size,0);assert.equal(ui.timers.size,0);
  ui.tick(400);ui.update(sample({sequence:2,upload_bps:80_000_000,sample_age_ms:500}));assert.equal(ui.frames.size,0);
  ui.hide(false);assert.equal(ui.text(1),'80.00 Mbps');assert.equal(ui.frames.size,0);
  ui.hide(true);ui.tick(10_000);ui.hide(false);assert.equal(ui.section.dataset.status,'stale');assert.equal(ui.text(1),'—');ui.panel.dispose();
});

test('reduced motion uses confirmed targets immediately and cancels an in-progress transition',()=>{
  const ui=fixture();ui.update();ui.update(sample({sequence:2,upload_bps:80_000_000}));ui.tick(100);
  ui.reduced(true);assert.equal(ui.text(1),'80.00 Mbps');assert.equal(ui.frames.size,0);
  ui.update(sample({sequence:3,upload_bps:90_000_000}));assert.equal(ui.text(1),'90.00 Mbps');assert.equal(ui.frames.size,0);ui.panel.dispose();
});

test('disposing a panel removes its timers, observers, and elements',()=>{
  const ui=fixture();ui.update();ui.update(sample({sequence:2,upload_bps:80_000_000}));ui.panel.dispose();
  assert.equal(ui.frames.size,0);assert.equal(ui.timers.size,0);assert.equal(ui.listeners.size,0);
  assert.equal(ui.motionListeners.size,0);assert.equal(ui.host.children.length,0);
  ui.update();assert.equal(ui.frames.size,0);
});
