export function workspaceStorageKey(session) {
  return session?.logged_in && /^[a-f\d]{64}$/.test(session.workspace_id || '')
    ? `ee.workspace.v2.${session.workspace_id}` : null;
}

export function connectionLabel(session, disconnected = false, busy = false) {
  if (disconnected) return 'Reconnecting…';
  if (session?.connecting) return 'Connecting…';
  if (!session?.logged_in) return 'Not connected';
  if (session.reconnect_required) return 'Switch connection needs attention';
  if (busy) return 'Saving…';
  if (session.simulated) return 'Demo session';
  return session.connected ? 'Switch connected' : 'Switch reconnecting…';
}

export function switchConnectionStatus(session, disconnected = false, busy = false) {
  const label = connectionLabel(session, disconnected, busy);
  let tone = 'is-disconnected';
  if (!session?.simulated) {
    if (session?.reconnect_required && !disconnected) tone = 'error';
    else if (disconnected || session?.connecting || (session?.logged_in && session.connected !== true)) tone = 'waiting';
    else if (session?.logged_in === true && session.connected === true) tone = 'online';
  }
  return {label, tone};
}

export function controllerConnectionStatus(controller, unavailable = false) {
  if (unavailable) return {label:'Controller status unavailable', tone:'is-disconnected'};
  if (controller?.connected === true) {
    return controller.stale
      ? {label:'Controller connected · Waiting for fresh data', tone:'waiting'}
      : {label:'Controller connected', tone:'online'};
  }
  // EE retries a controller that does not answer on its own, so that is not an error for the user to fix.
  if (controller?.reconnect_state === 'retrying') return {label:'Controller reconnecting…', tone:'waiting'};
  return controller?.error
    ? {label:'Controller connection needs attention', tone:'error'}
    : {label:'Controller not connected', tone:'is-disconnected'};
}
