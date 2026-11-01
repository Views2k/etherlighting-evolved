export function createWorkspaceCloser({release, child, closePrivate, timeoutMs = 20000}) {
  let released = false, quitRequested = false, privateClosed = false;

  function waitForExit() {
    if (child.exitCode !== null || child.signalCode != null) {
      return child.exitCode === 0 ? Promise.resolve() : Promise.reject(new Error('shutdown_unconfirmed'));
    }
    return new Promise((resolve, reject) => {
      const finish = error => {
        clearTimeout(timer);
        child.removeListener('exit', onExit);
        child.removeListener('error', onError);
        child.stdin.removeListener('error', onError);
        error ? reject(error) : resolve();
      };
      const onExit = code => finish(code === 0 ? null : new Error('shutdown_unconfirmed'));
      const onError = () => finish(new Error('shutdown_unconfirmed'));
      const timer = setTimeout(() => finish(new Error('shutdown_timeout')), timeoutMs);
      child.once('exit', onExit);
      child.once('error', onError);
      child.stdin.once('error', onError);
      if (!quitRequested) {
        quitRequested = true;
        try { child.stdin.end('quit\n'); } catch { onError(); }
      }
    });
  }

  return async function closeWorkspace() {
    if (!released) { await release(); released = true; }
    await waitForExit();
    if (!privateClosed) { await closePrivate(); privateClosed = true; }
  };
}
