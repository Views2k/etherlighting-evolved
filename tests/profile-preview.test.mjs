import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {profileColors,profilePorts,profileLayoutNotice,createProfilePreviews} from '../preview/profile-preview.mjs';
import {defaultConfig} from '../preview/effect-renderer.mjs';
import {rgbCss} from '../preview/port-animation.mjs';
import {rendererDom} from './fixtures/renderer-dom.mjs';

const scene = (effect='static', params={}) => ({brightness:100,zones:[{id:'all',ports:[1,2],effect:{...defaultConfig(effect),params:{...defaultConfig(effect).params,...params}}}],rules:[],overrides:{},sfp:{9:{mode:'color',effect:'two_color',color:'#0000ff',secondary_color:'#ff0000',period:6,brightness:100,color_brightness:100,secondary_brightness:100},10:{mode:'off'}},sfp_sync:{mode:'independent'}});

test('saved traffic previews use actual telemetry rather than empty facts',()=>{
  const saved=scene('traffic'), state={ports:[{port:1,facts:{link:true,speed:1000,traffic:0}},{port:2,facts:{link:true,speed:1000,traffic:1000}}],sfp:[]};
  const colors=profileColors(saved,state,1.2);
  assert.notDeepEqual(colors[1],colors[2]);
  assert.ok(colors[2].some(value=>value>0));
});

test('profile preview includes rule priority, manual overrides, and independent brightness',()=>{
  const saved=scene('static',{color:'#ffffff'});saved.brightness=20;
  saved.rules=[{enabled:true,ports:[1,2],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ff0000',brightness:50}}];
  saved.overrides={2:{color:'#0000ff',brightness:80}};
  const colors=profileColors(saved,{ports:[{port:1,facts:{link:true}},{port:2,facts:{link:true}}]},1);
  assert.deepEqual(colors[1],[127.5,0,0]);assert.deepEqual(colors[2],[0,0,204]);
});

test('saved SFP effects animate, share settings, and follow Ethernet colors',()=>{
  const saved=scene('static',{color:'#00ff00'});
  const a=profileColors(saved,{},0),b=profileColors(saved,{},3);
  assert.notDeepEqual(a[9],b[9]);assert.deepEqual(a[10],[0,0,0]);
  saved.sfp_sync={mode:'together'};
  assert.deepEqual(profileColors(saved,{},2)[9],profileColors(saved,{},2)[10]);
  saved.sfp_sync={mode:'ethernet',source_port:1};
  const together=profileColors(saved,{},1);assert.deepEqual(together[9],together[1]);assert.deepEqual(together[10],together[1]);
});

test('saved preview includes ungrouped copper ports and reported SFP identifiers',()=>{
  const saved=scene();saved.zones[0].ports=[3,1,2];saved.sfp={17:{mode:'off'},18:{mode:'off'}};
  assert.deepEqual(profilePorts(saved,{num_ports:16}),{copper:Array.from({length:16},(_,index)=>index+1),sfp:[17,18]});
});

const parity = JSON.parse(readFileSync(new URL('./fixtures/profile-colors.json',import.meta.url),'utf8'));
for (const fixture of parity) test(`backend profile color parity: ${fixture.name}`,()=>{
  const colors=profileColors(fixture.scene,fixture.state,0);
  assert.deepEqual(colors,fixture.colors);
});

test('native SFP interpolation uses rule brightness even when the saved master is zero',()=>{
  const saved=scene();saved.brightness=0;
  saved.sfp[9]={mode:'color',effect:'pulse',color:'#0000ff',period:4,brightness:100,color_brightness:100};
  saved.rules=[{enabled:true,ports:[9],conditions:[],look:{style:'effect',brightness:80}}];
  const state={switch:{sfp_native_gradient:true},output:{gamma:1},sfp:[{port:9,playback:{}}]};
  assert.deepEqual(profileColors(saved,state,0)[9],[0,0,204]);
  assert.deepEqual(profileColors(saved,state,1)[9],[0,0,102]);
});

test('active profile uses confirmed live colors and cleans up its animation',context=>{
  const dom=rendererDom(context),queue=new Map(),observers=[];let sequence=0,now=0;
  const env={document:dom.doc,performance:{now:()=>now},matchMedia:()=>({matches:true,addEventListener(){},removeEventListener(){}}),
    requestAnimationFrame:callback=>{queue.set(++sequence,callback);return sequence;},cancelAnimationFrame:id=>queue.delete(id),
    IntersectionObserver:class {constructor(callback){this.callback=callback;observers.push(this);}observe(target){this.callback([{target,isIntersecting:true}]);}disconnect(){}}};
  const previews=createProfilePreviews({ports:[{port:1,display_rgb:[200,30,40]},{port:2,display_rgb:[5,120,255]}],sfp:[{port:9,display_rgb:[80,30,200]},{port:10,display_rgb:[0,0,0]}]},env);
  const element=previews.add(scene('traffic'),true);dom.host.append(element);
  const flush=()=>{const work=[...queue.values()];queue.clear();for(const callback of work)callback(now);};
  flush();assert.equal(element.children.length,4);assert.equal(element.children[0].style.backgroundColor,rgbCss([200,30,40]));assert.equal(element.children[2].style.backgroundColor,rgbCss([80,30,200]));
  assert.equal(queue.size,0);
  now=200;previews.update({ports:[{port:1,display_rgb:[20,210,60]}],sfp:[]});flush();
  assert.equal(element.children.length,1);
  assert.equal(element.children[0].style.backgroundColor,rgbCss([20,210,60]));
  previews.dispose();assert.equal(queue.size,0);
});

test('saved topology stays with the profile when a different switch is connected',()=>{
  const saved={...scene(),version:3,topology:{version:1,model:'eight',copper_ports:[1,2,3,4,5,6,7,8],sfp_ports:[9,10]}};
  const ports=profilePorts(saved,{num_ports:48,ports:Array.from({length:48},(_,i)=>({port:i+1})),sfp:[{port:49},{port:50}]});
  assert.deepEqual(ports,{copper:[1,2,3,4,5,6,7,8],sfp:[9,10]});
  assert.equal(new Set([...ports.copper,...ports.sfp]).size,10);
});

test('legacy profile roles stay separate on a larger switch and explain the layout difference',()=>{
  const saved=scene(),state={num_ports:24};
  assert.deepEqual(profilePorts(saved,state),{copper:[1,2,3,4,5,6,7,8],sfp:[9,10]});
  assert.match(profileLayoutNotice(saved,state),/older profile.*different port layout/i);
});

test('optical-only saved profiles never invent copper ports or overwrite an optical color',()=>{
  const saved={...scene(),version:3,zones:[],topology:{version:1,copper_ports:[],sfp_ports:[1,2]},sfp:{1:{mode:'color',effect:'steady',color:'#00ff00'},2:{mode:'off'}}};
  assert.deepEqual(profilePorts(saved,{}),{copper:[],sfp:[1,2]});
  assert.deepEqual(profileColors(saved,{},0),{1:[0,255,0],2:[0,0,0]});
  saved.topology.sfp_ports=[1,1];
  assert.deepEqual(profilePorts(saved,{}),{copper:[],sfp:[]});
  assert.match(profileLayoutNotice(saved,{}),/Review/);
});
