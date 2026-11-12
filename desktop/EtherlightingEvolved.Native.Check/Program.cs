// Offscreen checks for the native window. Nothing here opens a window on screen.
// Usage: EtherlightingEvolved.Native.Check [--output dir] [--service] [--live http://127.0.0.1:4180 --seconds 60]
// --service starts the simulator from this source checkout and changes its lighting; it never contacts a switch.
// --live only reads from a running workspace; it sends no commands.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EtherlightingEvolved.Native;
using EtherlightingEvolved.Native.Check.Checks;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var output = Path.GetFullPath(Arg("--output") ?? "native-check");
Directory.CreateDirectory(output);
Locations.TestDataDirectory = Path.Combine(output, "client");
var failures = new List<string>();
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}");
    if (!ok) failures.Add(name);
}

ModelChecks.Run(Check);
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

ShellChecks.Contracts(Check);

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

// 1b. Lighting setup
const string stateJson = """
{"brightness":70,"mode":"running","zones":[
 {"id":"all","name":"All ports","ports":[1,2,3,4,5,6,7,8],"effect":{"id":"breathing","params":{"color":"#ff0000","period":4}}},
 {"id":"sfp","name":"SFP","ports":[9,10],"effect":{"id":"static","params":{"color":"#0000ff"}}}],
 "overrides":{"3":{"color":"#00ff00","brightness":40},"x":{"color":"#ffffff"}},
 "sfp":[{"port":9,"mode":"color","effect":"pulse","color":"#ff00ff","secondary_color":"#000000","brightness":80,"period":6,"effect_zone":null},
        {"port":10,"mode":"unmanaged","effect_zone":"sfp"}]}
""";
LightingState lighting;
using (var document = JsonDocument.Parse(stateJson)) lighting = LightingModel.State(document.RootElement, LightingState.Empty);
Check(lighting is { Brightness: 70, Released: false, Zones.Count: 2 } && lighting.ZoneOf(5)?.Id == "all" && lighting.Zones[0].Params["period"]?.GetValue<int>() == 4,
    "groups, their effects and master brightness are read");
Check(lighting.Overrides.Count == 1 && lighting.Overrides[3] == new ManualColor("#00ff00", 40), "fixed colors are read and invalid port keys are skipped");
Check(lighting.Sfp[9] is { Mode: "color", Effect: "pulse", Period: 6, EffectZone: null } && lighting.Sfp[10].EffectZone == "sfp", "SFP settings are read");
Check(LightingModel.Ranges([7, 1, 2, 3, 4, 9, 10]) == "1–4, 7, 9, 10", "port ranges are summarized");
var (regrouped, newId) = LightingModel.Regroup(lighting, [2, 3, 4], "rainbow", new JsonObject { ["speed"] = 0.2 });
var groups = regrouped.Select(node => node!.AsObject()).ToList();
Check(newId == "selection-2-3-4" && groups.Count == 3 && groups[0]["ports"]!.AsArray().Select(port => port!.GetValue<int>()).SequenceEqual([1, 5, 6, 7, 8])
      && groups[2]["effect"]!["id"]!.GetValue<string>() == "rainbow" && groups[2]["name"]!.GetValue<string>() == "Ports 2–4",
    "selected ports leave their group and form a new one with the chosen effect");
var (whole, _) = LightingModel.Regroup(lighting, [9, 10], "rainbow", new JsonObject());
Check(whole.Count == 2 && whole.All(node => node!["id"]!.GetValue<string>() != "sfp"), "a group whose ports are all selected is replaced");
var crowded = lighting with { Zones = [.. lighting.Zones, new ZoneInfo("selection-2-3-4", "Old", [11], "static", new JsonObject())] };
Check(LightingModel.Regroup(crowded, [2, 3, 4], "static", new JsonObject()).Id == "selection-2-3-4-2", "a new group id never repeats an existing one");

// 1c. Palettes read from the browser workspace's themes.css
Check(EtherlightingEvolved.Native.Theme.ThemeTokens.Themes.Count == 12, "all 12 themes are listed");
foreach (var theme in EtherlightingEvolved.Native.Theme.ThemeTokens.Themes)
{
    var tokens = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve(theme.Id);
    var missing = new[] { "surface", "panel", "raised", "text", "muted", "faint", "line", "accent", "button", "button-text", "chrome", "dialog", "selection-soft" }
        .Where(name => tokens.GetValueOrDefault(name) is not Avalonia.Media.ISolidColorBrush).ToList();
    Check(missing.Count == 0 && tokens.GetValueOrDefault("primary-fill") is Avalonia.Media.ILinearGradientBrush && tokens.GetValueOrDefault("canvas-tint") is Avalonia.Media.IBrush[] { Length: >= 2 }
          && tokens.GetValueOrDefault("shadow") is Avalonia.Media.BoxShadows { Count: 2 },
        $"theme {theme.Id} resolves every token{(missing.Count > 0 ? " (missing " + string.Join(", ", missing) + ")" : "")}");
}
var evolved = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve("evolved");
var midnight = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve("midnight");
Check(evolved["surface.color"] is Avalonia.Media.Color { R: 0x19, G: 0x19, B: 0x1f, A: 255 } && evolved["line.color"] is Avalonia.Media.Color { R: 0xba, G: 0xac, B: 0xda, A: 0x1c },
    "plain and translucent colors are read");
