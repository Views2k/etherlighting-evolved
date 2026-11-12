using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages.Lighting;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class LightingPageChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check, string output)
    {
        var w = shell.Workspace;
        if (w.Session.Bool("simulated") != true) throw new InvalidOperationException("Lighting checks require the simulator.");
        await shell.Navigate("Lighting", true);
        var studio = shell.Studio;
        var send = w.WorkspaceRequest();
        async Task<JsonObject> Scene() => (await send("/api/scene", "GET", null)).Obj();
        IEnumerable<Button> Buttons() => shell.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);
        void Click(string label)
        {
            var button = Buttons().First(b => Equals(b.Content, label) && b.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        async Task WaitFor(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            if (!condition()) throw new TimeoutException("Lighting did not reach the expected state.");
        }
        var initial = await Scene(); var original = SecondaryModel.SceneFingerprint(initial["scene"]);
        try
        {
            check(shell.CurrentPage is LightingPage, "the regular workspace opens the native Lighting page");
            var page = (LightingPage)shell.CurrentPage!;
            var bandwidth = page.GetLogicalDescendants().OfType<UplinkBandwidthMeter>().Single();
            var heading = bandwidth.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Uplink bandwidth");
            var window = (Window)TopLevel.GetTopLevel(page)!;
            var windowSize = (window.Width, window.Height);
            var detached = 0;
            void OnDetach(object? sender, LogicalTreeAttachmentEventArgs args) => detached++;
            bandwidth.DetachedFromLogicalTree += OnDetach;
            try
            {
                window.Width = 1300; window.Height = 800; Dispatcher.UIThread.RunJobs();
                var wideRow = Grid.GetRow(bandwidth);
                window.Width = 1000; window.Height = 410; Dispatcher.UIThread.RunJobs();
                page.Update(); page.Frame();
                check(wideRow == 0 && Grid.GetRow(bandwidth) == 3 && heading.IsEffectivelyVisible && detached == 0,
                    "wide and rack layouts keep the same attached bandwidth meter and visible heading");
                RackBandwidth(window, page, bandwidth, output, check);
            }
            finally
            {
                bandwidth.DetachedFromLogicalTree -= OnDetach;
                (window.Width, window.Height) = windowSize; Dispatcher.UIThread.RunJobs();
            }
            Click("None"); check(studio.Selected.Count == 0, "None clears the port selection");
            Click("Ethernet"); var copper = PortTopology.LightingPorts(w.State).Copper;
            check(studio.Selected.SequenceEqual(copper), "Ethernet selects precisely the switch's copper ports");
            check(await studio.ConfirmDraft(), "confirming an unchanged scene proceeds without a dialog");
            if (copper.Count == 0) throw new InvalidOperationException("The simulator requires an Ethernet port.");
            var cfg = EffectRenderer.DefaultConfig("static"); cfg["ports"] = Js.NumberArray(copper.Take(1)); cfg.Obj("params")["color"] = "#3A4BD0";
            await studio.ChooseEffect(cfg);
            var answer = studio.ConfirmDraft();
            await WaitFor(() => shell.Overlay.HasDialog);
            Click("Keep editing");
            check(!await answer && studio.HasEdits, "a later draft still asks for confirmation and Keep editing retains it");
            check(studio.HasEdits && studio.Selected.SequenceEqual(copper.Take(1)) && studio.PreviewScene() is not null, "a selected effect stages a scoped preview");
            check(SecondaryModel.SceneFingerprint((await Scene())["scene"]) == original, "previewing leaves the simulator scene intact");
            Click("Show applied"); check(Buttons().Any(b => Equals(b.Content, "Show preview")), "applied lighting and preview can be compared");
            Click("Show preview"); Click("Discard"); check(!studio.HasEdits, "Discard removes the preview");
            await studio.ChooseEffect(cfg); Click("Apply changes"); await WaitFor(() => !studio.HasEdits && w.Busy == 0);
            var after = await Scene();
            check(SecondaryModel.SceneFingerprint(after["scene"]) != original && studio.CanUndoApply, "Apply commits the preview and enables Undo");
            await studio.UndoLastApply();
            check(SecondaryModel.SceneFingerprint((await Scene())["scene"]) == original, "Undo restores the exact earlier simulator scene");
            foreach (var title in new[] { "Groups", "History", "Saved selections", "Find port", "Port details", "Map key" })
            {
                Click(title); await WaitFor(() => shell.Overlay.HasDialog); check(shell.Overlay.HasDialog, title + " opens in the native Lighting page");
                shell.Overlay.CloseAll();
            }
            var key = copper[0].ToString();
            var namesBefore = (await send("/api/port-names", "GET", null)).Obj();
            try
            {
                Click("Port details"); Click("Edit port names");
                await WaitFor(() => Buttons().Any(b => Equals(b.Content, "Save names") && b.IsEffectivelyEnabled));
                var auto = shell.Overlay.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Use UniFi port names"));
                auto.IsChecked = false;
                var input = shell.Overlay.GetLogicalDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == $"Port {key} name");
                input.Text = "Simulator lighting name"; Dispatcher.UIThread.RunJobs();
                auto.IsChecked = true; Dispatcher.UIThread.RunJobs();
                check(!input.IsEnabled, "UniFi naming displays the controller label on the naming form");
                auto.IsChecked = false; Dispatcher.UIThread.RunJobs();
                check(input.Text == "Simulator lighting name", "manual rename survives switching the naming mode");
                Click("Save names"); await WaitFor(() => !Buttons().Any(b => Equals(b.Content, "Save names")));
                var persisted = (await send("/api/port-names", "GET", null)).Obj();
                check(persisted.Obj("names").Str(key) == "Simulator lighting name" && persisted.Bool("use_unifi") == false, "a name saved in the native editor is returned by the simulator service");
            }
            finally
            {
                shell.Overlay.CloseAll();
                var afterNames = (await send("/api/port-names", "GET", null)).Obj();
                if (!JsonNode.DeepEquals(afterNames["names"], namesBefore["names"]) || afterNames.Bool("use_unifi") != namesBefore.Bool("use_unifi"))
                {
                    await send("/api/port-names", "PUT", new JsonObject { ["expected_revision"] = afterNames["revision"].Copy(), ["use_unifi"] = namesBefore.Bool("use_unifi") ?? false, ["names"] = new JsonObject { [key] = namesBefore.Obj("names").Str(key) ?? "" } });
                    await w.Refresh(false);
                }
            }
            var optical = PortTopology.LightingPorts(w.State).Sfp;
            if (optical.Count > 0)
            {
                await studio.ChooseSfpEffect(new JsonObject { ["effect"] = "pulse" }, [optical[0]]);
                check(studio.Draft.Obj("sfp").Count > 0 && studio.Draft.Obj("sfp").Any(x => x.Value.Str("mode") == "color" && x.Value.Str("effect") == "pulse"), "an optical gallery choice stages the requested SFP color and motion");
                Click("Discard");
            }
        }
        finally
        {
            shell.Overlay.CloseAll();
            if (studio.HasEdits && Buttons().Any(b => Equals(b.Content, "Discard"))) Click("Discard");
            await w.Idle();
            var now = await Scene();
            if (SecondaryModel.SceneFingerprint(now["scene"]) != original)
            {
                await SceneTransaction.CommitScene(now, initial["scene"], send, false); await w.Refresh();
            }
            Click("Ethernet");
        }
    }

    static void RackBandwidth(Window window, LightingPage page, UplinkBandwidthMeter meter, string output, Action<bool, string> check)
    {
        void Resize(int width) { window.Width = width; window.Height = 400; Dispatcher.UIThread.RunJobs(); }
        var plot = meter.GetLogicalDescendants().OfType<BandwidthPlot>().Single();
        var map = meter.GetLogicalAncestors().OfType<ScrollViewer>().First();
        Rect Bounds(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
        Button? HideButton() => page.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b =>
            b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Hide port controls");
        Resize(1276);
        var initiallyOpen = HideButton() is not null;
        var readings = new UplinkMeasurements();
        var history = new JsonArray();
        for (var i = -59; i <= 0; i++)
            history.Add(new JsonArray(i * 1000, (int)(26e6 + 12e6 * Math.Sin(i * .36)), (int)(66e6 + 28e6 * Math.Sin(i * .22))));
        readings.Receive(new JsonObject { ["connected"] = true, ["uplink_bandwidth"] = new JsonObject
        {
            ["status"] = "ready", ["sequence"] = 60, ["ports"] = new JsonArray(9), ["refresh_interval_ms"] = 1000,
            ["sample_period_ms"] = 1000, ["sample_age_ms"] = 0, ["download_bps"] = 66000000, ["upload_bps"] = 26000000,
            ["history"] = history,
        } }, "rack-layout", false, Environment.TickCount64);
        try
        {
            HideButton()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            meter.RenderReading(readings, Environment.TickCount64);
            window.UpdateLayout();
            var full = Bounds(plot);
            var viewport = Bounds(map);
            var labels = meter.GetLogicalDescendants().OfType<TextBlock>().ToArray();
            var peaks = labels.Where(t => t.Text?.StartsWith("Peak ", StringComparison.Ordinal) == true).ToArray();
            var metrics = meter.GetLogicalDescendants().OfType<Grid>().Single(g => g.Width == 270);
            check(full.Width > viewport.Width * .6 && viewport.Right - full.Right is >= 4 and <= 24,
                "the 1276 by 400 rack plot fills the width beside the two rates and reaches the map's padded right edge");
            var controls = labels.Where(t => !string.IsNullOrEmpty(t.Text) && t.Text != "Last minute").Cast<Control>()
                .Concat(page.GetLogicalDescendants().OfType<PortTile>()).Append(map)
                .Append(page.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text == "Default brightness")).ToArray();
            var positions = controls.Select(Bounds).ToArray();
            // Compare the same window and readings against the original fixed-width graph.
            var (alignment, column, width) = (meter.HorizontalAlignment, meter.ColumnDefinitions[1].Width, plot.Width);
            try
            {
                meter.HorizontalAlignment = HorizontalAlignment.Left;
                meter.ColumnDefinitions[1].Width = GridLength.Auto;
                plot.Width = 175;
                window.UpdateLayout();
                var original = Bounds(plot);
                check(metrics.Bounds.Width == 270 && peaks.Length == 2 && positions.SequenceEqual(controls.Select(Bounds)) &&
                    full.Position == original.Position && full.Height == original.Height && full.Width > original.Width,
                    "only the plot width changes from the original rack layout; the labels, peak rows, faceplate, dock and viewport keep their geometry");
            }
            finally
            {
                meter.HorizontalAlignment = alignment;
                meter.ColumnDefinitions[1].Width = column;
                plot.Width = width;
                window.UpdateLayout();
            }
            using (var frame = window.CaptureRenderedFrame())
            {
                check(frame is not null, "the native rack graph renders in the full Lighting window");
                if (frame is not null)
                {
                    frame.Save(Path.Combine(output, "rack-bandwidth-1276x400.png"));
                    var visible = full.Intersect(viewport);
                    CheckTracePixels(frame, visible, window, check);
                }
            }
            Resize(1040);
            var smaller = Bounds(plot);
            check(full.Width - smaller.Width > 200 && Bounds(map).Right - smaller.Right is >= 4 and <= 24,
                "the rack graph follows successive window widths without overflowing the map");
            Resize(1276); page.RevealEditor(); Dispatcher.UIThread.RunJobs();
            var withInspector = Bounds(plot);
            var openMap = Bounds(map);
            check(HideButton() is not null && full.Width - withInspector.Width > 265 && withInspector.Width > 400 &&
                openMap.Right - withInspector.Right is >= 4 and <= 24,
                "opening the inspector constrains the graph to the available map width with the same right padding");
        }
        finally
        {
            if (!initiallyOpen) HideButton()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            meter.RenderReading(page.Shell.Workspace.Bandwidth, Environment.TickCount64);
        }
    }

    static void CheckTracePixels(WriteableBitmap frame, Rect graph, Window window, Action<bool, string> check)
    {
        using var buffer = frame.Lock();
        var sx = buffer.Size.Width / window.Bounds.Width;
        var sy = buffer.Size.Height / window.Bounds.Height;
        var x0 = Math.Clamp((int)Math.Ceiling(graph.X * sx), 0, buffer.Size.Width);
        var x1 = Math.Clamp((int)Math.Floor(graph.Right * sx), x0, buffer.Size.Width);
        var y0 = Math.Clamp((int)Math.Ceiling(graph.Y * sy), 0, buffer.Size.Height);
        var y1 = Math.Clamp((int)Math.Floor(graph.Bottom * sy), y0, buffer.Size.Height);
        var blue = new bool[x1 - x0];
        var pink = new bool[x1 - x0];
        var rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        for (var y = y0; y < y1; ++y)
        for (var x = x0; x < x1; ++x)
        {
            var pixel = Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + 4 * x);
            var red = rgba ? (byte)pixel : (byte)(pixel >> 16);
            var green = (byte)(pixel >> 8);
            var b = rgba ? (byte)(pixel >> 16) : (byte)pixel;
            blue[x - x0] |= b - red > 35 && green - red > 18;
            pink[x - x0] |= red - green > 35 && red - b > 14;
        }
        static bool Spans(bool[] columns) =>
            columns.Count(x => x) > columns.Length * .75 && Array.IndexOf(columns, true) <= columns.Length * .08 &&
            Array.LastIndexOf(columns, true) >= columns.Length * .94;
        check(Spans(blue) && Spans(pink),
            $"both rack traces span the visible graph ({blue.Count(x => x)} download and {pink.Count(x => x)} upload columns across {x1 - x0} × {y1 - y0} pixels)");
    }
}
