import test from 'node:test';
import assert from 'node:assert/strict';
import {createUplinkMeasurements,formatBandwidth, mountUplinkBandwidth, uplinkReading} from '../preview/uplink-bandwidth.mjs';

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
  const measurements=createUplinkMeasurements(), panel=mountUplinkBandwidth(host,environment,measurements), section=host.children[0];
  const readings=section.children[1].children;
  const text=index=>readings[index].children[1].children.map(child=>child.textContent).join(' ').trim();
  return {panel,section,readings,text,frames,timers,doc,host,listeners,motionListeners,environment,measurements,
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
  const graph=ui.section.children[2].children[1].children[0].getAttribute('points');
  ui.update(sample({status:'stale'}));
  assert.equal(ui.readings[0].children[2].textContent,'Peak 1500 Mbps');
  assert.equal(ui.section.children[2].children[0].textContent,'Minute before last reading');
  assert.equal(ui.section.children[2].children[1].children[0].getAttribute('points'),graph);
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
  assert.equal(ui.section.children[0].children[1].textContent,'');
  assert.equal(ui.section.children[0].children[1].hidden,true);
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
  for(const [status,label] of [['waiting','Waiting for reading'],['unavailable','Unavailable'],['stale','Readings delayed'],['no_uplink','No uplink detected']]){
    ui.update(sample({status}));assert.equal(ui.text(1),'—');assert.equal(ui.text(0),'—');
    assert.equal(ui.section.children[0].children[1].textContent,label);assert.equal(ui.frames.size,0);
  }
  ui.update(sample({upload_bps:0,download_bps:0}));assert.equal(ui.text(1),'0.00 Mbps');ui.panel.dispose();
});

test('local freshness expires without another backend event and repeated local renders cannot renew it',()=>{
  const ui=fixture(), record=sample();ui.update(record);ui.tick(5000);ui.update(record);
  assert.equal(ui.section.dataset.status,'ready');ui.tick(5000);
  assert.equal(ui.section.dataset.status,'stale');assert.equal(ui.text(1),'25.00 Mbps');
  assert.equal(ui.section.children[0].children[1].textContent,'Last reading');assert.equal(ui.timers.size,0);ui.panel.dispose();
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
  ui.hide(true);ui.tick(10_000);ui.hide(false);assert.equal(ui.section.dataset.status,'stale');assert.equal(ui.text(1),'80.00 Mbps');ui.panel.dispose();
});

test('partial frames retain the last confirmed values and cannot renew their age',()=>{
  const ui=fixture();ui.update();ui.tick(4000);
  ui.update(sample({sample_age_ms:0})); // Repeated publication of the same sequence.
  ui.update(null);ui.tick(3000);ui.update(sample({status:'unavailable',ports:[]}));
  assert.equal(ui.section.dataset.status,'ready');assert.equal(ui.text(0),'400.00 Mbps');
  ui.tick(3000);assert.equal(ui.section.dataset.status,'stale');assert.equal(ui.text(0),'400.00 Mbps');
  assert.equal(ui.section.children[0].children[1].textContent,'Last reading');
  assert.match(ui.readings[0].children[1].getAttribute('aria-label'),/last reading/);
  ui.update(sample({sequence:2,download_bps:0,upload_bps:0}));ui.tick(700);
  assert.equal(ui.section.dataset.status,'ready');assert.equal(ui.text(0),'0.00 Mbps');
  assert.equal(ui.section.children[0].children[1].hidden,true);ui.panel.dispose();
});

test('a change of uplink, workspace or connectivity never carries a prior measurement across',()=>{
  const ui=fixture(), received={connected:true,uplink_bandwidth:sample()};
  ui.panel.update(received,false,'switch-a');
  ui.panel.update({connected:true,uplink_bandwidth:sample({status:'waiting',ports:[2]})},false,'switch-a');
  assert.equal(ui.text(0),'—');
  ui.panel.update(received,false,'switch-a');
  ui.panel.update({connected:true,uplink_bandwidth:null},false,'switch-b');
  assert.equal(ui.text(0),'—');assert.equal(ui.frames.size,0);
  ui.panel.update(received,false,'switch-b');
  ui.panel.update({connected:false,uplink_bandwidth:received.uplink_bandwidth},false,'switch-b');
  ui.panel.update(received,false,'switch-b');assert.equal(ui.text(0),'—');
  ui.update(sample());ui.tick(10_000);ui.update(sample({status:'no_uplink',ports:[]}));
  assert.equal(ui.text(0),'—');assert.equal(ui.section.children[0].children[1].textContent,'No uplink detected');
  ui.panel.dispose();
});

test('counter and telemetry-generation resets discard same-port readings',()=>{
  const ui=fixture(), readings=sample();
  ui.update(readings,{telemetry:{generation:'first'}});ui.update(null);ui.tick(10_000);
  assert.equal(ui.text(0),'400.00 Mbps'); // A partial frame omitted the generation.
  ui.update(null,{telemetry:{generation:'second'}});
  assert.equal(ui.text(0),'—');assert.equal(ui.readings[0].children[2].textContent,'');
  ui.update(sample(),{telemetry:{generation:'second'}});
  ui.update(sample({status:'waiting',ports:[5]}),{telemetry:{generation:'second'}});
  assert.equal(ui.text(0),'—');ui.panel.dispose();
});

test('returning to Lighting keeps the original measurement time and peak window',()=>{
  const ui=fixture(), record=sample({history:[[-5000,20e6,600e6],[0,25e6,400e6]]});
  ui.update(record);ui.panel.dispose();ui.tick(30_000);
  const panel=mountUplinkBandwidth(ui.host,ui.environment,ui.measurements);
  panel.update({connected:true,uplink_bandwidth:null});
  const [heading,readings]=ui.host.children[0].children;
  assert.equal(heading.children[1].textContent,'Last reading');
  assert.equal(readings.children[0].children[2].textContent,'Peak 600.00 Mbps');
  ui.measurements.reset();
  panel.update({connected:true,uplink_bandwidth:null});
  assert.equal(readings.children[0].children[1].children[0].textContent,'—');panel.dispose();
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
