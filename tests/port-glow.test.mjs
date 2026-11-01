import test from 'node:test';
import assert from 'node:assert/strict';
import {createPortGlow} from '../preview/motion.mjs';

function fixture() {
  const listeners=new Map(), gradients=[];
  let clears=0, removed=false, disconnected=false, resize;
  const context={clearRect(){clears++;},fillRect(){},createRadialGradient(){
    const stops=[];gradients.push(stops);return {addColorStop:(position,color)=>stops.push([position,color])};
  }};
  const canvas={style:{},setAttribute(){},getContext:()=>context,remove(){removed=true;}};
  const document={hidden:false,createElement:()=>canvas,addEventListener:(name,fn)=>listeners.set(name,fn),removeEventListener:name=>listeners.delete(name)};
  const map={ownerDocument:document,isConnected:true,scrollLeft:0,scrollTop:0,prepend(){},
    getBoundingClientRect:()=>({left:0,top:0,width:800,height:300}),
    querySelectorAll:selector=>selector==='.port-tile'?[{dataset:{port:'1'},querySelector:()=>({getBoundingClientRect:()=>({left:50,top:100,width:76,height:76})})}]:[],
    addEventListener:(name,fn)=>listeners.set(name,fn),removeEventListener:name=>listeners.delete(name)};
  const environment={document,ResizeObserver:class {constructor(fn){resize=fn;}observe(){}disconnect(){disconnected=true;}}};
  return {map,environment,canvas,listeners,gradients,resize:()=>resize(),clears:()=>clears,removed:()=>removed,disconnected:()=>disconnected};
}

test('glow uses displayed channels and skips identical frames',()=>{
  const f=fixture(),glow=createPortGlow(f.map,f.environment);
  const colors=new Map([['1',[22.25,100.5,255]]]);
  glow.paint(colors);
  assert.equal(f.gradients.at(-1)[0][1],'rgb(22.3 100.5 255.0 / .55)');
  const before=f.clears();
  glow.paint(colors);glow.paint(new Map(colors));
  assert.equal(f.clears(),before);
  glow.paint(new Map([['1',[0,0,0]]]));
  assert.equal(f.clears(),before+1);
  assert.equal(f.gradients.length,1,'dark ports have no artificial light');
  glow.dispose();
});

test('resize and scroll repaint held colors without waiting for a network frame',()=>{
  const f=fixture(),glow=createPortGlow(f.map,f.environment);
  glow.paint(new Map([['1',[10,100,200]]]));
  const before=f.clears();
  f.resize();
  assert.equal(f.clears(),before+1);
  f.map.scrollLeft=120;f.listeners.get('scroll')();
  assert.equal(f.canvas.style.transform,'translate(120px,0px)');
  assert.equal(f.clears(),before+2);
  assert.equal(f.gradients.at(-1)[0][1],'rgb(10.0 100.0 200.0 / .55)');
  glow.dispose();
});

test('hidden and disposed canvases do not draw and all observers are released',()=>{
  const f=fixture(),glow=createPortGlow(f.map,f.environment),before=f.clears();
  f.environment.document.hidden=true;
  glow.paint(new Map([['1',[100,20,255]]]));f.resize();
  assert.equal(f.clears(),before);
  f.environment.document.hidden=false;f.listeners.get('visibilitychange')();
  assert.equal(f.clears(),before+1);
  glow.dispose();glow.dispose();
  const after=f.clears();glow.paint(new Map([['1',[255,255,255]]]));f.resize();
  assert.equal(f.clears(),after);
  assert.equal(f.listeners.size,0);
  assert.equal(f.disconnected(),true);
  assert.equal(f.removed(),true);
});