Check(midnight["line.color"] is Avalonia.Media.Color { R: 0xe7, G: 0xef, B: 0xfc, A: 31 } && midnight["button.color"] is Avalonia.Media.Color { R: 0x14, G: 0x24, B: 0x3b },
    "var() and color-mix() resolve as in the browser");

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
    var window = new MainWindow { Width = width, Height = height };
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

// 3b. Lighting controls for a selection, rendered offscreen with a catalog like the engine's
const string catalogJson = """
[{"id":"static","name":"Static","category":"static","description":"One color.","params":[{"key":"color","label":"Color","type":"color","default":"#ffffff"}]},
 {"id":"breathing","name":"Breathing","category":"animated","description":"Fades in and out.","params":[
   {"key":"color","label":"Color","type":"color","default":"#ffffff"},{"key":"period","label":"Period","type":"float","default":4,"min":1,"max":20,"step":0.5,"unit":"s"},
   {"key":"direction","label":"Direction","type":"choice","default":"up","choices":["up","down"]},{"key":"sync","label":"In step","type":"bool","default":true}]}]
""";
IReadOnlyList<EffectInfo> catalog;
using (var document = JsonDocument.Parse(catalogJson)) catalog = LightingModel.Effects(document.RootElement);
Check(catalog.Count == 2 && catalog[1].Params.Count == 4 && catalog[1].Params[1] is { Min: 1, Max: 20, Step: 0.5, Unit: "s" } && catalog[1].Params[2].Choices.Count == 2,
    "the effect catalog and its settings are read");
{
    var window = new MainWindow { Width = 1320, Height = 860 };
    window.Show();
    window.Controls.Bind(new WorkspaceApi(new Uri("http://127.0.0.1:9/")));
    var shown = Synthetic(8, 2);
    window.Apply(new WorkspaceSnapshot("Synthetic 8 + 2", "Live", true, shown, lighting, catalog));
    IEnumerable<string> Labels() => window.Controls.GetLogicalDescendants().OfType<Button>().Select(button => button.Content as string ?? "");
    IEnumerable<string> Texts() => window.Controls.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "");
    Dispatcher.UIThread.RunJobs();
    Check(Texts().Contains("Select ports") && !Labels().Contains("Apply effect"), "with nothing selected the controls ask for a selection");
    window.Select([1, 2, 3, 4, 5, 6, 7, 8]);
    Dispatcher.UIThread.RunJobs();
    var pickers = window.Controls.GetLogicalDescendants().OfType<ComboBox>().ToList();
    Check(Texts().Contains("Ports 1–8") && Texts().Contains("Group: All ports") && Labels().Contains("Apply effect") && Labels().Contains("Apply color"),
        "selecting a whole group shows its effect and fixed color controls");
    Check(pickers.Count > 0 && pickers[0].SelectedItem as string == "Breathing", "the group's current effect is selected");
    Check(window.Controls.GetLogicalDescendants().OfType<CheckBox>().Any(box => box.IsChecked == true)
          && window.Controls.GetLogicalDescendants().OfType<Slider>().Any(slider => slider.Value == 4), "effect settings show the group's saved values");
    var frame1 = window.CaptureRenderedFrame();
    frame1?.Save(Path.Combine(output, "controls-group.png"));
    window.Select([2, 3]);
    Dispatcher.UIThread.RunJobs();
    Check(Texts().Any(text => text.StartsWith("These ports become their own group")), "part of a group is offered as a new group");
    window.Select([9]);
    Dispatcher.UIThread.RunJobs();
    Check(Texts().Contains("SFP 9") && Labels().Contains("Apply SFP lighting") && !Labels().Contains("Apply color"), "an SFP cage shows its indicator settings");
    window.Select([10]);
    Dispatcher.UIThread.RunJobs();
    Check(Texts().Any(text => text.StartsWith("These indicators run their group's effect")), "an SFP cage in a group only offers brightness");
    window.CaptureRenderedFrame()?.Save(Path.Combine(output, "controls-sfp.png"));
    window.Apply(new WorkspaceSnapshot("Synthetic 8 + 2", "Not connected", false, shown, lighting, catalog));
    Dispatcher.UIThread.RunJobs();
    Check(Texts().Any(text => text.StartsWith("Lighting controls are available while EE is connected")), "controls are withheld while EE is not connected");
    window.AllowClose = true;
    window.Close();
}

