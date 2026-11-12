const WINDOW_MS = 60_000;
const FRAME_MS = 1000 / 30;
const validRate = value => Number.isFinite(value) && value >= 0;

// History offsets are relative to the newest switch counter reading. Reuse an
// overlapping reading to keep network arrival jitter out of the chart's clock.
export function mountBandwidthPlot(chart, lines, environment, reducedMotion) {
  const now = () => environment.performance.now();
  let points = [], capture = 0, sequence = null, period = 0, gap = 0;
  let cursor = null, scale = 0, paintedAt = 0, frame = 0, stale = false;
  let visible = false, disposed = false;
  const observer = new environment.IntersectionObserver(entries => {
    if (disposed) return;
    visible = entries[entries.length - 1].isIntersecting;
    redraw(true);
  });
  observer.observe(chart);

  function cancel() {
    if (frame) environment.cancelAnimationFrame(frame);
    frame = 0;
  }

  function draw(time, reset) {
    if (!points.length) return false;
    const moving = visible && !chart.ownerDocument.hidden && !reducedMotion.matches && !stale && points.length > 1;
    const elapsed = Math.max(0, time - paintedAt);
    const target = Math.min(capture, time - period - 250);
    if (!moving) cursor = capture;
    else if (reset || cursor === null) cursor = target;
    else {
      const drift = target - cursor - elapsed;
      const speed = Math.max(0.9, Math.min(1.1, 1 + drift / 1000));
      cursor = Math.min(capture, cursor + elapsed * speed);
    }
    paintedAt = time;
    const start = cursor - WINDOW_MS;
    const segments = [];
    let peak = 1;
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1], b = points[i];
      if (a[0] >= cursor) break;
      if (b[0] <= start || b[0] - a[0] > gap) continue;
      const left = Math.max(start, a[0]), right = Math.min(cursor, b[0]);
      function clip(t) {
        const f = (t - a[0]) / (b[0] - a[0]);
        return [240 * (t - start) / WINDOW_MS, a[1] + f * (b[1] - a[1]), a[2] + f * (b[2] - a[2])];
      }
      const from = clip(left), to = clip(right);
      peak = Math.max(peak, from[1], from[2], to[1], to[2]);
      segments.push({i, from, to});
    }
    // Follow rising peaks as they appear. Ease a falling axis after a peak
    // leaves the window so the rest of the trace doesn't jump in one frame.
    scale = reset || !moving || peak >= scale ? peak : peak + (scale - peak) * Math.exp(-elapsed / 400);
    for (let direction = 0; direction < lines.length; direction++) {
      const column = 2 - direction; // History is [time, upload, download].
      const xy = point => `${point[0].toFixed(3)} ${(34 - 32 * point[column] / scale).toFixed(3)}`;
      let path = '', previous = -1;
      for (const {i, from, to} of segments) {
        if (previous !== i - 1) path += `M${xy(from)}`;
        path += `L${xy(to)}`;
        previous = i;
      }
      lines[direction].setAttribute('d', path);
    }
    return moving && cursor < capture;
  }

  function animate(time) {
    frame = 0;
    if (!visible || chart.ownerDocument.hidden) return;
    if (time - paintedAt < FRAME_MS - 1 || draw(time, false)) frame = environment.requestAnimationFrame(animate);
  }

  function redraw(reset) {
    cancel();
    if (!visible || chart.ownerDocument.hidden || !points.length) return;
    if (draw(now(), reset)) frame = environment.requestAnimationFrame(animate);
  }

  return {
    update(history, reading, capturedAt, delayed) {
      const rows = [];
      for (const row of Array.isArray(history) ? history : []) {
        if (Array.isArray(row) && row.length === 3 && Number.isFinite(row[0]) && row[0] <= 0 && row[0] > -WINDOW_MS
          && validRate(row[1]) && validRate(row[2]) && (!rows.length || row[0] > rows.at(-1)[0])) rows.push(row);
      }
      const steps = reading ? reading.sequence - sequence : 0;
      const shared = rows[rows.length - 1 - steps];
      const last = points.at(-1);
      const overlap = last && shared && Number.isInteger(steps) && steps >= 0
        && shared[1] === last[1] && shared[2] === last[2];
      if (overlap && cursor === capture) paintedAt = now();
      capture = overlap ? capture - shared[0] : capturedAt;
      const interval = reading?.period ?? 0;
      const cadence = reading?.refresh ?? interval;
      gap = 2.5 * cadence;
      const estimate = Math.min(interval, gap);
      period = overlap ? period + (estimate - period) * 0.15 : estimate;
      const start = rows.length ? capture + rows[0][0] : capture;
      // The buffered display can still show the leading edge that the newest
      // one-minute snapshot has dropped. Retain just that overlap.
      const leading = overlap ? points.filter(p => p[0] < start && p[0] >= start - period - gap - 250) : [];
      points = leading.concat(rows.map(([offset, up, down]) => [capture + offset, up, down]));
      sequence = reading?.sequence ?? null;
      stale = delayed;
      if (!overlap) cursor = null;
      chart.hidden = rows.length < 2;
      if (!rows.length) lines.forEach(line => line.setAttribute('d', ''));
      redraw(!overlap);
      return rows;
    },
    visibilityChanged() { redraw(true); },
    dispose() { disposed = true; cancel(); observer.disconnect(); },
  };
}
