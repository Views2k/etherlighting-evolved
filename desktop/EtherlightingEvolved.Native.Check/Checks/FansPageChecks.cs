using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The Fans page against the demo switch: live readings, each mode, the curves and both sliders.</summary>
public static class FansPageChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        if (!shell.Workspace.Api.BaseUri.IsLoopback || shell.Workspace.Session.Bool("simulated") != true)
            throw new InvalidOperationException("These fan checks require a local demo switch.");
        Control Page() => shell.CurrentPage ?? throw new InvalidOperationException("No workspace page");
        static IEnumerable<T> All<T>(Control root) where T : Control => root.GetLogicalDescendants().OfType<T>();
        static string? Label(Button button) => button.Content as string
            ?? (button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
        static Button? Find(Control root, string label) => All<Button>(root).FirstOrDefault(b => Label(b) == label && b.IsEffectivelyVisible);
        static async Task<bool> Until(Func<Task<bool>> predicate, double seconds = 8)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (await predicate()) return true;
                await Task.Delay(50);
            }
            return false;
        }
        async Task<JsonNode?> Fans() => await shell.Workspace.Request("/api/fans");
        async Task<bool> Click(string label)
        {
            if (!await Until(() => Task.FromResult(Find(Page(), label)?.IsEffectivelyEnabled == true))) return false;
            Find(Page(), label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return true;
        }
        bool Shows(string prefix) => All<TextBlock>(Page()).Any(text => text.IsEffectivelyVisible && (text.Text ?? "").StartsWith(prefix, StringComparison.Ordinal));

        await shell.Navigate("Fans");
        check(await Until(() => Task.FromResult(Shows("Switch control") && All<TextBlock>(Page()).Any(t => (t.Text ?? "").EndsWith(" °C", StringComparison.Ordinal)))),
            "Fans shows the switch temperatures and the fan modes");

        check(await Click("Thermal target") && await Until(async () => (await Fans()).Str("settings.mode") == "target"),
            "Thermal target starts fan control on the switch");
        var described = await Until(() => Task.FromResult(Shows("Keeping the CPU at 55 °C")));
        check(described, "the page describes the thermal target it holds"
                         + (described ? "" : $" ({string.Join(" | ", All<TextBlock>(Page()).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).Where(t => t?.Length > 20))})"));
        // The page swaps in each mode's slider after the change returns; wait for the one this mode uses.
        Slider? Visible(double minimum) => All<Slider>(Page()).FirstOrDefault(slider => slider.IsEffectivelyVisible && slider.Minimum == minimum);
        check(await Until(() => Task.FromResult(Visible(45) is not null)), "Thermal target shows the temperature slider");
        var target = Visible(45)!;
        target.Value = 61;
        check(await Until(async () => (await Fans()).Int("settings.target_celsius") == 61), "moving the target slider sets the target temperature");

        // A choice shows at once; the switch confirms it in the background.
        var current = (await Fans())?["reading"]?["fans"]?[0].Int("percent") ?? 0;
        Find(Page(), "Manual")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(Find(Page(), "Manual")!.Classes.Contains("selected") && Shows("Sending the new setting"),
            "a chosen mode shows as selected before the switch answers");
        check(await Until(async () => (await Fans()).Str("settings.mode") == "manual"), "Manual holds a fixed fan speed");
        check((await Fans()).Int("settings.manual_percent") == Math.Clamp(current, 30, 100),
            $"choosing Manual keeps the fans at their current speed ({current}%) instead of the last saved one");
        check(await Until(() => Task.FromResult(Visible(30) is not null)), "Manual shows the speed slider");
        var speed = Visible(30)!;
        speed.Value = 82;
        check(await Until(async () => (await Fans()) is { } fans && fans.Int("settings.manual_percent") == 82
                                       && fans["reading"]?["fans"]?.AsArray().All(fan => fan.Int("percent") == 82) == true),
            "moving the speed slider sets both fans");
        check(await Until(() => Task.FromResult(Shows("82%"))), "the fan reading follows the new speed");

        check(await Click("Curve") && await Click("High") && await Until(async () => (await Fans()) is { } fans
                                                                    && fans.Str("settings.mode") == "curve" && fans.Str("settings.curve") == "high"),
            "a fan curve is chosen from the four curves");
        check(await Until(() => Task.FromResult(Shows("Following the High curve"))), "the page names the curve the fans follow");

        var refused = await shell.Workspace.Api.RequestAsync("/api/fans", "PUT", new JsonObject { ["mode"] = "manual", ["manual_percent"] = 12 }, shell.Workspace.WorkspaceId)
            .ContinueWith(task => task.Exception?.InnerException as ApiException);
        check(refused is { Status: 400 } && (await Fans()).Str("settings.curve") == "high", "a speed below 30% is refused and the curve stays");

        // A 1280 × 400 rack window: everything fits without scrolling, with large controls that work.
        if (TopLevel.GetTopLevel(shell) is Window window)
        {
            var (width, height) = (window.Width, window.Height);
            try
            {
                // Curve mode shows 4 mode and 4 curve buttons; Manual shows the 4 mode buttons with − and +.
                window.Width = 1280; window.Height = 400;
                bool Fits(int buttonsShown)
                {
                    var root = Page();
                    var scrolling = All<ScrollViewer>(root).Any(s => s.IsEffectivelyVisible && s.Extent.Height > s.Viewport.Height + 0.5);
                    var buttons = All<Button>(root).Where(b => b.IsEffectivelyVisible).ToList();
                    var inside = buttons.All(b => b.TranslatePoint(new Point(0, b.Bounds.Height), root) is { } bottom && bottom.Y <= root.Bounds.Height + 0.5);
                    return !scrolling && buttons.Count >= buttonsShown && inside && Find(root, "Manual")?.Bounds.Height >= 44;
                }
                check(await Until(() => Task.FromResult(Fits(8))), "in a 1280 × 400 rack window Fans fills the space with large controls and does not scroll");
                check(await Click("Manual") && await Until(() => Task.FromResult(Visible(30) is not null && Find(Page(), "+") is not null)),
                    "the rack layout shows the speed slider with − and + buttons");
                var shown = (int)Visible(30)!.Value;
                await Until(async () => (await Fans()) is { } fans && fans.Str("settings.mode") == "manual" && fans.Int("settings.manual_percent") == shown);
                check(await Click("+") && await Until(async () => (await Fans()).Int("settings.manual_percent") == Math.Min(100, shown + 5)),
                    "+ raises the fan speed by 5%");
                check(await Until(() => Task.FromResult(Fits(6))), "the rack layout still fits with the slider shown");

                // Pointer input stays cheap: switching between touch and mouse does no work across the window.
                var plus = Find(Page(), "+")!;
                void Press(PointerType type)
                {
                    var pointer = new Pointer(Pointer.GetNextFreeId(), type, true);
                    var properties = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
                    window.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, plus, pointer, window, new Point(10, 10), 0, properties, KeyModifiers.None));
                }
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 1000; i++) Press(i % 2 == 0 ? PointerType.Touch : PointerType.Mouse);
                check(clock.ElapsedMilliseconds < 250, $"1,000 alternating touch and mouse events take {clock.ElapsedMilliseconds} ms");

                // A tap that arrives as a mouse click leaves the pointer in place, so the rack window shows no hover
                // tooltips: the tooltip service is off for the window and every control in it.
                check(!ToolTip.GetServiceEnabled(window) && !ToolTip.GetServiceEnabled(plus),
                    $"a rack-sized window shows no tooltips (window {ToolTip.GetServiceEnabled(window)}, control {ToolTip.GetServiceEnabled(plus)}, client {window.ClientSize})");
            }
            finally
            {
                window.Width = width; window.Height = height;
                await Until(() => Task.FromResult(ToolTip.GetServiceEnabled(window)));
                check(ToolTip.GetServiceEnabled(window), "a full-size window shows tooltips again");
            }
        }
        else check(false, "the Fans checks find their window");

        check(await Click("Switch control") && await Until(async () => (await Fans()) is { } fans
                                                                       && fans.Str("settings.mode") == "switch" && fans.Str("reading.controller") == "absent"),
            "Switch control returns the fans to the switch");
    }
}
