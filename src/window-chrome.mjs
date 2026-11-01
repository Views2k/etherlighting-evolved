export function initWindowChrome(onError) {
  const controls = document.querySelector('.window-controls');
  const titlebar = document.querySelector('.window-titlebar');
  const maximize = controls.querySelector('[data-window-action="toggle_maximize"]');
  let api = null;
  let busy = false;

  function update(state) {
    const maximized = state?.maximized === true;
    document.body.classList.toggle('window-maximized', maximized);
    maximize.title = maximized ? 'Restore' : 'Maximize';
    maximize.setAttribute('aria-label', maximized ? 'Restore window' : 'Maximize window');
  }

  async function connect() {
    const candidate = window.pywebview?.api;
    if (typeof candidate?.window_action !== 'function' || typeof candidate?.window_state !== 'function') return;
    try {
      const state = await candidate.window_state();
      if (state?.frameless !== true || typeof state.maximized !== 'boolean') return;
      update(state);
      api = candidate;
      document.body.classList.add('native-window');
      titlebar.hidden = false;
      controls.hidden = false;
    } catch {
      onError('Window controls could not connect. Reopen the app to try again.');
    }
  }

  async function act(action) {
    if (!api || busy) return;
    busy = true;
    try {
      await api.window_action(action);
      update(await api.window_state());
    } catch {
      onError('The window action could not be completed. Try again.');
    } finally {
      busy = false;
    }
  }

  controls.addEventListener('click', event => {
    const button = event.target.closest('[data-window-action]');
    if (button) act(button.dataset.windowAction);
  });
  document.querySelector('.window-drag-region').addEventListener('dblclick', () => act('toggle_maximize'));
  window.addEventListener('ee-window-state', event => { if (api) update(event.detail); });
  window.addEventListener('focus', async () => {
    if (api) { try { update(await api.window_state()); } catch { /* The next window event refreshes the state. */ } }
  });
  window.addEventListener('pywebviewready', connect);
  connect();
}
