// Offscreen checks for the native window. Nothing here opens a window on screen.
// Usage: EtherlightingEvolved.Native.Check [--output dir] [--live http://127.0.0.1:4180 --seconds 60]
// --live only reads from a running workspace; it sends no commands.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EtherlightingEvolved.Native;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var output = Path.GetFullPath(Arg("--output") ?? "native-check");
Directory.CreateDirectory(output);
var failures = new List<string>();
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}");
    if (!ok) failures.Add(name);
}

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

// 1. Payload reading
const string frame = """
{"type":"frame","connected":true,"ports":[
 {"port":1,"display_rgb":[0,108.4,255],"name":"Core","link":{"admin_up":true,"link_up":true,"speed_mbps":10000,"uplink":false},
  "poe":{"delivering":true,"power_w":21.21},"controller":{"clients":[{"name":"Printer","name_source":"assigned"},{"name":"Desk dock","name_source":"assigned"}],"count":2}},
 {"port":2,"display_rgb":null,"name":"","link":{"admin_up":false,"link_up":false,"speed_mbps":0}},
 {"port":3,"preview":"#ff0000","output":{"color":"#00ff00","level":50},"link":{"link_up":true,"speed_mbps":25000}},
 {"port":4,"output":{"color":"#808080","level":100},"link":{"link_up":true,"speed_mbps":2500,"uplink":true}},
 {"port":5,"mode":"unmanaged","output":{"color":"#ffffff"},"link":{"link_up":false}},
 {"port":"6"},{"port":7.5}
],"sfp":[{"port":9,"display_rgb":[255,0,0],"link":false,"mode":"color"},{"port":1,"display_rgb":[1,2,3]}]}
""";
List<PortView> ParseFrame(string json)
{
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    return WorkspaceModel.Ports(root.GetProperty("ports"), root.GetProperty("sfp"), 2.2);
}
var ports = ParseFrame(frame);
PortView Port(int id) => ports.First(port => port.Port == id);
Check(ports.Count == 6, "ports with invalid numbers and SFP entries that repeat a copper port are skipped");
Check(Port(1) is { R: 0, G: 108, B: 255, Link: "10 GbE", Power: "21.2 W", Online: true }, "reported display color, speed and PoE are used as sent");
Check(Port(1).Label == "Desk dock +1", "assigned client names are sorted and summarized");
Check(Port(2) is { R: 0, G: 0, B: 0, Link: "Disabled", Online: false, Label: "Port 2" }, "an unreported color is dark and an admin-down port is disabled");
Check(Port(3) is { R: 255, G: 0, B: 0, Link: "25 GbE" }, "a staged preview takes precedence over the output at full level");
Check(Port(4) is { R: 186, G: 186, B: 186, Label: "Uplink", Uplink: true, Link: "2.5 GbE" }, "the output color is gamma encoded and the uplink is labeled");
Check(Port(5) is { R: 0, G: 0, B: 0, Link: "Disconnected" }, "an unmanaged port without a rule is dark");
Check(Port(9) is { Sfp: true, R: 255, Label: "SFP 9", Link: "Disconnected" }, "SFP cages are labeled and accept a boolean link");

var client = new WorkspaceClient(new Uri("http://127.0.0.1:9/"));
var published = new List<WorkspaceSnapshot>();
client.Changed += published.Add;
using (var document = JsonDocument.Parse(frame)) client.Handle(document.RootElement);
Check(client.Frames == 1 && published.Count == 1 && published[0] is { Live: true, Status: "Live" } && published[0].Ports.Count == 6,
    "a stream frame produces one live snapshot");
using (var document = JsonDocument.Parse("""{"type":"session","session":{"logged_in":false}}""")) client.Handle(document.RootElement);
Check(published[^1] is { Live: false, Ports.Count: 0 } && published[^1].Status.Contains("not signed in"), "a signed-out session clears the ports and says why");
using (var document = JsonDocument.Parse("""{"type":"session","session":{"logged_in":true}}"""))
    Check(client.Handle(document.RootElement), "signing in again asks for a full reload");
await client.DisposeAsync();

// 2. Redraw only on visible change
var map = new PortMap();
Check(map.Update(ports), "the first set of ports is drawn");
var invalidations = map.InvalidateCount;
for (var i = 0; i < 100; i++) map.Update(ParseFrame(frame));
Check(map.InvalidateCount == invalidations, "100 identical frames cause no redraw");
var moved = ports.Select(port => port.Port == 1 ? port with { G = 120 } : port).ToList();
Check(map.Update(moved) && map.InvalidateCount == invalidations + 1, "one changed color causes one redraw");
Check(map.Update(moved.Select(port => port.Port == 3 ? port with { Label = "Office" } : port).ToList()), "a changed label causes a redraw");

