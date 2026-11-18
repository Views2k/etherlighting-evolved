// "exit" comes from a desktop that is quitting after its own close was not confirmed. That attempt was made, so the
// gateway calls exit instead of retrying, and the input closing afterward starts nothing.
export function watchDesktopParent({input, close, report = () => {}, exhausted = () => {}, exit = () => {},
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
  input.on('data', chunk => {
    const text = String(chunk);
    if (/\bexit\b/.test(text)) {
      if (!done) { done = true; exit(); }
    } else if (text.includes('quit')) stop();
  });
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

// Runs one last step, then finishes once it settles or the time is up, whichever comes first.
export function finishAfter(step, timeoutMs, finish, setTimer = setTimeout) {
  let finished = false;
  const end = () => { if (!finished) { finished = true; finish(); } };
  setTimer(end, timeoutMs);
  Promise.resolve().then(step).then(end, end);
}
