export function watchDesktopParent({input, close, report = () => {}, setTimer = setTimeout}) {
  let task, done = false;
  const stop = () => {
    if (task || done) return;
    task = Promise.resolve().then(close).then(() => { done = true; }, () => {
      report();
      setTimer(() => { task = null; stop(); }, 5000);
    });
  };
  input.on('end', stop);
  input.on('data', chunk => { if (String(chunk).includes('quit')) stop(); });
  input.resume();
  return stop;
}