// 3. Offscreen rendering
static List<PortView> Synthetic(int copper, int optical)
{
    var result = new List<PortView>();
    for (var i = 1; i <= copper + optical; i++)
    {
        var hue = (i - 1) * 360.0 / (copper + optical);
        var (r, g, b) = Hsv(hue);
        var sfp = i > copper;
        result.Add(new PortView(i, sfp, r, g, b, i == 1 ? "Uplink" : sfp ? $"SFP {i}" : $"Device {i}",
            i % 7 == 0 ? "Disconnected" : sfp ? "10 GbE" : "2.5 GbE", i % 7 != 0, i == 1, i % 3 == 0 && !sfp ? "7.5 W" : ""));
    }
    return result;
}
static (byte, byte, byte) Hsv(double hue)
{
    var x = 1 - Math.Abs(hue / 60 % 2 - 1);
    (double r, double g, double b) = hue switch
    {
        < 60 => (1d, x, 0d), < 120 => (x, 1d, 0d), < 180 => (0d, 1d, x), < 240 => (0d, x, 1d), < 300 => (x, 0d, 1d), _ => (1d, 0d, x),
    };
    return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
}
static (byte R, byte G, byte B) Pixel(WriteableBitmap bitmap, int x, int y)
{
    using var buffer = bitmap.Lock();
    var value = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
    return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
        ? ((byte)value, (byte)(value >> 8), (byte)(value >> 16))
        : ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}

foreach (var (copper, optical, width, height) in new[] { (8, 2, 1200, 660), (48, 4, 1440, 1200), (0, 32, 1440, 1000) })
{
    var window = new MainWindow(App.Workspace, null) { Width = width, Height = height };
    window.Show();
    var shown = Synthetic(copper, optical);
    window.Apply(new WorkspaceSnapshot($"Synthetic {copper} + {optical}", "Live", true, shown));
    Dispatcher.UIThread.RunJobs();
    var bitmap = window.CaptureRenderedFrame();
    Check(bitmap is not null, $"{copper} + {optical} ports render offscreen");
    if (bitmap is null) continue;
    var file = Path.Combine(output, $"port-map-{copper}-{optical}.png");
    bitmap.Save(file);
    Check(window.Map.TileCount == copper + optical, $"{copper} + {optical}: every port has a tile");
    var sampled = shown.Take(3).All(port =>
    {
        var socket = window.Map.SocketOf(port.Port);
        if (window.Map.TranslatePoint(new Point(socket.X + 4, socket.Center.Y), window) is not { } at) return false;
        var (r, g, b) = Pixel(bitmap, (int)at.X, (int)at.Y);
        var ok = Math.Abs(r - port.R) <= 8 && Math.Abs(g - port.G) <= 8 && Math.Abs(b - port.B) <= 8;
        if (!ok) Console.WriteLine($"      port {port.Port}: expected {port.R},{port.G},{port.B} drawn {r},{g},{b}");
        return ok;
    });
    Check(sampled, $"{copper} + {optical}: drawn socket colors match the reported colors");
    window.Close();
}

// 4. Optional: follow a running workspace (read only) and measure this process
if (Arg("--live") is { } live)
{
    var seconds = int.Parse(Arg("--seconds") ?? "60");
    var follower = new WorkspaceClient(new Uri(live));
    var window = new MainWindow(new Uri(live), null) { Width = 1200, Height = 660 };
    window.Show();
    WorkspaceSnapshot? pending = null;
    using var signal = new SemaphoreSlim(0);
    follower.Changed += snapshot =>
    {
        Volatile.Write(ref pending, snapshot);
        if (signal.CurrentCount == 0) signal.Release();
    };
    follower.Start();
    var process = Process.GetCurrentProcess();
    var started = process.TotalProcessorTime;
    var clock = Stopwatch.StartNew();
    int applied = 0, redrawn = 0;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        if (!signal.Wait(250)) continue;
        if (Interlocked.Exchange(ref pending, null) is not { } snapshot) continue;
        applied++;
        var before = window.Map.InvalidateCount;
        window.Apply(snapshot);
        Dispatcher.UIThread.RunJobs();
        if (window.Map.InvalidateCount == before) continue;
        window.CaptureRenderedFrame();
        redrawn++;
    }
    process.Refresh();
    var cpu = (process.TotalProcessorTime - started).TotalSeconds / clock.Elapsed.TotalSeconds * 100;
    window.CaptureRenderedFrame()?.Save(Path.Combine(output, "live.png"));
    Console.WriteLine($"live {live}: {follower.Frames} frames ({follower.Frames / clock.Elapsed.TotalSeconds:0.0}/s), {applied} applied, " +
                      $"{redrawn} redrawn, this process {cpu:0.0}% of one core, {process.PrivateMemorySize64 / 1048576} MB");
    Check(follower.Frames > 0, "the live workspace streams frames to the native client");
    follower.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

Console.WriteLine(failures.Count == 0 ? $"All checks passed. Images: {output}" : $"{failures.Count} check(s) failed.");
return failures.Count == 0 ? 0 : 1;
