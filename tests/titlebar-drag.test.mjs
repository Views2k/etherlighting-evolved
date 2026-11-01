import test from 'node:test';
import assert from 'node:assert/strict';
import {bindTitlebarDrag} from '../preview/titlebar-drag.mjs';

function fixture() {
  const titlebar = new EventTarget(), events = new EventTarget(), calls = [];
  const stop = bindTitlebarDrag(titlebar, action => {calls.push(action); return Promise.resolve();}, events);
  const send = (type, values = {}, control = false) => {
    const event = new Event(type, {cancelable:true});
    Object.defineProperties(event, Object.fromEntries(Object.entries({button:0, buttons:1, isPrimary:true,
      pointerId:1, detail:1, screenX:20, screenY:20, ...values,
      target:{closest:selector => selector === '.titlebar' ? titlebar : control ? {} : null},
    }).map(([key,value]) => [key,{value}])));
    (['pointerdown','dblclick'].includes(type) ? titlebar : events).dispatchEvent(event);
  };
  return {send,calls,stop};
}

test('blank titlebar starts one native move after the drag threshold', () => {
  const {send,calls} = fixture();
  send('pointerdown'); send('pointermove',{screenX:22}); assert.deepEqual(calls,[]);
  send('pointermove',{screenX:27}); send('pointermove',{screenX:40}); assert.deepEqual(calls,['drag']);
});
test('navigation and window buttons never start a window drag', () => {
  const {send,calls} = fixture();
  send('pointerdown',{},true); send('pointermove',{screenX:40}); send('dblclick',{},true);
  assert.deepEqual(calls,[]);
});
test('titlebar double-click maximizes without dragging', () => {
  const {send,calls} = fixture();
  send('pointerdown',{detail:2}); send('pointermove',{screenX:40}); send('dblclick');
  assert.deepEqual(calls,['toggle_maximize']);
});
test('release, cancellation and focus loss cannot leave a pending drag', () => {
  for (const end of ['pointerup','pointercancel','blur']) {
    const {send,calls} = fixture();
    send('pointerdown'); send(end); send('pointermove',{screenX:40}); assert.deepEqual(calls,[]);
  }
});
test('right-click, secondary touch, another pointer and released mouse are ignored', () => {
  for (const values of [{button:2},{isPrimary:false}]) {
    const {send,calls} = fixture(); send('pointerdown',values); send('pointermove',{screenX:40}); assert.deepEqual(calls,[]);
  }
  const {send,calls,stop} = fixture(); send('pointerdown'); send('pointermove',{pointerId:2,screenX:40});
  send('pointermove',{buttons:0,screenX:40}); send('pointermove',{screenX:40}); assert.deepEqual(calls,[]);
  send('pointerdown'); stop(); send('pointermove',{screenX:40}); assert.deepEqual(calls,[]);
});
