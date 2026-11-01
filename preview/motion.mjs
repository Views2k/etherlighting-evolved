export const MOTION = Object.freeze({quick:120, change:200, feedback:320, settle:600});
export const EASE_OUT = 'cubic-bezier(.2,.7,.2,1)';
const animations = new Set();
globalThis.document?.addEventListener('visibilitychange', () => {
  if (document.hidden) for (const animation of animations) animation.cancel();
});
function animate(element, frames, duration) {
  try {
    const animation = element.animate(frames,{duration,easing:EASE_OUT});
    animations.add(animation);
    animation.finished.catch(() => {}).finally(() => animations.delete(animation));
    return animation;
  } catch { return null; }
}

export function enterInspector(element) {
  if (!element || document.hidden || matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  element.getAnimations().forEach(animation => animation.cancel());
  animate(element,[{opacity:.65,transform:'translateY(6px)'},{opacity:1,transform:'translateY(0)'}],MOTION.change);
}

export function confirmPorts(ports) {
  if (document.hidden || matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  for (const port of new Set(ports)) {
    if (!Number.isInteger(Number(port))) continue;
    const light = document.querySelector(`.port-tile[data-port="${Number(port)}"] .port-led`);
    if (!light) continue;
    light.querySelector('.port-confirmation')?.remove();
    const bloom = document.createElement('span');
    bloom.className = 'port-confirmation'; bloom.setAttribute('aria-hidden','true'); light.append(bloom);
    const animation = animate(bloom,[{opacity:0},{opacity:.75,offset:.2},{opacity:0}],MOTION.feedback);
    if (animation) animation.finished.catch(() => {}).finally(() => bloom.remove());
    else bloom.remove();
  }
}

export function createPortGlow(map, environment = globalThis) {
  const canvas = map.ownerDocument.createElement('canvas');
  canvas.className = 'port-glow'; canvas.setAttribute('aria-hidden','true'); map.prepend(canvas);
  const context = canvas.getContext('2d');
  if (!context) { canvas.remove(); return {paint(){},dispose(){}}; }
  let geometry = [], disposed = false, lastColors = '', currentColors = new Map();
  const paint = colors => {
    if (disposed) return;
    currentColors = colors;
    if (environment.document.hidden || !geometry.length) return;
    const samples = geometry.map(point => colors.get(point.port)?.map(channel => Math.max(0,Math.min(255,channel)).toFixed(1)).join(' ') || '0 0 0');
    const signature = samples.join(';');
    if (signature === lastColors) return;
    lastColors = signature;
    context.clearRect(0,0,canvas.width,canvas.height);
    for (const [index,point] of geometry.entries()) {
      const rgb = colors.get(point.port);
      if (!rgb || !rgb.some(channel => channel > .5)) continue;
      const channels = samples[index];
      const glow = context.createRadialGradient(point.x,point.y,0,point.x,point.y,point.radius);
      glow.addColorStop(0,`rgb(${channels} / .55)`);
      glow.addColorStop(1,`rgb(${channels} / 0)`);
      context.fillStyle = glow;
      context.fillRect(point.x-point.radius,point.y-point.radius,point.radius*2,point.radius*2);
    }
  };
  const measure = () => {
    if (disposed || !map.isConnected || environment.document.hidden) return;
    const bounds = map.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return;
    canvas.width = Math.min(400, Math.ceil(bounds.width / 3));
    canvas.height = Math.min(140, Math.ceil(bounds.height / 3));
    lastColors = '';
    canvas.style.transform = `translate(${map.scrollLeft}px,${map.scrollTop}px)`;
    const x = canvas.width / bounds.width, y = canvas.height / bounds.height;
    geometry = [...map.querySelectorAll('.port-tile')].map(tile => {
      const rect = tile.querySelector('.port-led').getBoundingClientRect();
      return {port:tile.dataset.port,x:(rect.left-bounds.left+rect.width/2)*x,
        y:(rect.top-bounds.top+rect.height/2)*y,radius:Math.max(18,rect.width*x*1.2)};
    });
    paint(currentColors);
  };
  const observer = new environment.ResizeObserver(measure);
  observer.observe(map);
  map.querySelectorAll('.port-bank').forEach(bank => observer.observe(bank));
  map.addEventListener('scroll',measure,{passive:true,capture:true});
  environment.document.addEventListener('visibilitychange',measure);
  measure();
  return {
    paint,
    dispose() {
      disposed = true; observer.disconnect(); map.removeEventListener('scroll',measure,true);
      environment.document.removeEventListener('visibilitychange',measure); canvas.remove(); geometry = []; currentColors = new Map();
    }
  };
}
