export function watchDesktopParent({input, close, report = () => {}, exhausted = () => {},
  setTimer = setTimeout, maxAttempts = 4, retryMs = 5000}) {
  if (!Number.isSafeInteger(maxAttempts) || maxAttempts < 1 || maxAttempts > 10) throw new RangeError('Choose 1–10 cleanup attempts.');
  let task, done = false, attempts = 0;
  const stop = () => {
    if (task || done) return;
    attempts++;
    task = Promise.resolve().then(close).then(() => { done = true; }, () => {
      report({attempts, maxAttempts});
      if (attempts >= maxAttempts) {
        done = true;
        exhausted({attempts});
      } else setTimer(() => { task = null; stop(); }, retryMs);
    });
  };
  input.on('end', stop);
  input.on('data', chunk => { if (String(chunk).includes('quit')) stop(); });
  input.resume();
  return stop;
}

// After an unconfirmed shutdown the gateway can still support recovery while
// either its desktop or engine is alive. Once both are gone it only holds ports.
export function whenWorkspaceGone(input, engine, finish) {
  function waitForEngine() {
    if (engine.exitCode !== null || engine.signalCode != null) finish();
    else engine.once('exit', finish);
  }
  if (input.readableEnded) waitForEngine();
  else input.once('end', waitForEngine);
}
