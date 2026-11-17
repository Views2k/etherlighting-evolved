using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Pages.Lighting;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>
/// The Lighting page's port tiles, rendered off screen: a tile repaints its light only when the color changes, and the
/// drawn light matches the reported color for copper and SFP ports.
/// </summary>
internal static class PortTileChecks
{
    static JsonObject Port(int id, string name, int speed) => new()
    {
        ["port"] = id, ["display_name"] = name,
        ["link"] = new JsonObject { ["admin_up"] = true, ["link_up"] = true, ["speed_mbps"] = speed },
    };

    static (byte R, byte G, byte B) Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var value = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? ((byte)value, (byte)(value >> 8), (byte)(value >> 16))
            : ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public static void Run(Action<bool, string> check, string output)
    {
        var copper = new PortTile(1, false, (_, _) => { });
        var optical = new PortTile(9, true, (_, _) => { });
        var window = new Window
        {
            Width = 420, Height = 300,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { copper, optical } },
        };
        window.Show();
        try
        {
            copper.Read(Port(1, "Core", 10000));
            optical.Read(Port(9, "Storage", 25000));
            copper.PaintColor([0, 108.4, 255]);
            optical.PaintColor([255, 0, 0]);
            Dispatcher.UIThread.RunJobs();

            var repaints = copper.LedRepaints;
            for (var i = 0; i < 100; i++) copper.PaintColor([0, 108.4, 255]);
            check(copper.LedRepaints == repaints, "100 identical port colors cause no repaint");
            copper.PaintColor([0, 120, 255]);
            check(copper.LedRepaints == repaints + 1, "one changed port color causes one repaint");
            static string Name(PortTile tile) => AutomationProperties.GetName(tile) ?? "";
            var named = Name(copper);
            copper.Read(Port(1, "Core", 10000));
            check(named.StartsWith("Port 1 · Core · ") && Name(copper) == named, "a port tile names its port and label");
            copper.Read(Port(1, "Office", 10000));
            check(Name(copper).StartsWith("Port 1 · Office · "), "a changed label updates the tile");
            check(Name(optical).StartsWith("SFP 9 · Storage · "), "an SFP tile is labeled as SFP");

            Dispatcher.UIThread.RunJobs();
            using var bitmap = window.CaptureRenderedFrame();
            check(bitmap is not null, "port tiles render off screen");
            if (bitmap is null) return;
            bitmap.Save(Path.Combine(output, "port-tiles.png"));
            foreach (var (tile, expected) in new[] { (copper, (R: 0, G: 120, B: 255)), (optical, (R: 255, G: 0, B: 0)) })
            {
                var socket = tile.GetVisualDescendants().OfType<Panel>().First(panel => panel.GetType().Name == "Socket");
                var at = socket.TranslatePoint(new Point(1, socket.Bounds.Height * .35), window);
                var (r, g, b) = at is { } point ? Pixel(bitmap, (int)point.X, (int)point.Y) : ((byte)0, (byte)0, (byte)0);
                var ok = at is not null && Math.Abs(r - expected.R) <= 8 && Math.Abs(g - expected.G) <= 8 && Math.Abs(b - expected.B) <= 8;
                if (!ok) Console.WriteLine($"      {(tile.Sfp ? "SFP" : "copper")} tile: expected {expected.R},{expected.G},{expected.B} drawn {r},{g},{b}");
                check(ok, $"the drawn {(tile.Sfp ? "SFP" : "copper")} port light matches the reported color");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
