using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Pages.Lighting;

internal sealed class BandwidthPlot : Control
{
    static readonly Pen Download = new(new SolidColorBrush(Color.Parse("#74b9ff")), 1.6);
    static readonly Pen Upload = new(new SolidColorBrush(Color.Parse("#eb80c0")), 1.6);
    readonly List<(double Time, double Up, double Down)> values = [];
    double scale = 1;

    public BandwidthPlot()
    {
        Width = 175;
        Height = 32;
        ClipToBounds = true;
    }

    public bool HasHistory => values.Count >= 2;

    public void Update(IReadOnlyList<JsonArray> records)
    {
        values.Clear();
        scale = 1;
        foreach (var row in records)
        {
            var up = row[1].Num()!.Value;
            var down = row[2].Num()!.Value;
            values.Add((1 + row[0].Num()!.Value / 60000, up, down));
            scale = Math.Max(scale, Math.Max(up, down));
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Math.Max(0, Bounds.Height - 2);
        Point At(double time, double rate) => new(time * width, height - rate / scale * (height - 2));
        for (var i = 1; i < values.Count; i++)
        {
            var a = values[i - 1];
            var b = values[i];
            context.DrawLine(Download, At(a.Time, a.Down), At(b.Time, b.Down));
            context.DrawLine(Upload, At(a.Time, a.Up), At(b.Time, b.Up));
        }
    }
}
