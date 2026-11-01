import {bindTitlebarDrag} from './titlebar-drag.mjs';

export async function connectDesktopChrome(root = document, host = window) {
  const bridge = host.pywebview?.api;
  if (!bridge?.window_state || !bridge?.window_action) return false;
  const state = await bridge.window_state();
  if (!state.frameless) return false;
  const controls = root.querySelector('.traffic-lights');
  if (!controls || controls.dataset.connected) return false;
  controls.dataset.connected = 'true';
  controls.removeAttribute('aria-hidden');
  controls.setAttribute('aria-label', 'Window controls');
  const labels = {close: 'Hide to notification area', minimize: 'Minimize', maximize: 'Maximize'};
  for (const [kind, label] of Object.entries(labels)) {
    const old = controls.querySelector(`.${kind}`), button = root.createElement('button');
    button.type = 'button'; button.className = `traffic-light ${kind}`;
    button.setAttribute('aria-label', label);
    button.addEventListener('click', () => bridge.window_action(kind === 'maximize' ? 'toggle_maximize' : kind).catch(() => {}));
    old.replaceWith(button);
  }
  const update = value => controls.querySelector('.maximize').setAttribute('aria-label', value.maximized ? 'Restore window' : 'Maximize');
  update(state);
  host.addEventListener('ee-window-state', event => update(event.detail));
  const titlebar = root.querySelector('.titlebar');
  if (titlebar) bindTitlebarDrag(titlebar, action => bridge.window_action(action), host);
  root.documentElement.classList.add('native-window');
  return true;
}

if (typeof window !== 'undefined') {
  const connect = () => connectDesktopChrome().catch(() => {});
  window.addEventListener('pywebviewready', connect);
  connect();
}