// 4. Optional: start the simulator from this checkout and change its lighting through the same calls the window uses
if (args.Contains("--service"))
{
    var page = Arg("--page-check");
    if (page is not (null or "Lighting" or "Effects" or "Automation" or "Profiles"))
        throw new ArgumentException("Unknown editing workflow.");
    ShellChecks.ExerciseDesktop(output, Check, page);
    // The API checks below run independently of the UI dispatcher.
    SynchronizationContext.SetSynchronizationContext(null);
    var data = Path.Combine(Path.GetTempPath(), $"ee-native-check-{Environment.ProcessId}");
    var host = ServiceHost.Locate(AppContext.BaseDirectory, true, data);
    Check(host is not null, "the EE service in this checkout is found");
    if (host is not null)
    {
        var address = await host.StartAsync(CancellationToken.None);
        Check(address.Port >= 4190, $"the simulator starts on its own port ({address})");
        using var api = new WorkspaceApi(address);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = address };
        async Task<LightingState> StateAsync()
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync("/api/state"));
            return LightingModel.State(document.RootElement, LightingState.Empty);
        }
        var follower = new WorkspaceClient(address);
        WorkspaceSnapshot? followed = null;
        follower.Changed += snapshot => Volatile.Write(ref followed, snapshot);
        follower.Start();
        async Task<bool> WaitFor(Func<WorkspaceSnapshot, bool> condition)
        {
            for (var i = 0; i < 100; i++)
            {
                if (Volatile.Read(ref followed) is { } snapshot && condition(snapshot)) return true;
                await Task.Delay(100);
            }
            return false;
        }
        Check(await WaitFor(snapshot => snapshot is { Live: true, Lighting: not null, Effects.Count: > 5 } && snapshot.Ports.Count > 0),
            "the window's client follows the simulator with its lighting setup and effect catalog");
        var state = await StateAsync();
        var effect = await api.ApplyEffectAsync(state, [1, 2, 3, 4], "rainbow", new JsonObject { ["speed"] = 0.2 });
        state = await StateAsync();
        var group = state.Zones.FirstOrDefault(zone => zone.Ports.Order().SequenceEqual([1, 2, 3, 4]));
        Check(effect is null && group?.EffectId == "rainbow" && group.Params["speed"]?.GetValue<double>() == 0.2, $"an effect is applied to selected ports ({effect ?? "ok"})");
        var members = state.Zones.SelectMany(zone => zone.Ports).ToList();
        Check(members.Count == members.Distinct().Count() && state.Zones.All(zone => zone.Ports.Count > 0), "no port is left in two groups and no group is empty");
        var again = await api.ApplyEffectAsync(state, [4, 3, 2, 1], "breathing", new JsonObject());
        state = await StateAsync();
        Check(again is null && state.Zones.Count(zone => zone.Ports.Order().SequenceEqual([1, 2, 3, 4])) == 1 && state.ZoneOf(1)?.EffectId == "breathing",
            "an exact group changes its effect in place");
        Check(await WaitFor(snapshot => snapshot.Lighting?.ZoneOf(1)?.EffectId == "breathing"), "the window's client sees the change");
        var color = await api.ApplyColorAsync([5], "#ff8800", 80);
        state = await StateAsync();
        Check(color is null && state.Overrides.GetValueOrDefault(5) == new ManualColor("#ff8800", 80), "a fixed color is applied");
        var follow = await api.FollowEffectAsync([5]);
        state = await StateAsync();
        Check(follow is null && !state.Overrides.ContainsKey(5), "a port follows its group effect again");
        if (state.Sfp.Keys.Order().FirstOrDefault() is var cage and > 0)
        {
            var grouped = state.Sfp[cage].EffectZone is not null;
            var settings = grouped ? new JsonObject { ["brightness"] = 60 }
                : new JsonObject { ["mode"] = "color", ["effect"] = "pulse", ["color"] = "#00ff88", ["brightness"] = 60, ["period"] = 6 };
            var sfp = await api.ApplySfpAsync([cage], settings);
            state = await StateAsync();
            Check(sfp is null && state.Sfp[cage].Brightness == 60 && (grouped || state.Sfp[cage] is { Mode: "color", Effect: "pulse", Color: "#00ff88" }),
                $"SFP {cage} indicator settings are applied ({sfp ?? "ok"})");
        }
        else Check(false, "the simulator reports SFP cages");
        Check(await api.SetBrightnessAsync(55) is null && (await StateAsync()).Brightness == 55, "master brightness is applied");
        Check(await api.SetReleasedAsync(true) is null && (await StateAsync()).Released, "EE lighting pauses");
        Check(await api.SetReleasedAsync(false) is null && !(await StateAsync()).Released, "EE lighting resumes");
        var refused = await api.ApplyEffectAsync(await StateAsync(), [6], "no-such-effect", new JsonObject());
        Check(refused is { Length: > 0 } && !refused.StartsWith("EE is not reachable"), $"a refused change returns EE's message ({refused})");
        await follower.DisposeAsync();
        Check(await host.StopAsync() && !host.Running, "the service stops with confirmed cleanup");
        try { Directory.Delete(data, true); } catch (Exception) { }
    }
}

// 5. Optional: follow a running workspace (read only) and measure this process
if (Arg("--live") is { } live)
{
    var seconds = int.Parse(Arg("--seconds") ?? "60");
    var follower = new WorkspaceClient(new Uri(live));
    var window = new MainWindow { Width = 1200, Height = 660 };
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
