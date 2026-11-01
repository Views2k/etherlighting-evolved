// Opt-in browser verification. It records timing only and never sends API requests.
if (new URLSearchParams(location.search).has('motion-check')) {
  const result = document.createElement('output');
  result.id = 'motion-check-results'; result.hidden = true; document.body.append(result);
  const results = {};
  let route = '', last = 0, warmup = 240, samples = [], longTasks = 0, costs = [];
  window.addEventListener('ee-render-sample', event => {
    if (event.detail?.kind === route && warmup === 0 && !results[route]) costs.push(event.detail.ms);
  }, {capture:true});
  let observer;
  if (PerformanceObserver.supportedEntryTypes.includes('longtask')) {
    observer = new PerformanceObserver(entries => { longTasks += entries.getEntries().length; });
    observer.observe({type:'longtask'});
  }
  function tick(now) {
    const next = document.querySelector('[data-page][aria-current="page"]')?.dataset.page || '';
    if (next !== route) { route = next; last = 0; warmup = 240; samples = []; longTasks = 0; costs = []; }
    if (last && route && !document.hidden && !results[route]) {
      if (warmup > 0) warmup--;
      else samples.push(now-last);
      if (samples.length === 1200) {
        const sorted = [...samples].sort((a,b)=>a-b), median=sorted[Math.floor(sorted.length/2)], renderCosts=[...costs].sort((a,b)=>a-b);
        results[route] = {
          frames:samples.length, medianMs:median, p95Ms:sorted[Math.floor(sorted.length*.95)],
          maxMs:sorted.at(-1), delayedFrames:samples.filter(value=>value>median*1.5).length,
          longTasks, renderP95Ms:renderCosts[Math.floor(renderCosts.length*.95)] ?? null,
          renderMaxMs:renderCosts.at(-1) ?? null,
          viewport:[innerWidth,innerHeight], colorSpace:CSS.supports('color','color(srgb .1 .2 .3)')
        };
        result.textContent = JSON.stringify(results);
      }
    }
    last=now; requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
}
