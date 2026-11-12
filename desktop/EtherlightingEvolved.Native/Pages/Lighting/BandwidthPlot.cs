using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Pages.Lighting;

internal sealed class BandwidthPlot : Control
{
    static readonly Pen Download = new(new SolidColorBrush(Color.Parse("#74b9ff")), 1.6);
    static readonly Pen Upload = new(new SolidColorBrush(Color.Parse("#eb80c0")), 1.6);
    readonly BandwidthTimeline trace = new();
    readonly Func<long> time;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
    StreamGeometry? uploadLine, downloadLine;
    double drawnScale, deadline;
    Rect drawnBounds;
    Rect? viewport;
    bool active, attached;

    public BandwidthPlot() : this(() => Environment.TickCount64) { }

    internal BandwidthPlot(Func<long> time)
    {
        this.time = time;
        Height = 32;
        ClipToBounds = true;
        timer.Tick += (_, _) => Tick(time());
        AttachedToVisualTree += (_, _) => { attached = true; Tick(time()); };
        DetachedFromVisualTree += (_, _) => { attached = false; viewport = null; timer.Stop(); };
        EffectiveViewportChanged += (_, e) =>
        {
            viewport = e.EffectiveViewport;
            Tick(time());
        };
    }

    public bool HasHistory => trace.Points.Count >= 2;
    internal bool IsAnimating => timer.IsEnabled;

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        var now = time();
        if (value) trace.Resume(now);
        Tick(now);
    }

    public void Update(UplinkSample? sample, double now)
    {
        trace.Update(sample, now);
        downloadLine = uploadLine = null;
        InvalidateVisual();
    }

    public void SetDeadline(double value, double now)
    {
        var changed = value != deadline;
        deadline = value;
        if (changed || timer.IsEnabled != ShouldAnimate(now)) Tick(now);
    }

    Rect VisiblePlot => viewport is { } clip ? new Rect(Bounds.Size).Intersect(clip) : new Rect(Bounds.Size);
    bool ShouldAnimate(double now) => active && attached && IsEffectivelyVisible && (viewport is null || VisiblePlot.Height >= 4)
        && now < deadline && trace.Moving(now);

    void Tick(double now)
    {
        if (ShouldAnimate(now)) timer.Start();
        else timer.Stop();
        if (active && attached && IsEffectivelyVisible) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var visible = VisiblePlot;
        if (!HasHistory || visible.Width <= 0 || visible.Height < 4) return;
        var graph = new Rect(0, visible.Y, Bounds.Width, visible.Height);
        var now = time();
        var fresh = now < deadline;
        var scale = fresh ? trace.Scale(now) : trace.FinalScale;
        if (downloadLine is null || drawnScale != scale || drawnBounds != graph)
        {
            downloadLine = BuildLine(graph, scale, true);
            uploadLine = BuildLine(graph, scale, false);
            drawnScale = scale;
            drawnBounds = graph;
        }
        var shift = fresh ? -trace.Offset(now) * Bounds.Width / BandwidthTimeline.Window : 0;
        using (context.PushTransform(Matrix.CreateTranslation(shift, 0)))
        {
            context.DrawGeometry(null, Download, downloadLine);
            context.DrawGeometry(null, Upload, uploadLine!);
        }
    }

    StreamGeometry BuildLine(Rect graph, double scale, bool down)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        double? previous = null;
        foreach (var point in trace.Points)
        {
            var xy = new Point((1 + point.Offset / BandwidthTimeline.Window) * graph.Width,
                graph.Bottom - 2 - (down ? point.Down : point.Up) / scale * (graph.Height - 4));
            if (previous is null || !trace.Joins(previous.Value, point.Offset))
            {
                if (previous is not null) path.EndFigure(false);
                path.BeginFigure(xy, false);
            }
            else path.LineTo(xy);
            previous = point.Offset;
        }
        if (previous is not null) path.EndFigure(false);
        return geometry;
    }
}
