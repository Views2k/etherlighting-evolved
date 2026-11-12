namespace EtherlightingEvolved.Native.Core;

// History offsets are measured on the switch sampling clock. Keep that spacing when the UI
// receives samples at uneven intervals, and reveal only the part of the trace already measured.
internal sealed class BandwidthTimeline
{
    public const double Window = 60_000;
    UplinkSample? sample;
    List<BandwidthPoint> points = [];
    double newest, origin, clock, interval, delay, speed = 1;
    double scaleFrom = 1, scaleTo = 1, allPointsPeak = 1, scaleDuration;

    public IReadOnlyList<BandwidthPoint> Points => points;
    public double GapLimit { get; private set; }
    public double Cursor(double now) => Math.Min(newest, origin + Math.Max(0, now - clock) * speed);
    public double Offset(double now) => Cursor(now) - newest;
    public double FinalScale => scaleTo;
    public bool Joins(double left, double right) => right > left && right - left <= GapLimit;
    public double Scale(double now)
    {
        var scale = now >= clock + scaleDuration || scaleDuration <= 0 ? scaleTo
            : scaleFrom + (scaleTo - scaleFrom) * Math.Clamp((now - clock) / scaleDuration, 0, 1);
        if (scale >= allPointsPeak) return scale;
        // A rolling peak can still be visible at the delayed left edge. Include the clipped
        // endpoints of each visible segment so neither line disappears above the plot.
        var right = Offset(now);
        var left = right - Window;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (b.Offset < left || a.Offset > right || !Joins(a.Offset, b.Offset)) continue;
            double At(double time)
            {
                var fraction = (time - a.Offset) / (b.Offset - a.Offset);
                return Math.Max(a.Up + (b.Up - a.Up) * fraction, a.Down + (b.Down - a.Down) * fraction);
            }
            scale = Math.Max(scale, Math.Max(At(Math.Max(a.Offset, left)), At(Math.Min(b.Offset, right))));
        }
        return scale;
    }
    public bool Moving(double now) => points.Count >= 2 && (Cursor(now) < newest ||
        scaleFrom != scaleTo && now < clock + scaleDuration);

    public void Resume(double now)
    {
        if (sample is null || points.Count == 0) return;
        origin = Math.Clamp(now - delay, newest + points[0].Offset, newest);
        clock = now;
        speed = 1;
        scaleFrom = scaleTo;
    }

    public void Update(UplinkSample? next, double now)
    {
        var previous = sample;
        sample = next;
        if (next is null || next.History.Count == 0)
        {
            points = [];
            return;
        }
        var index = previous is null ? -1 : next.History.Count - 1L - ((long)next.Sequence - previous.Sequence);
        var shared = previous is not null && previous.Series == next.Series && next.Sequence > previous.Sequence &&
            index >= 0 && index < next.History.Count && previous.History.Count > 0 &&
            next.History[(int)index].Up == previous.History[^1].Up && next.History[(int)index].Down == previous.History[^1].Down;
        var cursor = Cursor(now);
        var scale = Scale(now);
        var advertised = Math.Clamp(next.Interval, 100, 10_000);
        var priorPeriods = new List<double>(7);
        // Exclude the incoming interval: a missed poll must not widen its own gap threshold.
        for (var i = Math.Max(1, next.History.Count - 8); i < next.History.Count - 1; i++)
        {
            var spacing = next.History[i].Offset - next.History[i - 1].Offset;
            if (spacing > 0) priorPeriods.Add(spacing);
        }
        if (priorPeriods.Count >= 3)
        {
            priorPeriods.Sort();
            interval = Math.Max(advertised, priorPeriods[(priorPeriods.Count - 1) / 2]);
        }
        else if (!shared) interval = advertised;
        if (next.Period >= advertised && next.Period <= interval * 2)
            interval += (next.Period - interval) * .2;
        GapLimit = Math.Max(advertised, interval) * 2.5;
        delay = Math.Clamp(interval + 250, 350, 9500);
        var incoming = new List<BandwidthPoint>(next.History.Count + 8);
        if (shared)
        {
            // Keep the extra left edge used by the presentation delay after the server trims its minute.
            var elapsed = -next.History[(int)index].Offset;
            foreach (var point in points)
            {
                var offset = point.Offset - elapsed;
                if (offset < next.History[0].Offset && offset >= -Window - delay - GapLimit)
                    incoming.Add(point with { Offset = offset });
            }
            newest += elapsed;
        }
        else newest = next.CapturedAt;
        incoming.AddRange(next.History);
        points = incoming;
        var target = Math.Clamp(now - delay, newest + points[0].Offset, newest);
        // A resumed stream can deliver a whole window at once. Begin at the current measured
        // segment instead of replaying a disconnected or hidden interval in slow motion.
        origin = shared && newest - cursor <= 3 * delay ? Math.Min(cursor, newest) : target;
        speed = Math.Clamp(1 + (target - origin) / delay, .9, 1.1);
        clock = now;
        var peak = Math.Max(1, next.History.Max(p => Math.Max(p.Up, p.Down)));
        allPointsPeak = Math.Max(peak, incoming.Max(p => Math.Max(p.Up, p.Down)));
        scaleFrom = shared ? scale : peak;
        scaleTo = peak;
        scaleDuration = Math.Min(interval, 600);
    }
}
