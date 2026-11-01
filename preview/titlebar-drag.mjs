const interactive = 'button,a,input,select,textarea,label,summary,[role="button"],[role="tab"],[contenteditable="true"]';

export function bindTitlebarDrag(titlebar, action, events = window) {
  let start = null;
  const background = event => event.target?.closest('.titlebar') === titlebar && !event.target.closest(interactive);
  const invoke = name => Promise.resolve(action(name)).catch(() => {});
  const clear = () => { start = null; };
  const down = event => {
    clear();
    if (event.button !== 0 || event.isPrimary === false || event.detail > 1 || !background(event)) return;
    start = {id:event.pointerId, x:event.screenX, y:event.screenY};
  };
  const move = event => {
    if (!start || event.pointerId !== start.id) return;
    if (!(event.buttons & 1)) { clear(); return; }
    if (Math.hypot(event.screenX - start.x, event.screenY - start.y) < 4) return;
    clear();
    event.preventDefault();
    invoke('drag');
  };
  const double = event => {
    clear();
    if (event.button === 0 && background(event)) invoke('toggle_maximize');
  };
  titlebar.addEventListener('pointerdown', down);
  titlebar.addEventListener('dblclick', double);
  events.addEventListener('pointermove', move, {passive:false});
  events.addEventListener('pointerup', clear);
  events.addEventListener('pointercancel', clear);
  events.addEventListener('blur', clear);
  return () => {
    clear();
    titlebar.removeEventListener('pointerdown', down);
    titlebar.removeEventListener('dblclick', double);
    events.removeEventListener('pointermove', move);
    events.removeEventListener('pointerup', clear);
    events.removeEventListener('pointercancel', clear);
    events.removeEventListener('blur', clear);
  };
}
